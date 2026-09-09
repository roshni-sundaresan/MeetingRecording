using AutoMapper;
using FluentAssertions;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Application.Mapping;
using MeetingRecorder.Application.Services;
using MeetingRecorder.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MeetingRecorder.UnitTests;

public class UserServiceTests
{
    private static User NewUser(string name, string email, string mobile) => new()
    {
        Id = Guid.NewGuid(), Email = email, Name = name, Mobile = mobile,
        PasswordHash = "h", Role = "User", CreatedDate = DateTime.UtcNow
    };

    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IRepository<User>> _repo = new();
    private readonly Mock<IPasswordHasher> _hasher = new();
    private readonly IMapper _mapper = new MapperConfiguration(cfg => cfg.AddProfile(new UserProfile()), NullLoggerFactory.Instance).CreateMapper();

    public UserServiceTests()
    {
        _uow.Setup(u => u.Repository<User>()).Returns(_repo.Object);
    }

    private UserService CreateSut() => new(_uow.Object, _mapper, _hasher.Object);

    [Fact]
    public async Task GetUsers_AppliesSearchAndPaging()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        var bob = NewUser("Bob", "bob@test.com", "2222222222");
        _repo.Setup(r => r.Query()).Returns(new[] { alice, bob }.AsQueryable());

        var result = await CreateSut().GetUsersAsync(new QueryParameters { Page = 1, PageSize = 10, Search = "ali", SortBy = "name", SortOrder = "asc" });

        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Email.Should().Be("alice@test.com");
    }

    [Fact]
    public async Task GetUser_WhenMissing_ThrowsNotFound()
    {
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var act = () => CreateSut().GetUserAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task CreateUser_WithDuplicateEmail_ThrowsConflict()
    {
        _repo.Setup(r => r.AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = () => CreateSut().CreateUserAsync(new CreateUserRequest("alice@test.com", "A", "1111111111", "Passw0rd!", null));

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task UpdateUser_UpdatesAndReturnsNewValues()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        var result = await CreateSut().UpdateUserAsync(alice.Id, new UpdateUserRequest(Name: "Alice Updated", Mobile: "9999999999"));

        result.Name.Should().Be("Alice Updated");
        result.Mobile.Should().Be("9999999999");
        result.Email.Should().Be("alice@test.com");
    }

    [Fact]
    public async Task UpdateUser_UpdatesOnlyProvidedFields_AndKeepsOthersUnchanged()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        alice.ProfilePhotoUrl = "http://example.com/old.jpg";
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        var result = await CreateSut().UpdateUserAsync(alice.Id, new UpdateUserRequest(Name: "New Name Only"));

        result.Name.Should().Be("New Name Only");
        result.Email.Should().Be("alice@test.com");
        result.Mobile.Should().Be("1111111111");
        result.ProfilePhotoUrl.Should().Be("http://example.com/old.jpg");
    }

    [Fact]
    public async Task UpdateUser_WithPassword_HashesNewPassword()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);
        _hasher.Setup(h => h.Hash("NewSecret123!")).Returns("hashed_secret");

        await CreateSut().UpdateUserAsync(alice.Id, new UpdateUserRequest(Password: "NewSecret123!"));

        alice.PasswordHash.Should().Be("hashed_secret");
    }

    [Fact]
    public async Task UpdateUser_WithDuplicateEmail_ThrowsConflict()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);
        _repo.Setup(r => r.AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = () => CreateSut().UpdateUserAsync(alice.Id, new UpdateUserRequest(Email: "bob@test.com"));

        await act.Should().ThrowAsync<ConflictException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task DeleteUser_SoftDeletes_AndCascades()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);
        // No recordings or sessions exist for Alice — cascade is a no-op.
        _uow.Setup(u => u.Repository<Recording>().Query()).Returns(new List<Recording>().AsQueryable());
        _uow.Setup(u => u.Repository<RefreshToken>().Query()).Returns(new List<RefreshToken>().AsQueryable());

        await CreateSut().DeleteUserAsync(alice.Id);

        alice.IsDeleted.Should().BeTrue();
        alice.UpdatedDate.Should().NotBeNull();
        _repo.Verify(r => r.Update(alice), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetApiKey_WhenValidKey_UpdatesUserAndReturnsMaskedStatus()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        var result = await CreateSut().SetApiKeyAsync(alice.Id, new SetApiKeyRequest("sk_live_1234567890abcdef"));

        result.HasCustomKey.Should().BeTrue();
        result.KeySource.Should().Be("custom");
        result.MaskedKey.Should().Be("sk_live_1234567890abcdef");
        alice.CustomApiKey.Should().Be("sk_live_1234567890abcdef");
        alice.UpdatedDate.Should().NotBeNull();
        _repo.Verify(r => r.Update(alice), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetApiKey_WithEmail_UpdatesUserAndReturnsStoredKey()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        var result = await CreateSut().SetApiKeyAsync(null, new SetApiKeyRequest(
            ApiKey: "sk_live_custom_sarvam_key",
            Email: "alice@test.com"));

        result.HasCustomKey.Should().BeTrue();
        result.MaskedKey.Should().Be("sk_live_custom_sarvam_key");
        result.Email.Should().Be("alice@test.com");
        alice.CustomApiKey.Should().Be("sk_live_custom_sarvam_key");
        _repo.Verify(r => r.Update(alice), Times.Once);
    }

    [Fact]
    public async Task GetApiKeyStatus_WithEmail_ReturnsStoredKeyInMaskedKeyField()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        alice.CustomApiKey = "sk_live_my_saved_key";
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        var result = await CreateSut().GetApiKeyStatusAsync(null, "alice@test.com");

        result.HasCustomKey.Should().BeTrue();
        result.MaskedKey.Should().Be("sk_live_my_saved_key");
        result.Email.Should().Be("alice@test.com");
    }

    [Fact]
    public async Task SetApiKey_WithValidationFailure_ThrowsAppException()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        var mockSarvam = new Mock<ISarvamApiService>();
        mockSarvam.Setup(s => s.ValidateApiKeyWithDetailsAsync("sk_invalid_key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyValidationResult(false, "The provided Sarvam API key is invalid or unauthorized.", "INVALID_API_KEY"));

        var sut = new UserService(_uow.Object, _mapper, _hasher.Object, mockSarvam.Object);

        var act = () => sut.SetApiKeyAsync(alice.Id, new SetApiKeyRequest("sk_invalid_key", Validate: true));

        await act.Should().ThrowAsync<AppException>().WithMessage("*invalid or unauthorized*");
    }

    [Fact]
    public async Task SetApiKey_WithInsufficientCredits_ThrowsAppExceptionWithClearMessage()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        var mockSarvam = new Mock<ISarvamApiService>();
        mockSarvam.Setup(s => s.ValidateApiKeyWithDetailsAsync("sk_no_credits_key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyValidationResult(
                false,
                "API key is valid, but the Sarvam account has no remaining credits (insufficient quota). Please recharge credits on your Sarvam dashboard.",
                "INSUFFICIENT_QUOTA",
                402));

        var sut = new UserService(_uow.Object, _mapper, _hasher.Object, mockSarvam.Object);

        var act = () => sut.SetApiKeyAsync(alice.Id, new SetApiKeyRequest("sk_no_credits_key", Validate: true));

        var ex = await act.Should().ThrowAsync<AppException>();
        ex.WithMessage("*insufficient quota*");
        ex.Which.ErrorCode.Should().Be("INSUFFICIENT_QUOTA");
    }

    [Fact]
    public async Task GetApiKeyStatus_WhenNoCustomKey_ReturnsSystemSource()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        alice.CustomApiKey = null;
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        var result = await CreateSut().GetApiKeyStatusAsync(alice.Id);

        result.HasCustomKey.Should().BeFalse();
        result.KeySource.Should().Be("system");
        result.MaskedKey.Should().BeNull();
        result.IsSystemKeyConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task ResetApiKey_ClearsCustomKey_ReturnsSystemSource()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        alice.CustomApiKey = "sk_custom_123456";
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        var result = await CreateSut().ResetApiKeyAsync(alice.Id);

        result.HasCustomKey.Should().BeFalse();
        result.KeySource.Should().Be("system");
        result.MaskedKey.Should().BeNull();
        alice.CustomApiKey.Should().BeNull();
        alice.UpdatedDate.Should().NotBeNull();
        _repo.Verify(r => r.Update(alice), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
