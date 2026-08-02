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

        var result = await CreateSut().UpdateUserAsync(alice.Id, new UpdateUserRequest("Alice Updated", "9999999999", null));

        result.Name.Should().Be("Alice Updated");
        result.Mobile.Should().Be("9999999999");
    }

    [Fact]
    public async Task DeleteUser_SoftDeletes()
    {
        var alice = NewUser("Alice", "alice@test.com", "1111111111");
        _repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alice);

        await CreateSut().DeleteUserAsync(alice.Id);

        alice.IsDeleted.Should().BeTrue();
        alice.UpdatedDate.Should().NotBeNull();
        _repo.Verify(r => r.Update(alice), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
