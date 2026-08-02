namespace MeetingRecorder.Application.Interfaces;

/// <summary>
/// Persists/reads raw chunk binaries on disk and merges them into the final file.
/// Implementations must be path-traversal safe (keys are GUID batch ids + ints).
/// </summary>
public interface IChunkStorageService
{
    Task SaveChunkAsync(Guid batchId, int chunkNumber, Stream content, CancellationToken ct = default);
    Task<bool> ChunkExistsAsync(Guid batchId, int chunkNumber, CancellationToken ct = default);
    Task<Stream> OpenChunkAsync(Guid batchId, int chunkNumber, CancellationToken ct = default);
    Task<string> MergeChunksAsync(Guid batchId, int totalChunks, string targetFileName, CancellationToken ct = default);
    Task DeleteBatchAsync(Guid batchId, CancellationToken ct = default);
}
