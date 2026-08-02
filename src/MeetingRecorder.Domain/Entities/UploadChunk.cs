namespace MeetingRecorder.Domain.Entities;

public class UploadChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UploadBatchId { get; set; }
    public int ChunkNumber { get; set; }
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }

    public UploadBatch? UploadBatch { get; set; }
}
