using AutoMapper;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Application.Validators;
using MeetingRecorder.Domain;
using MeetingRecorder.Domain.Entities;

namespace MeetingRecorder.Application.Services;

public interface IRecordingService
{
    Task<PagedResult<RecordingResponse>> GetRecordingsAsync(Guid? userId, QueryParameters query, CancellationToken ct = default);
    Task<RecordingResponse> GetRecordingAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<RecordingResponse>> GetRecordingsByIdsAsync(Guid? requesterUserId, IReadOnlyList<Guid> ids, CancellationToken ct = default);
    Task<RecordingResponse> CreateRecordingAsync(CreateRecordingRequest request, CancellationToken ct = default);
    Task<RecordingResponse> UpdateRecordingAsync(Guid id, UpdateRecordingRequest request, CancellationToken ct = default);
    Task<RecordingResponse> SetBookmarkAsync(Guid id, bool bookmarked, CancellationToken ct = default);
    Task DeleteRecordingAsync(Guid id, CancellationToken ct = default);
}

public class RecordingService : IRecordingService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;

    public RecordingService(IUnitOfWork uow, IMapper mapper)
    {
        _uow = uow;
        _mapper = mapper;
    }

    public Task<PagedResult<RecordingResponse>> GetRecordingsAsync(Guid? userId, QueryParameters query, CancellationToken ct = default)
    {
        var repo = _uow.Repository<Recording>();
        var baseQuery = repo.Query().Where(r => !r.IsDeleted);

        if (userId.HasValue)
            baseQuery = baseQuery.Where(r => r.UserId == userId.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            baseQuery = baseQuery.Where(r =>
                r.Title.Contains(term) || (r.Summary != null && r.Summary.Contains(term))
                || (r.Transcript != null && r.Transcript.Contains(term)));
        }

        var total = baseQuery.Count();

        var sorted = (query.SortBy?.ToLowerInvariant()) switch
        {
            "title" => query.SortOrder == "desc" ? baseQuery.OrderByDescending(r => r.Title) : baseQuery.OrderBy(r => r.Title),
            "duration" => query.SortOrder == "desc" ? baseQuery.OrderByDescending(r => r.Duration) : baseQuery.OrderBy(r => r.Duration),
            "createdat" => query.SortOrder == "desc" ? baseQuery.OrderByDescending(r => r.CreatedAt) : baseQuery.OrderBy(r => r.CreatedAt),
            _ => query.SortOrder == "desc" ? baseQuery.OrderByDescending(r => r.CreatedAt) : baseQuery.OrderBy(r => r.CreatedAt)
        };

        var items = _mapper.Map<List<RecordingResponse>>(
            sorted.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList());

        return Task.FromResult(new PagedResult<RecordingResponse>
        {
            Items = items, Page = query.Page, PageSize = query.PageSize, TotalCount = total
        });
    }

    public async Task<RecordingResponse> GetRecordingAsync(Guid id, CancellationToken ct = default)
    {
        var rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, ct);
        return _mapper.Map<RecordingResponse>(rec ?? throw new NotFoundException(nameof(Recording), id));
    }

    /// <summary>
    /// Batch fetch: returns recordings in the same order as requested, silently skipping
    /// missing/soft-deleted ids (and anything the requester may not access when a userId
    /// is supplied). Input is capped at 100 distinct ids.
    /// </summary>
    public Task<IReadOnlyList<RecordingResponse>> GetRecordingsByIdsAsync(Guid? requesterUserId, IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var requested = ids.Where(i => i != Guid.Empty).Distinct().Take(BatchRecordingsValidator.MaxBatchSize).ToList();
        if (requested.Count == 0)
            return Task.FromResult<IReadOnlyList<RecordingResponse>>(Array.Empty<RecordingResponse>());

        var query = _uow.Repository<Recording>().Query().Where(r => !r.IsDeleted && requested.Contains(r.Id));
        if (requesterUserId.HasValue)
            query = query.Where(r => r.UserId == requesterUserId.Value);

        var byId = query.ToList().ToDictionary(r => r.Id);
        var items = requested
            .Where(id => byId.ContainsKey(id))
            .Select(id => _mapper.Map<RecordingResponse>(byId[id]))
            .ToList();

        return Task.FromResult<IReadOnlyList<RecordingResponse>>(items);
    }

    public async Task<RecordingResponse> CreateRecordingAsync(CreateRecordingRequest request, CancellationToken ct = default)
    {
        var userExists = await _uow.Repository<User>().AnyAsync(u => u.Id == request.UserId && !u.IsDeleted, ct);
        if (!userExists)
            throw new NotFoundException(nameof(User), request.UserId);

        var rec = new Recording
        {
            UserId = request.UserId,
            Title = request.Title.Trim(),
            Type = request.Type,
            CreatedAt = request.CreatedAt ?? DateTime.UtcNow,
            Duration = request.Duration ?? TimeSpan.Zero,
            Summary = request.Summary,
            Transcript = request.Transcript,
            Actions = request.Actions,
            Notes = request.Notes,
            IsRecording = request.IsRecording,
            Bookmarked = request.Bookmarked,
            FilePath = request.FilePath,
            SourceLanguageCode = request.SourceLanguageCode,
            TranscriptionStatus = request.TranscriptionStatus ?? TranscriptionStatus.Pending
        };

        _uow.Repository<Recording>().Add(rec);
        await _uow.SaveChangesAsync(ct);
        return _mapper.Map<RecordingResponse>(rec);
    }

    public async Task<RecordingResponse> UpdateRecordingAsync(Guid id, UpdateRecordingRequest request, CancellationToken ct = default)
    {
        var rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, ct)
            ?? throw new NotFoundException(nameof(Recording), id);

        if (request.Title is not null) rec.Title = request.Title.Trim();
        if (request.Type.HasValue) rec.Type = request.Type.Value;
        if (request.Duration.HasValue) rec.Duration = request.Duration.Value;
        if (request.Summary is not null) rec.Summary = request.Summary;
        if (request.Transcript is not null) rec.Transcript = request.Transcript;
        if (request.Actions is not null) rec.Actions = request.Actions;
        if (request.Notes is not null) rec.Notes = request.Notes;
        if (request.IsRecording.HasValue) rec.IsRecording = request.IsRecording.Value;
        if (request.Bookmarked.HasValue) rec.Bookmarked = request.Bookmarked.Value;
        if (request.FilePath is not null) rec.FilePath = request.FilePath;
        if (request.SourceLanguageCode is not null) rec.SourceLanguageCode = request.SourceLanguageCode;
        if (request.TranscriptionStatus.HasValue) rec.TranscriptionStatus = request.TranscriptionStatus.Value;
        rec.UpdatedDate = DateTime.UtcNow;

        _uow.Repository<Recording>().Update(rec);
        await _uow.SaveChangesAsync(ct);
        return _mapper.Map<RecordingResponse>(rec);
    }

    public async Task<RecordingResponse> SetBookmarkAsync(Guid id, bool bookmarked, CancellationToken ct = default)
    {
        var rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, ct)
            ?? throw new NotFoundException(nameof(Recording), id);

        rec.Bookmarked = bookmarked;
        rec.UpdatedDate = DateTime.UtcNow;
        _uow.Repository<Recording>().Update(rec);
        await _uow.SaveChangesAsync(ct);
        return _mapper.Map<RecordingResponse>(rec);
    }

    public async Task DeleteRecordingAsync(Guid id, CancellationToken ct = default)
    {
        var rec = await _uow.Repository<Recording>().FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, ct)
            ?? throw new NotFoundException(nameof(Recording), id);

        rec.IsDeleted = true;   // soft delete keeps history & audit trail
        rec.UpdatedDate = DateTime.UtcNow;
        _uow.Repository<Recording>().Update(rec);
        await _uow.SaveChangesAsync(ct);
    }
}
