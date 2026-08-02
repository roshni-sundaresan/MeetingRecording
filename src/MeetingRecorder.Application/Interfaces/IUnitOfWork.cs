namespace MeetingRecorder.Application.Interfaces;

/// <summary>Unit of Work over the application DbContext. Commits all tracked changes atomically.</summary>
public interface IUnitOfWork : IDisposable
{
    IRepository<T> Repository<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task ExecuteInTransactionAsync(Func<Task> action, CancellationToken ct = default);
}
