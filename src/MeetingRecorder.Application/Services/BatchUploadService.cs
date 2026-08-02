using System.Collections.Concurrent;
using AutoMapper;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain;
using MeetingRecorder.Domain.Entities;

namespace MeetingRecorder.Application.Services;

public interface IBatchUploadService
{
    Task<StartUploadResponse> StartUploadAsync(StartUploadRequest request, CancellationToken ct = default);
    Task<UploadStatusResponse> UploadChunkAsync(UploadChunkRequest request, Stream chunkContent, CancellationToken ct = default);
    Task<UploadStatusResponse> GetUploadStatusAsync(Guid batchId, CancellationToken ct = default);
    Task<UploadStatusResponse> RetryChunkAsync(Guid batchId, int chunkNumber, CancellationToken ct = default);
    Task<RecordingResponse> CompleteUploadAsync(Guid batchId, CancellationToken ct = default);
}

public class BatchUploadService : IBatchUploadService
{
    // Serializes merge per batch so two "complete" calls cannot race on the same batch.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> BatchLocks = new();

    private readonly IUnitOfWork _uow;
    private readonly IChunkStorageService _chunkStorage;
    private readonly IMapper _mapper;

    public BatchUploadService(IUnitOfWork uow, IChunkStorageService chunkStorage, IMapper mapper)
    {
        _uow = uow;
        _chunkStorage = chunkStorage;
        _mapper = mapper;
    }

    public async Task<StartUploadResponse> StartUploadAsync(StartUploadRequest request, CancellationToken ct = default)
    {
        var userExists = await _uow.Repository<User>().AnyAsync(u => u.Id == request.UserId && !u.IsDeleted, ct);
        if (!userExists)
            throw new NotFoundException(nameof(User), request.UserId);

        var batch = new UploadBatch
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            FileName = Path.GetFileName(request.FileName.Trim()),   // strip any path segments
            Type = request.Type,
            SourceLanguageCode = request.SourceLanguageCode,
            TotalChunks = request.TotalChunks,
            Status = UploadStatus.InProgress
        };

        _uow.Repository<UploadBatch>().Add(batch);
        await _uow.SaveChangesAsync(ct);

