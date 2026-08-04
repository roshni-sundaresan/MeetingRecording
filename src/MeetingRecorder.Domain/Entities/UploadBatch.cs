using MeetingRecorder.Domain.Common;

namespace MeetingRecorder.Domain.Entities;

/// <summary>
/// Tracks a chunked (resumable) upload batch. Chunk binary payloads are stored
/// on disk by <see cref="MeetingRecorder.Application.Interfaces.IChunkStorageService"/>;
/// this row records metadata + which chunks have arrived.
/// </summary>
public class UploadBatch : BaseEntity
{
    public Guid UserId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public RecordingType Type { get; set; }
    public string? SourceLanguageCode { get; set; }
    public int TotalChunks { get; set; }
    public UploadStatus Status { get; set; } = UploadStatus.InProgress;
    public long TotalBytesReceived { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public string? Summary { get; set; }
    public string? Transcript { get; set; }
    public string? Actions { get; set; }
    public string? Notes { get; set; }

    public User? User { get; set; }
    public ICollection<UploadChunk> Chunks { get; set; } = new List<UploadChunk>();
}
