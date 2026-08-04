using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Microsoft.Extensions.Logging;

namespace MeetingRecorder.Infrastructure.Security;

/// <summary>
/// Wraps Firebase Admin SDK token verification. Only becomes active when a
/// service-account credential is available (GOOGLE_APPLICATION_CREDENTIALS or
/// FIREBASE_CREDENTIALS_PATH); otherwise <see cref="IsEnabled"/> is false and
/// the API keeps using its own JWT scheme exclusively.
/// </summary>
public interface IFirebaseAuthService
{
    bool IsEnabled { get; }
    Task<FirebaseTokenInfo?> VerifyIdTokenAsync(string idToken, CancellationToken ct = default);
}

public record FirebaseTokenInfo(string Uid, string? Email, string? Name);

public class FirebaseAuthService : IFirebaseAuthService
{
    private readonly ILogger<FirebaseAuthService> _logger;
    private bool _initAttempted;

    public FirebaseAuthService(ILogger<FirebaseAuthService> logger)
    {
        _logger = logger;
    }

    public bool IsEnabled
    {
        get
        {
            TryInitialize();
            return FirebaseApp.DefaultInstance != null;
        }
    }

    private void TryInitialize()
    {
        if (_initAttempted || FirebaseApp.DefaultInstance != null) return;
        _initAttempted = true;

        try
        {
            var credentialsPath = Environment.GetEnvironmentVariable("FIREBASE_CREDENTIALS_PATH")
                ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

            if (string.IsNullOrWhiteSpace(credentialsPath) || !File.Exists(credentialsPath))
            {
                _logger.LogWarning("Firebase credentials not found; Firebase-token authentication is DISABLED (native JWT still works).");
                return;
            }

            FirebaseApp.Create(new AppOptions
            {
                Credential = Google.Apis.Auth.OAuth2.CredentialFactory
                    .FromFile<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(credentialsPath)
                    .ToGoogleCredential()
            });
            _logger.LogInformation("Firebase Admin initialized from {Path}", credentialsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Firebase Admin; Firebase-token authentication DISABLED.");
        }
    }

    public async Task<FirebaseTokenInfo?> VerifyIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (!IsEnabled) return null;

        try
        {
            var decoded = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(idToken, ct);
            if (decoded == null) return null;
            return new FirebaseTokenInfo(
                decoded.Uid,
                decoded.Claims.TryGetValue("email", out var e) ? e?.ToString() : null,
                decoded.Claims.TryGetValue("name", out var n) ? n?.ToString() : null);
        }
        catch (FirebaseAuthException)
        {
            return null;
        }
    }
}
