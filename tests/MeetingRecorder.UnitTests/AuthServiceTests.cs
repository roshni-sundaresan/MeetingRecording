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

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsToken()
    {
        _uow.Setup(u => u.Repository<User>().FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveUser);
        _hasher.Setup(h => h.Verify("Passw0rd!", "hashed")).Returns(true);
        _tokens.Setup(t => t.GenerateToken(ActiveUser.Id, ActiveUser.Email, ActiveUser.Name, ActiveUser.Role))
            .Returns(("jwt-token", DateTime.UtcNow.AddHours(1)));

        var result = await CreateSut().LoginAsync(new LoginRequest("user@test.com", "Passw0rd!"));

        result.Token.Should().Be("jwt-token");
        result.User.Email.Should().Be(ActiveUser.Email);
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

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Register_WithNewEmail_CreatesUserAndReturnsToken()
    {
        _uow.Setup(u => u.Repository<User>().AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _hasher.Setup(h => h.Hash("Passw0rd!")).Returns("new-hash");
        _tokens.Setup(t => t.GenerateToken(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "User"))
            .Returns(("jwt-token", DateTime.UtcNow.AddHours(1)));

        var result = await CreateSut().RegisterAsync(new RegisterRequest("NEW@test.com", "New User", "1234567890", "Passw0rd!", null));

        result.Token.Should().Be("jwt-token");
        result.User.Email.Should().Be("new@test.com");   // lower-cased + trimmed
    }
}
