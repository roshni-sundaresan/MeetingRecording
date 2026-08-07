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

    private AuthService CreateSut() => new(_uow.Object, _tokens.Object, _hasher.Object);

    /// <summary>Common stubs for the refresh-token plumbing used by login/register/refresh.</summary>
    private void SetupAuthSuccess()
    {
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-token-123");
        _tokens.Setup(t => t.HashRefreshToken(It.IsAny<string>())).Returns("rt-hash");
        _uow.Setup(u => u.Repository<RefreshToken>().Add(It.IsAny<RefreshToken>()));
        _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.FromResult(1));
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

    // ── Password reset ──

    [Fact]
    public async Task ForgotPassword_ForExistingUser_IssuesToken()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);

        var result = await CreateSut().ForgotPasswordAsync(new ForgotPasswordRequest("user@test.com"));

        result.ResetToken.Should().NotBeNullOrEmpty();
        result.ExpiresMinutes.Should().Be(15);
    }

    [Fact]
    public async Task ForgotPassword_ForUnknownUser_ReturnsNoToken()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var result = await CreateSut().ForgotPasswordAsync(new ForgotPasswordRequest("ghost@test.com"));

        result.ResetToken.Should().BeNull();   // no account enumeration
    }

    [Fact]
    public async Task ResetPassword_WithValidCode_UpdatesHashAndIsSingleUse()
    {
        // Dedicated user — never mutate the shared ActiveUser fixture.
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "resetuser@test.com",
            Name = "Reset User",
            PasswordHash = "old-hash",
            Role = "User"
        };
        _uow.SetupSequence(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user)
            .ReturnsAsync(user);
        _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.FromResult(1));
        _hasher.Setup(h => h.Hash("NewPassw0rd!")).Returns("new-hash");

        var sut = CreateSut();
        var issued = await sut.ForgotPasswordAsync(new ForgotPasswordRequest("resetuser@test.com"));
        issued.ResetToken.Should().NotBeNull();

        // First reset succeeds
        var act = () => sut.ResetPasswordAsync(new ResetPasswordRequest("resetuser@test.com", issued.ResetToken!, "NewPassw0rd!"));
        await act.Should().NotThrowAsync();
        user.PasswordHash.Should().Be("new-hash");

        // Token is single-use: reuse fails
        var reuse = () => sut.ResetPasswordAsync(new ResetPasswordRequest("resetuser@test.com", issued.ResetToken!, "AnotherPassw0rd!"));
        await reuse.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 400);
    }

    [Fact]
    public async Task ResetPassword_WithWrongCode_ThrowsBadRequest()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);

        var sut = CreateSut();
        await sut.ForgotPasswordAsync(new ForgotPasswordRequest("user@test.com"));

        var act = () => sut.ResetPasswordAsync(new ResetPasswordRequest("user@test.com", "WRONG-CODE", "NewPassw0rd!"));

        await act.Should().ThrowAsync<AppException>().Where(e => e.StatusCode == 400);
    }
}
