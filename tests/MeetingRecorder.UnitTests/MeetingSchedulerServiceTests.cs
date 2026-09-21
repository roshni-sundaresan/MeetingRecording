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
    public async Task ScheduleMeeting_WithHeaderAndMom_CombinesIntoCleanMinutesOfMeetingSection()
    {
        ScheduleMeetingRequest? capturedRequest = null;
        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduleMeetingRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new MeetingConferenceDetails("https://teams.microsoft.com/meet", "123-456", "pass", "ext-1"));

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Sprint 42 Architecture",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00",
            Header: "Sprint 42 Architecture Discussion",
            Mom: "* Point A\n* Point B");

        var result = await sut.ScheduleMeetingAsync(_userId, req);

        result.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Description.Should().Be("Sprint 42 Architecture Discussion\n\nMinutes of Meeting (MOM):\n• Point A\n• Point B");
    }

    [Fact]
    public async Task ScheduleMeeting_WithMomOnly_FormatsWithMinutesOfMeetingPrefix()
    {
        ScheduleMeetingRequest? capturedRequest = null;
        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduleMeetingRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new MeetingConferenceDetails("https://teams.microsoft.com/meet", "123-456", "pass", "ext-1"));

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "MOM Only Test",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00",
            Mom: "• Action item 1\n• Action item 2");

        var result = await sut.ScheduleMeetingAsync(_userId, req);

        result.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Description.Should().Be("Minutes of Meeting (MOM):\n• Action item 1\n• Action item 2");
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

    [Fact]
    public async Task ScheduleMeeting_ExpiredTokenWithRefreshToken_RefreshesTokenAndRetriesSuccessfully()
    {
        var user = new User
        {
            Id = _userId,
            Email = "user@example.com",
            MicrosoftOAuthKey = "expired-ms-token",
            MicrosoftRefreshToken = "valid-ms-refresh-token"
        };

        _userRepo.Setup(r => r.GetByIdAsync(_userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var callCount = 0;
        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .Returns<ScheduleMeetingRequest, CancellationToken>((req, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call fails with MICROSOFT_TOKEN_EXPIRED
                    throw new AppException("Token expired", 403, "MICROSOFT_TOKEN_EXPIRED");
                }
                // Second call (after refresh) succeeds
                return Task.FromResult(new MeetingConferenceDetails("https://teams.microsoft.com/retried-meet", "meet-code-99", null, "ext-id-99"));
            });

        _teamsClient.Setup(c => c.RefreshAccessTokenAsync("valid-ms-refresh-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("freshly-minted-ms-token");

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Token Refresh Test",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00");

        var result = await sut.ScheduleMeetingAsync(_userId, req);

        result.Should().NotBeNull();
        result.JoinUrl.Should().Be("https://teams.microsoft.com/retried-meet");
        callCount.Should().Be(2);

        // User should now have the freshly refreshed token persisted
        user.MicrosoftOAuthKey.Should().Be("freshly-minted-ms-token");
        user.OAuthKey.Should().Be("freshly-minted-ms-token");
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ScheduleMeeting_ExpiredTokenWithoutRefreshToken_ClearsTokenAndThrowsForbidden()
    {
        var user = new User
        {
            Id = _userId,
            Email = "user@example.com",
            MicrosoftOAuthKey = "expired-ms-token",
            MicrosoftRefreshToken = null
        };

        _userRepo.Setup(r => r.GetByIdAsync(_userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AppException("Lifetime validation failed", 403, "MICROSOFT_TOKEN_EXPIRED"));

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Token Refresh Fail Test",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00");

        var act = async () => await sut.ScheduleMeetingAsync(_userId, req);

        var ex = await act.Should().ThrowAsync<AppException>();
        ex.Which.StatusCode.Should().Be(403);
        ex.Which.ErrorCode.Should().Be("MICROSOFT_TOKEN_EXPIRED");

        // Expired token should be cleared from user
        user.MicrosoftOAuthKey.Should().BeNull();
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ScheduleMeeting_NoAccessTokenPassed_ProactivelyUsesRefreshTokenToFetchToken()
    {
        var user = new User
        {
            Id = _userId,
            Email = "user@example.com",
            MicrosoftOAuthKey = null,
            MicrosoftRefreshToken = "my-proactive-refresh-token"
        };

        _userRepo.Setup(r => r.GetByIdAsync(_userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        _teamsClient.Setup(c => c.RefreshAccessTokenAsync("my-proactive-refresh-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("proactively-acquired-token");

        ScheduleMeetingRequest? capturedRequest = null;
        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduleMeetingRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new MeetingConferenceDetails("https://teams.microsoft.com/proactive", "meet-code-pro", null, "ext-pro"));

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Proactive Refresh Test",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00");

        var result = await sut.ScheduleMeetingAsync(_userId, req);

        result.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ProviderAccessToken.Should().Be("proactively-acquired-token");
        user.MicrosoftOAuthKey.Should().Be("proactively-acquired-token");
    }

    [Fact]
    public async Task ScheduleMeeting_WithAuthCode_ExchangesCodeAndSavesTokensAndCreatesMeeting()
    {
        var user = new User
        {
            Id = _userId,
            Email = "user@example.com"
        };

        _userRepo.Setup(r => r.GetByIdAsync(_userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        _teamsClient.Setup(c => c.ExchangeAuthCodeAsync("one-time-ms-code", "postmessage", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthTokenResult("fresh-access-token", "fresh-refresh-token", 3600));

        ScheduleMeetingRequest? capturedRequest = null;
        _teamsClient.Setup(c => c.CreateMeetingAsync(It.IsAny<ScheduleMeetingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduleMeetingRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new MeetingConferenceDetails("https://teams.microsoft.com/from-code", "meet-code-code", null, "ext-code"));

        var sut = CreateSut();
        var req = new ScheduleMeetingRequest(
            Title: "Auth Code Schedule Test",
            Provider: MeetingProvider.Teams,
            StartTime: "2026-09-17T10:00:00",
            EndTime: "2026-09-17T11:00:00",
            MicrosoftAuthCode: "one-time-ms-code",
            RedirectUri: "postmessage");

        var result = await sut.ScheduleMeetingAsync(_userId, req);

        result.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ProviderAccessToken.Should().Be("fresh-access-token");

        user.MicrosoftOAuthKey.Should().Be("fresh-access-token");
        user.MicrosoftRefreshToken.Should().Be("fresh-refresh-token");
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExchangeOAuthCodeAsync_DedicatedEndpoint_ExchangesCodeAndSavesToUser()
    {
        var user = new User
        {
            Id = _userId,
            Email = "user@example.com"
        };

        _userRepo.Setup(r => r.GetByIdAsync(_userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        _teamsClient.Setup(c => c.ExchangeAuthCodeAsync("dedicated-code-xyz", "http://localhost:8080", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthTokenResult("dedicated-access-token", "dedicated-refresh-token", 3600));

        var sut = CreateSut();
        var req = new ExchangeOAuthCodeRequest(
            Provider: MeetingProvider.Teams,
            Code: "dedicated-code-xyz",
            RedirectUri: "http://localhost:8080");

        var result = await sut.ExchangeOAuthCodeAsync(_userId, req);

        result.Should().NotBeNull();
        result.AccessToken.Should().Be("dedicated-access-token");
        result.RefreshToken.Should().Be("dedicated-refresh-token");
        result.ExpiresIn.Should().Be(3600);

        user.MicrosoftOAuthKey.Should().Be("dedicated-access-token");
        user.MicrosoftRefreshToken.Should().Be("dedicated-refresh-token");
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}


