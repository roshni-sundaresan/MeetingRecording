using AutoMapper;
using FluentAssertions;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Application.Mapping;
using MeetingRecorder.Application.Services;
using MeetingRecorder.Domain;
using MeetingRecorder.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MeetingRecorder.UnitTests;

public class RecordingServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly Recording Sample = new()
    {
        Id = Guid.NewGuid(),
        UserId = UserId,
        Title = "Sprint Planning",
        Type = RecordingType.Meeting,
        CreatedAt = DateTime.UtcNow,
        Duration = TimeSpan.FromMinutes(30),
        Summary = "Plan the sprint",
        Bookmarked = false
    };

    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IRepository<Recording>> _recRepo = new();
    private readonly Mock<IRepository<User>> _userRepo = new();
    private readonly IMapper _mapper = new MapperConfiguration(cfg => cfg.AddProfile(new RecordingProfile()), NullLoggerFactory.Instance).CreateMapper();

    public RecordingServiceTests()
    {
        _uow.Setup(u => u.Repository<Recording>()).Returns(_recRepo.Object);
        _uow.Setup(u => u.Repository<User>()).Returns(_userRepo.Object);
    }

    private RecordingService CreateSut() => new(_uow.Object, _mapper);

    [Fact]
    public async Task Create_WhenUserMissing_ThrowsNotFound()
    {
        _userRepo.Setup(r => r.AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var act = () => CreateSut().CreateRecordingAsync(new CreateRecordingRequest(
            UserId, "T", RecordingType.Audio, null, null, null, null, null, null, false, false, null, "en", null));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Create_WhenUserExists_AddsRecording()
    {
        _userRepo.Setup(r => r.AnyAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await CreateSut().CreateRecordingAsync(new CreateRecordingRequest(
            UserId, "  New Meeting  ", RecordingType.Video, null, TimeSpan.FromMinutes(10), "sum", null, "act", null,
            false, true, null, "en", TranscriptionStatus.Pending));

        result.Title.Should().Be("New Meeting");   // trimmed
        result.Bookmarked.Should().BeTrue();
        _recRepo.Verify(r => r.Add(It.IsAny<Recording>()), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetById_WhenMissing_ThrowsNotFound()
    {
        _recRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Recording, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Recording?)null);

        var act = () => CreateSut().GetRecordingAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Update_PartialFields_OnlyChangesProvidedValues()
    {
        _recRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Recording, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample);

        var result = await CreateSut().UpdateRecordingAsync(Sample.Id,
            new UpdateRecordingRequest(null, null, TimeSpan.FromMinutes(45), null, null, null, "new note",
                null, null, null, null, null));

        result.Duration.Should().Be(TimeSpan.FromMinutes(45));
        result.Notes.Should().Be("new note");
        result.Title.Should().Be("Sprint Planning");   // untouched
    }

    [Fact]
    public async Task SetBookmark_TogglesFlag()
    {
        _recRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Recording, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample);

        var result = await CreateSut().SetBookmarkAsync(Sample.Id, true);

        result.Bookmarked.Should().BeTrue();
    }
}
