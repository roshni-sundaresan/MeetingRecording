using System.Collections.Concurrent;
using System.Security.Cryptography;
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
    Task<UploadStatusResponse> GetUploadStatusAsync(Guid batchId, Guid? requesterUserId, CancellationToken ct = default);
    Task<UploadStatusResponse> RetryChunkAsync(Guid batchId, int chunkNumber, Guid? requesterUserId, CancellationToken ct = default);
    Task<RecordingResponse> CompleteUploadAsync(Guid batchId, Guid? requesterUserId, CancellationToken ct = default);
}

public class BatchUploadService : IBatchUploadService
{
    // Serializes merge per batch so two "complete" calls cannot race on the same batch.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> BatchLocks = new();

    public const int MaxChunksPerBatch = 512;
    public const long MaxFileSizeBytes = 2L * 1024 * 1024 * 1024;          // 2 GB declared total
    public const long MaxChunkSizeBytes = 60L * 1024 * 1024;               // 60 MB per chunk

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
        // ---- input caps (defense in depth; validators mirror these) ----
        if (request.TotalChunks is < 1 or > MaxChunksPerBatch)
            throw new AppException($"TotalChunks must be between 1 and {MaxChunksPerBatch}.", 400, "VALIDATION_ERROR");
        if (request.FileSizeBytes is > MaxFileSizeBytes)
            throw new AppException($"File size must not exceed {MaxFileSizeBytes / (1024 * 1024 * 1024)} GB.", 400, "VALIDATION_ERROR");

        var fileName = Path.GetFileName(request.FileName.Trim());   // strip any path segments
        if (string.IsNullOrWhiteSpace(fileName))
            throw new AppException("file_name must not be empty.", 400, "VALIDATION_ERROR");
        if (fileName.Length > 255)
            throw new AppException("file_name must not exceed 255 characters.", 400, "VALIDATION_ERROR");

        var userExists = await _uow.Repository<User>().AnyAsync(u => u.Id == request.UserId && !u.IsDeleted, ct);
        if (!userExists)
            throw new NotFoundException(nameof(User), request.UserId);

        var batch = new UploadBatch
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            FileName = fileName,
            Type = request.Type,
            SourceLanguageCode = request.SourceLanguageCode,
            TotalChunks = request.TotalChunks,
            Duration = request.Duration ?? (request.DurationSeconds is int ds ? TimeSpan.FromSeconds(ds) : TimeSpan.Zero),
            Status = UploadStatus.InProgress
        };

        _uow.Repository<UploadBatch>().Add(batch);
        await _uow.SaveChangesAsync(ct);

        return new StartUploadResponse(batch.Id, batch.TotalChunks);
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

        if (chunkContent.Length > MaxChunkSizeBytes)
            throw new AppException($"Chunk must not exceed {MaxChunkSizeBytes / (1024 * 1024)} MB.", 400, "VALIDATION_ERROR");

        // Buffer the chunk so the declared checksum can be verified before it
        // is written to storage.
        await using var buffer = new MemoryStream();
        await chunkContent.CopyToAsync(buffer, ct);
        if (buffer.Length <= 0)
            throw new AppException("Chunk payload is empty.");

        if (!string.IsNullOrWhiteSpace(request.ChecksumSha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
            if (!actual.Equals(request.ChecksumSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                throw new AppException("Chunk checksum mismatch (sha256).", 400, "CHECKSUM_MISMATCH");
        }

        buffer.Position = 0;   // always rewind before handing to storage
        await _chunkStorage.SaveChunkAsync(batch.Id, request.ChunkNumber, buffer, ct);

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
        return await GetUploadStatusAsync(batch.Id, request.UserId, ct);
    }

    public async Task<UploadStatusResponse> GetUploadStatusAsync(Guid batchId, Guid? requesterUserId, CancellationToken ct = default)
    {
        var batch = await _uow.Repository<UploadBatch>().FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new NotFoundException(nameof(UploadBatch), batchId);

        EnsureBatchOwnership(batch, requesterUserId);

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

    public async Task<UploadStatusResponse> RetryChunkAsync(Guid batchId, int chunkNumber, Guid? requesterUserId, CancellationToken ct = default)
    {
        var batch = await _uow.Repository<UploadBatch>().FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new NotFoundException(nameof(UploadBatch), batchId);

        EnsureBatchOwnership(batch, requesterUserId);

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

        return await GetUploadStatusAsync(batchId, requesterUserId, ct);
    }

    public async Task<RecordingResponse> CompleteUploadAsync(Guid batchId, Guid? requesterUserId, CancellationToken ct = default)
    {
        var gate = BatchLocks.GetOrAdd(batchId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var batch = await _uow.Repository<UploadBatch>().FirstOrDefaultAsync(b => b.Id == batchId, ct)
                ?? throw new NotFoundException(nameof(UploadBatch), batchId);

            EnsureBatchOwnership(batch, requesterUserId);

            if (batch.Status == UploadStatus.Completed)
                throw new AppException("This upload has already been completed.", 409, "UPLOAD_ALREADY_COMPLETED");

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
                Duration = batch.Duration,
                Summary = batch.Summary,
                Transcript = batch.Transcript,
                Actions = batch.Actions,
                Notes = batch.Notes,
                IsRecording = false,
                Bookmarked = false,
                FilePath = finalPath,
                SourceLanguageCode = batch.SourceLanguageCode,
                TranscriptionStatus = TranscriptionStatus.None
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

    /// <summary>Non-admin callers may only touch their own batches. A null
    /// requester means an admin bypass.</summary>
    private static void EnsureBatchOwnership(UploadBatch batch, Guid? requesterUserId)
    {
        if (requesterUserId.HasValue && batch.UserId != requesterUserId.Value)
            throw new AppException("You do not have permission to access this upload batch.", 403);
    }
}