        return new StartUploadResponse(batch.Id, batch.TotalChunks, $"chunks/{batch.Id}");
    }

    public async Task<UploadStatusResponse> UploadChunkAsync(UploadChunkRequest request, Stream chunkContent, CancellationToken ct = default)
    {
        var batch = await GetActiveBatchAsync(request.BatchId, request.UserId, ct);

        if (request.TotalChunks != batch.TotalChunks)
            throw new AppException($"TotalChunks ({request.TotalChunks}) does not match the batch ({batch.TotalChunks}).");

        if (request.ChunkNumber < 1 || request.ChunkNumber > batch.TotalChunks)
            throw new AppException($"ChunkNumber must be between 1 and {batch.TotalChunks}.");

        if (chunkContent.Length <= 0)
            throw new AppException("Chunk payload is empty.");

        await _chunkStorage.SaveChunkAsync(batch.Id, request.ChunkNumber, chunkContent, ct);

        // Upsert the chunk registry row (retries simply overwrite the same row).
        var repo = _uow.Repository<UploadChunk>();
        var existing = await repo.FirstOrDefaultAsync(c => c.UploadBatchId == batch.Id && c.ChunkNumber == request.ChunkNumber, ct);
        var bytesDelta = chunkContent.Length - (existing?.SizeBytes ?? 0);
        if (existing is null)
        {
            repo.Add(new UploadChunk
            {
                UploadBatchId = batch.Id,
                ChunkNumber = request.ChunkNumber,
                SizeBytes = chunkContent.Length,
                UploadedAt = request.UploadedAt ?? DateTime.UtcNow
            });
        }
        else
        {
            existing.SizeBytes = chunkContent.Length;
            existing.UploadedAt = request.UploadedAt ?? DateTime.UtcNow;
            repo.Update(existing);
        }

        // Chunk metadata (summary/transcript/actions/notes) — keep the latest values on the batch.
        batch.Summary = request.Summary ?? batch.Summary;
        batch.Transcript = request.Transcript ?? batch.Transcript;
        batch.Actions = request.Actions ?? batch.Actions;
        batch.Notes = request.Notes ?? batch.Notes;
        batch.TotalBytesReceived += bytesDelta;
        _uow.Repository<UploadBatch>().Update(batch);

        await _uow.SaveChangesAsync(ct);
        return await GetUploadStatusAsync(batch.Id, ct);
    }

    public async Task<UploadStatusResponse> GetUploadStatusAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await _uow.Repository<UploadBatch>().FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new NotFoundException(nameof(UploadBatch), batchId);

        var chunks = _uow.Repository<UploadChunk>().Query()
            .Where(c => c.UploadBatchId == batchId)
            .OrderBy(c => c.ChunkNumber)
            .ToList();

        var received = chunks.Select(c => c.ChunkNumber).ToList();
        var complete = batch.Status == UploadStatus.Completed
            || (received.Count == batch.TotalChunks && received.Distinct().Count() == batch.TotalChunks);

        return new UploadStatusResponse(batch.Id, batch.UserId, batch.FileName, batch.TotalChunks,
            received, complete, batch.Status.ToString(), batch.TotalBytesReceived);
    }

    public async Task<UploadStatusResponse> RetryChunkAsync(Guid batchId, int chunkNumber, CancellationToken ct = default)
    {
        var batch = await _uow.Repository<UploadBatch>().FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new NotFoundException(nameof(UploadBatch), batchId);

        if (chunkNumber < 1 || chunkNumber > batch.TotalChunks)
            throw new AppException($"ChunkNumber must be between 1 and {batch.TotalChunks}.");

        // Drop the failed/missing chunk so the client can re-upload it via POST /upload/chunk.
        var chunkRepo = _uow.Repository<UploadChunk>();
        var existing = await chunkRepo.FirstOrDefaultAsync(c => c.UploadBatchId == batchId && c.ChunkNumber == chunkNumber, ct);
        if (existing is not null)
        {
            chunkRepo.Remove(existing);
            await _uow.SaveChangesAsync(ct);
        }

        return await GetUploadStatusAsync(batchId, ct);
    }

    public async Task<RecordingResponse> CompleteUploadAsync(Guid batchId, CancellationToken ct = default)
    {
        var gate = BatchLocks.GetOrAdd(batchId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var batch = await _uow.Repository<UploadBatch>().FirstOrDefaultAsync(b => b.Id == batchId, ct)
                ?? throw new NotFoundException(nameof(UploadBatch), batchId);

            if (batch.Status == UploadStatus.Completed)
                throw new AppException("This upload has already been completed.", 409);

            var chunks = _uow.Repository<UploadChunk>().Query()
                .Where(c => c.UploadBatchId == batchId)
                .OrderBy(c => c.ChunkNumber)
                .ToList();

            // 1. Validate chunk count / completeness
            var received = chunks.Select(c => c.ChunkNumber).Distinct().OrderBy(n => n).ToList();
            var expected = Enumerable.Range(1, batch.TotalChunks).ToList();
            if (!received.SequenceEqual(expected))
            {
                var missing = expected.Except(received).ToList();
                throw new AppException($"Upload incomplete. Missing chunk(s): {string.Join(", ", missing)}.");
            }

            // 2. Merge in order (all chunk files must exist on disk)
            batch.Status = UploadStatus.Merging;
            _uow.Repository<UploadBatch>().Update(batch);
            await _uow.SaveChangesAsync(ct);

            var ext = Path.GetExtension(batch.FileName);
            var finalPath = await _chunkStorage.MergeChunksAsync(batch.Id, batch.TotalChunks, $"{batch.Id}{ext}", ct);

            // 3. Save final recording
            var recording = new Recording
            {
                UserId = batch.UserId,
                Title = Path.GetFileNameWithoutExtension(batch.FileName),
                Type = batch.Type,
                CreatedAt = DateTime.UtcNow,
                Duration = TimeSpan.Zero,
                Summary = batch.Summary,
                Transcript = batch.Transcript,
                Actions = batch.Actions,
                Notes = batch.Notes,
                IsRecording = false,
                Bookmarked = false,
                FilePath = finalPath,
                SourceLanguageCode = batch.SourceLanguageCode,
                TranscriptionStatus = TranscriptionStatus.Pending
            };
            _uow.Repository<Recording>().Add(recording);

            // 4. Delete temporary chunks (rows + files)
            foreach (var chunk in chunks)
                _uow.Repository<UploadChunk>().Remove(chunk);

            // 5. Mark upload completed
            batch.Status = UploadStatus.Completed;
            batch.UpdatedDate = DateTime.UtcNow;
            _uow.Repository<UploadBatch>().Update(batch);

            await _uow.SaveChangesAsync(ct);
            await _chunkStorage.DeleteBatchAsync(batch.Id, ct);

            return _mapper.Map<RecordingResponse>(recording);
        }
        catch
        {
            var batch = await _uow.Repository<UploadBatch>().FirstOrDefaultAsync(b => b.Id == batchId, ct);
            if (batch is { Status: UploadStatus.Merging })
            {
                batch.Status = UploadStatus.Failed;
                batch.UpdatedDate = DateTime.UtcNow;
                _uow.Repository<UploadBatch>().Update(batch);
                await _uow.SaveChangesAsync(ct);
            }
            throw;
        }
        finally
        {
            gate.Release();   // always release the per-batch lock, success or failure
        }
    }

    private async Task<UploadBatch> GetActiveBatchAsync(Guid batchId, Guid userId, CancellationToken ct)
    {
        var batch = await _uow.Repository<UploadBatch>().FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new NotFoundException(nameof(UploadBatch), batchId);

        if (batch.UserId != userId)
            throw new AppException("This batch does not belong to the given user.", 403);

        if (batch.Status != UploadStatus.InProgress)
            throw new AppException($"Upload is not accepting chunks (status: {batch.Status}).");

        return batch;
    }
}
