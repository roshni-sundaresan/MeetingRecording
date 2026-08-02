using FluentValidation;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.WebApi.Common;
using Microsoft.AspNetCore.Mvc;
using ValidationException = MeetingRecorder.Application.Exceptions.ValidationException;

namespace MeetingRecorder.WebApi.Common;

/// <summary>
/// Base controller: standard envelope + declarative FluentValidation hook.
/// Derived controllers call <c>ValidateAsync(request)</c> before processing.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    private IServiceProvider? _services;
    protected IServiceProvider Services => _services ??= HttpContext.RequestServices;

    protected ICurrentUserService CurrentUser
        => Services.GetRequiredService<ICurrentUserService>();

    protected async Task ValidateAsync<T>(T request, CancellationToken ct = default)
    {
        var validator = Services.GetService<IValidator<T>>();
        if (validator is null) return;

        var result = await validator.ValidateAsync(request, ct);
        if (!result.IsValid)
        {
            var errors = result.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            throw new ValidationException(errors);
        }
    }

    protected ActionResult<ApiResponse<T>> Envelope<T>(T data, string? message = null, int statusCode = 200)
        => StatusCode(statusCode, ApiResponse<T>.Ok(data, message, statusCode));

    /// <summary>Envelope for endpoints that return no payload (e.g. DELETE).</summary>
    protected ActionResult<ApiResponse<bool>> Ok(string? message = null, int statusCode = 200)
        => StatusCode(statusCode, ApiResponseFactory.Ok(message));
}

/// <summary>Verifies the caller may act on the target user (self or admin).</summary>
public static class AccessPolicies
{
    public static bool CanActOnUser(ICurrentUserService current, Guid targetUserId)
        => current.IsAdmin || current.UserId == targetUserId;

    public static void EnsureCanActOnUser(ICurrentUserService current, Guid targetUserId)
    {
        if (!CanActOnUser(current, targetUserId))
            throw new AppException("You do not have permission to perform this action.", 403);
    }
}
