using FluentAssertions;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Application.Services;
using MeetingRecorder.Domain.Entities;
using Moq;

namespace MeetingRecorder.UnitTests;

public class AuthServiceTests
{
    private static readonly User ActiveUser = new()
    {
        Id = Guid.NewGuid(),
        Email = "user@test.com",
        Name = "Test User",
        Mobile = "+91 90000 00000",
        PasswordHash = "hashed",
        Role = "User"
    };

    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<ITokenService> _tokens = new();
    private readonly Mock<IPasswordHasher> _hasher = new();
    private readonly Mock<IOtpService> _otp = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly PasswordResetOptions _resetOptions = new()
    {
        OtpLifetimeMinutes = 5,
        MaxOtpAttempts = 5,
        ResendCooldownSeconds = 60,
        ResetTokenLifetimeMinutes = 10,
        DevOtpExposure = false
    };

    private AuthService CreateSut() => new(_uow.Object, _tokens.Object, _hasher.Object,
        _otp.Object, _email.Object, Microsoft.Extensions.Options.Options.Create(_resetOptions));

    /// <summary>Common stubs for the refresh-token plumbing used by login/register/refresh.</summary>
    private void SetupAuthSuccess()
    {
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-token-123");
        _tokens.Setup(t => t.HashRefreshToken(It.IsAny<string>())).Returns("rt-hash");
        _uow.Setup(u => u.Repository<RefreshToken>().Add(It.IsAny<RefreshToken>()));
        _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.FromResult(1));
    }

    /// <summary>Stubs for the password-reset flow: real crypto for hash/verify,
    /// a fixed OTP from the mocked generator, and a persisted-request sink.</summary>
    private PasswordResetRequest SetupResetFlow(string otp = "482731")
    {
        var realOtp = new OtpService();
        _otp.Setup(o => o.GenerateOtp()).Returns(otp);
        _otp.Setup(o => o.HashOtp(It.IsAny<string>())).Returns<string>(realOtp.HashOtp);
        _otp.Setup(o => o.VerifyOtp(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>(realOtp.VerifyOtp);
        _otp.Setup(o => o.GenerateResetToken()).Returns("reset-auth-token-xyz");
        _uow.Setup(u => u.Repository<PasswordResetRequest>().Add(It.IsAny<PasswordResetRequest>()));
        _uow.Setup(u => u.Repository<PasswordResetRequest>().Update(It.IsAny<PasswordResetRequest>()));
        _uow.Setup(u => u.Repository<RefreshToken>().Query()).Returns(new List<RefreshToken>().AsQueryable());
        _uow.Setup(u => u.Repository<PasswordResetRequest>().Query()).Returns(new List<PasswordResetRequest>().AsQueryable());
        _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.FromResult(1));

        return new PasswordResetRequest
        {
            Id = Guid.NewGuid(),
            UserId = ActiveUser.Id,
            OtpHash = realOtp.HashOtp(otp),
            Purpose = AuthService.PurposePasswordReset,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ResendAt = DateTime.UtcNow.AddSeconds(60)
        };
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsToken()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);
        _hasher.Setup(h => h.Verify("Passw0rd!", "hashed")).Returns(true);
        _tokens.Setup(t => t.GenerateToken(ActiveUser.Id, ActiveUser.Email, ActiveUser.Name, ActiveUser.Role))
            .Returns(("jwt-token", DateTime.UtcNow.AddHours(1)));
        SetupAuthSuccess();

        var result = await CreateSut().LoginAsync(new LoginRequest("user@test.com", "Passw0rd!"));

        result.Token.Should().Be("jwt-token");
        result.User.Email.Should().Be(ActiveUser.Email);
        result.TokenType.Should().Be("Bearer");
        result.RefreshToken.Should().Be("refresh-token-123");
        result.RefreshExpiresAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Login_WithWrongPassword_ThrowsUnauthorized()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);
        _hasher.Setup(h => h.Verify(It.IsAny<string>(), "hashed")).Returns(false);

        var act = () => CreateSut().LoginAsync(new LoginRequest("user@test.com", "wrong"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 401);
    }

    [Fact]
    public async Task Login_WithUnknownUser_ThrowsUnauthorized()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var act = () => CreateSut().LoginAsync(new LoginRequest("nobody@test.com", "whatever"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 401);
    }

    [Fact]
    public async Task Login_WithOAuthProviderAndNewUser_ProvisionsUserAndReturnsToken()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        _uow.Setup(u => u.Repository<User>().Add(It.IsAny<User>()));
        _hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("random-oauth-hash");
        _tokens.Setup(t => t.GenerateToken(It.IsAny<Guid>(), "googleuser@test.com", It.IsAny<string>(), MeetingRecorder.Domain.Constants.Roles.User))
            .Returns(("jwt-google-token", DateTime.UtcNow.AddHours(1)));
        SetupAuthSuccess();

        var result = await CreateSut().LoginAsync(new LoginRequest(
            Email: "googleuser@test.com",
            Password: null,
            ProviderName: "google",
            OAuthKey: "ya29.sample_oauth_key"));

        result.Token.Should().Be("jwt-google-token");
        result.User.Email.Should().Be("googleuser@test.com");
        _uow.Verify(u => u.Repository<User>().Add(It.Is<User>(user =>
            user.Email == "googleuser@test.com" &&
            user.ProviderName == "google" &&
            user.OAuthKey == "ya29.sample_oauth_key")), Times.Once);
    }

    [Fact]
    public async Task Login_WithOAuthProviderAndExistingUser_UpdatesOAuthKeyAndReturnsToken()
    {
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Email = "existing@test.com",
            Name = "Existing User",
            PasswordHash = "hashed",
            Role = MeetingRecorder.Domain.Constants.Roles.User
        };

        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _uow.Setup(u => u.Repository<User>().Update(It.IsAny<User>()));
        _tokens.Setup(t => t.GenerateToken(existing.Id, existing.Email, existing.Name, existing.Role))
            .Returns(("jwt-token", DateTime.UtcNow.AddHours(1)));
        SetupAuthSuccess();

        var result = await CreateSut().LoginAsync(new LoginRequest(
            Email: "existing@test.com",
            Password: null,
            ProviderName: "microsoft",
            OAuthKey: "ms_access_token_xyz"));

        result.Token.Should().Be("jwt-token");
        existing.OAuthKey.Should().Be("ms_access_token_xyz");
        existing.ProviderName.Should().Be("microsoft");
        _uow.Verify(u => u.Repository<User>().Update(existing), Times.Once);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ThrowsConflict()
    {
        _uow.Setup(u => u.Repository<User>().AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = () => CreateSut().RegisterAsync(new RegisterRequest("user@test.com", "A", "1234567890", "Passw0rd!", null));

        await act.Should().ThrowAsync<ConflictException>().Where(e => e.ErrorCode == "EMAIL_TAKEN");
    }

    [Fact]
    public async Task Register_WithDuplicateMobile_ThrowsConflictWithMobileTakenCode()
    {
        // First AnyAsync call (email check) returns false; second (mobile check) returns true.
        _uow.SetupSequence(u => u.Repository<User>().AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        var act = () => CreateSut().RegisterAsync(new RegisterRequest("fresh@test.com", "A", "1234567890", "Passw0rd!", null));

        await act.Should().ThrowAsync<ConflictException>().Where(e => e.ErrorCode == "MOBILE_TAKEN");
    }

    [Fact]
    public async Task Register_WithNewEmail_CreatesUserAndReturnsToken()
    {
        _uow.SetupSequence(u => u.Repository<User>().AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(false);
        _hasher.Setup(h => h.Hash("Passw0rd!")).Returns("new-hash");
        _tokens.Setup(t => t.GenerateToken(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "User"))
            .Returns(("jwt-token", DateTime.UtcNow.AddHours(1)));
        SetupAuthSuccess();

        var result = await CreateSut().RegisterAsync(new RegisterRequest("NEW@test.com", "New User", "1234567890", "Passw0rd!", null));

        result.Token.Should().Be("jwt-token");
        result.User.Email.Should().Be("new@test.com");   // lower-cased + trimmed
    }

    // ── Refresh tokens ──

    [Fact]
    public async Task Refresh_WithValidToken_RotatesAndReturnsNewPair()
    {
        var stored = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = ActiveUser.Id,
            TokenHash = "rt-hash",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _uow.Setup(u => u.Repository<RefreshToken>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<RefreshToken, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        _uow.Setup(u => u.Repository<RefreshToken>().Update(It.IsAny<RefreshToken>()));
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);
        _tokens.Setup(t => t.HashRefreshToken("presented-token")).Returns("rt-hash");
        _tokens.Setup(t => t.GenerateToken(ActiveUser.Id, ActiveUser.Email, ActiveUser.Name, ActiveUser.Role))
            .Returns(("new-jwt", DateTime.UtcNow.AddHours(1)));
        SetupAuthSuccess();

        var result = await CreateSut().RefreshAsync(new RefreshTokenRequest("presented-token"));

        result.Token.Should().Be("new-jwt");
        result.RefreshToken.Should().Be("refresh-token-123");   // rotated
        stored.RevokedAt.Should().NotBeNull();                  // old token revoked
    }

    [Fact]
    public async Task Refresh_WithUnknownToken_ThrowsUnauthorized()
    {
        _uow.Setup(u => u.Repository<RefreshToken>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<RefreshToken, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RefreshToken?)null);

        var act = () => CreateSut().RefreshAsync(new RefreshTokenRequest("ghost-token"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 401 && e.ErrorCode == "INVALID_REFRESH_TOKEN");
    }

    [Fact]
    public async Task Refresh_WithExpiredToken_ThrowsUnauthorized()
    {
        var stored = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = ActiveUser.Id,
            TokenHash = "rt-hash",
            ExpiresAt = DateTime.UtcNow.AddDays(-1)   // expired
        };
        _uow.Setup(u => u.Repository<RefreshToken>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<RefreshToken, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        _tokens.Setup(t => t.HashRefreshToken("old-token")).Returns("rt-hash");

        var act = () => CreateSut().RefreshAsync(new RefreshTokenRequest("old-token"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 401);
    }

    [Fact]
    public async Task Logout_WithValidToken_RevokesIt()
    {
        var stored = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = ActiveUser.Id,
            TokenHash = "rt-hash",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _uow.Setup(u => u.Repository<RefreshToken>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<RefreshToken, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        _uow.Setup(u => u.Repository<RefreshToken>().Update(It.IsAny<RefreshToken>()));
        _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.FromResult(1));
        _tokens.Setup(t => t.HashRefreshToken("bye-token")).Returns("rt-hash");

        await CreateSut().LogoutAsync(new RefreshTokenRequest("bye-token"));

        stored.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Logout_WithUnknownToken_IsIdempotent()
    {
        _uow.Setup(u => u.Repository<RefreshToken>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<RefreshToken, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RefreshToken?)null);

        var act = () => CreateSut().LogoutAsync(new RefreshTokenRequest("ghost-token"));

        await act.Should().NotThrowAsync();
    }

    // ── Password reset (server-authoritative OTP flow) ──

    [Fact]
    public async Task PasswordReset_Request_ForKnownUser_CreatesRequestEmailsAndReturnsRequestId()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);
        SetupResetFlow();

        var result = await CreateSut().RequestPasswordResetAsync(new PasswordResetRequestRequest("user@test.com"));

        result.ResetRequestId.Should().NotBeNull();
        result.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));
        result.DevOtp.Should().BeNull();   // production: never returns the OTP
        _email.Verify(e => e.SendPasswordResetOtpAsync(ActiveUser.Email, "482731", 5, It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.Repository<PasswordResetRequest>().Add(It.IsAny<PasswordResetRequest>()), Times.Once);
    }

    [Fact]
    public async Task PasswordReset_Request_ForUnknownUser_ReturnsGenericResponse()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var result = await CreateSut().RequestPasswordResetAsync(new PasswordResetRequestRequest("ghost@test.com"));

        result.ResetRequestId.Should().BeNull();                 // no enumeration
        result.Message.Should().Be("If the account exists, an OTP has been sent.");
        _uow.Verify(u => u.Repository<PasswordResetRequest>().Add(It.IsAny<PasswordResetRequest>()), Times.Never);
        _email.Verify(e => e.SendPasswordResetOtpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PasswordReset_Request_InDevMode_ReturnsOtp()
    {
        _resetOptions.DevOtpExposure = true;
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);
        SetupResetFlow();

        var result = await CreateSut().RequestPasswordResetAsync(new PasswordResetRequestRequest("user@test.com"));

        result.DevOtp.Should().Be("482731");   // dev-only convenience flag
    }

    [Fact]
    public async Task PasswordReset_Verify_WithCorrectOtp_IssuesResetTokenAndConsumes()
    {
        var reset = SetupResetFlow();
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reset);

        var result = await CreateSut().VerifyOtpAsync(new VerifyOtpRequest(reset.Id.ToString(), "482731"));

        result.ResetToken.Should().Be("reset-auth-token-xyz");
        reset.IsUsed.Should().BeTrue();                              // OTP consumed
        reset.ResetTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task PasswordReset_Verify_WithWrongOtp_IncrementsAttempts()
    {
        var reset = SetupResetFlow("482731");
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reset);

        var act = () => CreateSut().VerifyOtpAsync(new VerifyOtpRequest(reset.Id.ToString(), "000000"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 400 && e.ErrorCode == "INVALID_OTP");
        reset.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task PasswordReset_Verify_ExceedingMaxAttempts_LocksRequest()
    {
        var reset = SetupResetFlow("482731");
        reset.AttemptCount = 5;   // already at the limit
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reset);

        var act = () => CreateSut().VerifyOtpAsync(new VerifyOtpRequest(reset.Id.ToString(), "482731"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 400);
        reset.IsUsed.Should().BeTrue();   // locked — brute force bounded
    }

    [Fact]
    public async Task PasswordReset_Verify_ExpiredOtp_Rejected()
    {
        var reset = SetupResetFlow("482731");
        reset.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reset);

        var act = () => CreateSut().VerifyOtpAsync(new VerifyOtpRequest(reset.Id.ToString(), "482731"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 400);
    }

    [Fact]
    public async Task PasswordReset_Verify_ConsumedOtp_Rejected()
    {
        var reset = SetupResetFlow("482731");
        reset.IsUsed = true;
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reset);

        var act = () => CreateSut().VerifyOtpAsync(new VerifyOtpRequest(reset.Id.ToString(), "482731"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 400);
    }

    [Fact]
    public async Task PasswordReset_Resend_WithinCooldown_Rejected()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);
        var active = SetupResetFlow();
        active.ResendAt = DateTime.UtcNow.AddSeconds(30);   // still cooling down
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(active);

        var act = () => CreateSut().ResendOtpAsync(new ResendOtpRequest("user@test.com"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 429 && e.ErrorCode == "RESEND_COOLDOWN");
    }

    [Fact]
    public async Task PasswordReset_Resend_AfterCooldown_InvalidatesPreviousAndCreatesNew()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);
        var previous = SetupResetFlow("111111");
        previous.ResendAt = DateTime.UtcNow.AddSeconds(-1);   // cooldown passed
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(previous);

        var result = await CreateSut().ResendOtpAsync(new ResendOtpRequest("user@test.com"));

        result.ResetRequestId.Should().NotBeNull();
        previous.IsUsed.Should().BeTrue();   // old OTP invalidated
        _uow.Verify(u => u.Repository<PasswordResetRequest>().Add(It.IsAny<PasswordResetRequest>()), Times.Once);
        _email.Verify(e => e.SendPasswordResetOtpAsync(ActiveUser.Email, "111111", 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PasswordReset_Complete_WithValidToken_UpdatesPasswordAndRevokesSessions()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "resetuser@test.com",
            Name = "Reset User",
            PasswordHash = "old-hash",
            Role = "User"
        };
        var realOtp = new OtpService();
        var reset = SetupResetFlow("482731");
        reset.UserId = user.Id;
        reset.ResetTokenHash = realOtp.HashOtp("reset-auth-token-xyz");
        reset.ResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(10);
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reset);
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        var session = new RefreshToken { Id = Guid.NewGuid(), UserId = user.Id, TokenHash = "h", ExpiresAt = DateTime.UtcNow.AddDays(7) };
        _uow.Setup(u => u.Repository<RefreshToken>().Query()).Returns(new List<RefreshToken> { session }.AsQueryable());
        _uow.Setup(u => u.Repository<RefreshToken>().Update(It.IsAny<RefreshToken>()));
        _uow.Setup(u => u.Repository<PasswordResetRequest>().Update(It.IsAny<PasswordResetRequest>()));
        _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.FromResult(1));
        _hasher.Setup(h => h.Hash("NewPassw0rd!")).Returns("new-hash");

        await CreateSut().CompletePasswordResetAsync(new CompleteResetRequest("reset-auth-token-xyz", "NewPassw0rd!"));

        user.PasswordHash.Should().Be("new-hash");
        reset.ResetTokenHash.Should().BeNull();            // authorization consumed
        session.RevokedAt.Should().NotBeNull();            // sessions revoked
    }

    [Fact]
    public async Task PasswordReset_Complete_WithUnknownToken_Rejected()
    {
        SetupResetFlow("482731");
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PasswordResetRequest?)null);

        var act = () => CreateSut().CompletePasswordResetAsync(new CompleteResetRequest("bogus-token", "NewPassw0rd!"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 400 && e.ErrorCode == "INVALID_RESET_TOKEN");
    }

    [Fact]
    public async Task PasswordReset_Complete_WithExpiredResetToken_Rejected()
    {
        var realOtp = new OtpService();
        var reset = SetupResetFlow("482731");
        reset.ResetTokenHash = realOtp.HashOtp("reset-auth-token-xyz");
        reset.ResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);   // expired
        _uow.Setup(u => u.Repository<PasswordResetRequest>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<PasswordResetRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reset);

        var act = () => CreateSut().CompletePasswordResetAsync(new CompleteResetRequest("reset-auth-token-xyz", "NewPassw0rd!"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 400);
    }
}
