using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Exceptions;

namespace MeetingRecorder.Application.Services;

/// <summary>
/// Shared validation for paged-list query parameters (sort whitelist,
/// sort direction, page bounds). Keeps list endpoints consistent and
/// prevents free-text sort keys from reaching EF expressions.
/// </summary>
public static class QueryGuard
{
    public static readonly string[] AllowedRecordingSort = { "title", "duration", "createdat", "updatedat" };
    public static readonly string[] AllowedUserSort = { "name", "email", "createdat" };

    public static void Validate(QueryParameters query, string[]? allowedSortBy = null)
    {
        if (query.Page < 1)
            throw new AppException("Page must be >= 1.", 400, "VALIDATION_ERROR");

        if (!string.IsNullOrWhiteSpace(query.SortBy))
        {
            var candidates = allowedSortBy ?? AllowedRecordingSort;
            // Normalize: created_at == createdat == CreatedAt (underscores ignored).
            var key = query.SortBy.ToLowerInvariant().Replace("_", "");
            if (!candidates.Contains(key))
                throw new AppException(
                    $"Invalid sortBy '{query.SortBy}'. Allowed: {string.Join(", ", candidates)}.",
                    400, "VALIDATION_ERROR");
        }

        if (query.SortOrder.ToLowerInvariant() is not ("asc" or "desc"))
            throw new AppException("sortOrder must be 'asc' or 'desc'.", 400, "VALIDATION_ERROR");

        if (query.CreatedAfter.HasValue && query.CreatedBefore.HasValue &&
            query.CreatedAfter.Value > query.CreatedBefore.Value)
        {
            throw new AppException("createdAfter must not be later than createdBefore.", 400, "VALIDATION_ERROR");
        }
    }
}
