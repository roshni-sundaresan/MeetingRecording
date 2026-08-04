using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Constants;
using MeetingRecorder.Domain.Entities;
using MeetingRecorder.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MeetingRecorder.WebApi.Services;

/// <summary>
/// Accepts either a native API JWT (issued by /api/auth/login|register) or a
/// Firebase ID token (when Firebase Admin is configured). Firebase users are
/// auto-provisioned into the Users table on first authenticated call, so
/// ownership rules work identically for both identity providers.
/// </summary>
public class HybridAuthOptions : AuthenticationSchemeOptions
{
    public TokenValidationParameters TokenValidationParameters { get; set; } = new();
}

public class HybridAuthenticationHandler : AuthenticationHandler<HybridAuthOptions>
{
    private readonly IFirebaseAuthService _firebase;

    public HybridAuthenticationHandler(
        IOptionsMonitor<HybridAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IFirebaseAuthService firebase)
        : base(options, logger, encoder)
    {
        _firebase = firebase;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        // 1) Firebase ID token (active only when service credentials are configured)
        if (_firebase.IsEnabled)
        {
            var info = await _firebase.VerifyIdTokenAsync(token);
            if (info != null)
            {
                try
                {
                    var user = await ProvisionUserAsync(info);
                    var claims = new List<Claim>
                    {
                        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                        new(ClaimTypes.Email, user.Email),
                        new(ClaimTypes.Name, user.Name),
                        new(ClaimTypes.Role, user.Role)
                    };
                    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Firebase"));
                    return AuthenticateResult.Success(new AuthenticationTicket(principal, "Firebase"));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Firebase user provisioning failed.");
                    return AuthenticateResult.Fail("Firebase user provisioning failed.");
                }
            }
        }

        // 2) Native JWT fallback
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, Options.TokenValidationParameters, out _);
            return AuthenticateResult.Success(
                new AuthenticationTicket(principal, JwtBearerDefaults.AuthenticationScheme));
        }
        catch (Exception)
        {
            return AuthenticateResult.Fail("Invalid token.");
        }
    }

    private async Task<User> ProvisionUserAsync(FirebaseTokenInfo info)
    {
        var uow = Context.RequestServices.GetRequiredService<IUnitOfWork>();
        var repo = uow.Repository<User>();

        var existing = await repo.FirstOrDefaultAsync(u => u.FirebaseUid == info.Uid, Context.RequestAborted);
        if (existing != null)
            return existing;

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = (info.Email ?? $"{info.Uid}@firebase.local").ToLowerInvariant(),
            Name = info.Name ?? "Firebase User",
            Mobile = string.Empty,
            ProfilePhotoUrl = null,
            Role = Roles.User,
            FirebaseUid = info.Uid,
            PasswordHash = "<firebase-managed>",
            CreatedDate = DateTime.UtcNow
        };

        repo.Add(user);
        await uow.SaveChangesAsync(Context.RequestAborted);
        return user;
    }
}
