using System.Linq.Expressions;
using FluentAssertions;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Application.Services;
using MeetingRecorder.Domain;
using MeetingRecorder.Domain.Entities;
using MeetingRecorder.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MeetingRecorder.UnitTests;

public class MeetingSchedulerServiceTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IRepository<User>> _userRepo = new();
    private readonly Mock<IRepository<Recording>> _recRepo = new();
    private readonly Mock<IRepository<ScheduledMeeting>> _meetingRepo = new();
    private readonly Mock<IMeetingProviderClient> _teamsClient = new();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _recordingId = Guid.NewGuid();

    public MeetingSchedulerServiceTests()
    {
        _uow.Setup(u => u.Repository<User>()).Returns(_userRepo.Object);
        _uow.Setup(u => u.Repository<Recording>()).Returns(_recRepo.Object);
        _uow.Setup(u => u.Repository<ScheduledMeeting>()).Returns(_meetingRepo.Object);

        _teamsClient.Setup(c => c.Provider).Returns(MeetingProvider.Teams);
        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeetingConferenceDetails("https://teams.microsoft.com/meet", "123-456", "pass", "ext-1"));

        _userRepo.Setup(r => r.GetByIdAsync(_userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                Id = _userId,
                Email = "test@example.com",
                Role = "User"
            });
    }

    private MeetingSchedulerService CreateSut()
    {
        return new MeetingSchedulerService(
            _uow.Object,
            new[] { _teamsClient.Object },
            NullLogger<MeetingSchedulerService>.Instance);
    }

    [Fact]
    public async Task ScheduleMeeting_WithRecordingId_LoadsSummaryIntoDescription()
    {
        var recording = new Recording
        {
            Id = _recordingId,
            UserId = _userId,
            Title = "Sales Call",
            Summary = "### Discussion\nClient agreed to contract terms."
        };

        _recRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Recording, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(recording);

        ScheduleMeetingRequest? capturedRequest = null;
        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduleMeetingRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new MeetingConferenceDetails("https://teams.microsoft.com/meet", "123-456", "pass", "ext-1"));

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Contract Discussion",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00",
            RecordingId: _recordingId);

        var result = await sut.ScheduleMeetingAsync(_userId, req);

        result.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Description.Should().Be("Discussion\nClient agreed to contract terms.");
    }

    [Fact]
    public async Task ScheduleMeeting_WithDescriptionAndRecordingId_CombinesBothWithoutMarkdownNoise()
    {
        var recording = new Recording
        {
            Id = _recordingId,
            UserId = _userId,
            Title = "Sprint Demo",
            Summary = "### **1. Overview**\n* **Item 1:** Detail A\n---"
        };

        _recRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Recording, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(recording);

        ScheduleMeetingRequest? capturedRequest = null;
        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduleMeetingRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new MeetingConferenceDetails("https://teams.microsoft.com/meet", "123-456", "pass", "ext-1"));

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Demo Followup",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00",
            Description: "Please see notes below before joining:",
            RecordingId: _recordingId);

        var result = await sut.ScheduleMeetingAsync(_userId, req);

        result.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Description.Should().Contain("Please see notes below before joining:");
        capturedRequest!.Description.Should().Contain("Meeting Summary:");
        capturedRequest!.Description.Should().Contain("1. Overview");
        capturedRequest!.Description.Should().Contain("• Item 1: Detail A");
        capturedRequest!.Description.Should().NotContain("###");
        capturedRequest!.Description.Should().NotContain("**");
        capturedRequest!.Description.Should().NotContain("---");
    }

    [Fact]
    public async Task ScheduleMeeting_WithDirectSummary_UsesProvidedSummary()
    {
        ScheduleMeetingRequest? capturedRequest = null;
        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduleMeetingRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new MeetingConferenceDetails("https://teams.microsoft.com/meet", "123-456", "pass", "ext-1"));

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Quick Sync",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00",
            Summary: "Directly passed summary text.");

        var result = await sut.ScheduleMeetingAsync(_userId, req);

        result.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Description.Should().Be("Directly passed summary text.");
    }

    [Fact]
    public async Task ScheduleMeeting_RecordingNotFound_ThrowsNotFoundException()
    {
        _recRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Recording, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Recording?)null);

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Contract Discussion",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00",
            RecordingId: _recordingId);

        var act = async () => await sut.ScheduleMeetingAsync(_userId, req);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ScheduleMeeting_RecordingBelongsToOtherUser_ThrowsForbidden()
    {
        var otherUserId = Guid.NewGuid();
        var recording = new Recording
        {
            Id = _recordingId,
            UserId = otherUserId,
            Title = "Confidential meeting",
            Summary = "Secret data"
        };

        _recRepo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Recording, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(recording);

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Contract Discussion",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00",
            RecordingId: _recordingId);

        var act = async () => await sut.ScheduleMeetingAsync(_userId, req);

        var ex = await act.Should().ThrowAsync<AppException>();
        ex.Which.StatusCode.Should().Be(403);
        ex.Which.ErrorCode.Should().Be("RECORDING_ACCESS_DENIED");
    }
}
