namespace MeetingRecorder.Domain.Common;

/// <summary>Base class for all entities with audit + soft-delete fields.</summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedDate { get; set; }
    public bool IsDeleted { get; set; }
}
