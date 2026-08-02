using System.Security.Claims;
using MeetingRecorder.Application.Interfaces;

namespace MeetingRecorder.WebApi.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? User?.FindFirst("sub")?.Value, out var id)
            ? id : null;

    public string? Email => User?.FindFirstValue(ClaimTypes.Email) ?? User?.FindFirst("email")?.Value;

    public string? Name => User?.FindFirstValue(ClaimTypes.Name) ?? User?.FindFirst("name")?.Value;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public bool IsAdmin => User?.IsInRole("Admin") == true;
}
