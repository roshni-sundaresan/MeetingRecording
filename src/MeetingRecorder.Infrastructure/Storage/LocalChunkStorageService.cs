using MeetingRecorder.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingRecorder.Infrastructure.Storage;

public class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root directory for chunk temp files and merged recordings.</summary>
    public string RootPath { get; set; } = "uploads";

    public string ChunkDirectory => Path.Combine(RootPath, "chunks");
    public string RecordingDirectory => Path.Combine(RootPath, "recordings");
}

/// <summary>
/// Local-disk implementation of chunk storage. Every path is derived from a GUID batch id
/// and an integer chunk number — no user-supplied strings are ever used in file paths,
/// which makes it immune to path-traversal attacks.
/// </summary>
public class LocalChunkStorageService : IChunkStorageService
{
    private readonly StorageOptions _options;
    private readonly ILogger<LocalChunkStorageService> _logger;

    public LocalChunkStorageService(IOptions<StorageOptions> options, ILogger<LocalChunkStorageService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    private string BatchDir(Guid batchId) => Path.Combine(_options.ChunkDirectory, batchId.ToString("N"));
    private string ChunkPath(Guid batchId, int chunkNumber) => Path.Combine(BatchDir(batchId), $"chunk_{chunkNumber:D6}.part");

    public Task SaveChunkAsync(Guid batchId, int chunkNumber, Stream content, CancellationToken ct = default)
    {
        var dir = BatchDir(batchId);
        Directory.CreateDirectory(dir);

        var path = ChunkPath(batchId, chunkNumber);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        return content.CopyToAsync(file, ct);
    }

    public Task<bool> ChunkExistsAsync(Guid batchId, int chunkNumber, CancellationToken ct = default)
        => Task.FromResult(File.Exists(ChunkPath(batchId, chunkNumber)));

    public Task<Stream> OpenChunkAsync(Guid batchId, int chunkNumber, CancellationToken ct = default)
        => Task.FromResult<Stream>(new FileStream(ChunkPath(batchId, chunkNumber), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

    public async Task<string> MergeChunksAsync(Guid batchId, int totalChunks, string targetFileName, CancellationToken ct = default)
    {
        var dir = BatchDir(batchId);
        var recordingDir = Path.Combine(_options.RecordingDirectory, batchId.ToString("N"));
        Directory.CreateDirectory(recordingDir);

        var target = Path.Combine(recordingDir, Path.GetFileName(targetFileName));
        var sourceChunks = Enumerable.Range(1, totalChunks)
            .Select(n => ChunkPath(batchId, n))
            .ToList();

        foreach (var chunkPath in sourceChunks)
        {
            if (!File.Exists(chunkPath))
                throw new FileNotFoundException($"Chunk file missing during merge: {chunkPath}");
        }

        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            foreach (var chunkPath in sourceChunks)
            {
                await using var input = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await input.CopyToAsync(output, ct);
            }
        }

        _logger.LogInformation("Merged {Count} chunks into {Target} ({Size} bytes)", totalChunks, target, new FileInfo(target).Length);
        return target;
    }

    public Task DeleteBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        var dir = BatchDir(batchId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Deleted temporary chunk directory {Dir}", dir);
        }
        return Task.CompletedTask;
    }
}
