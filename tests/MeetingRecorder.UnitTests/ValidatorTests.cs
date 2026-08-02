using FluentAssertions;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Validators;
using MeetingRecorder.Domain;

namespace MeetingRecorder.UnitTests;

public class ValidatorTests
{
    [Fact]
    public async Task LoginValidator_InvalidEmail_Fails()
    {
        var validator = new LoginValidator();
        var result = await validator.ValidateAsync(new LoginRequest("not-an-email", "secret"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(LoginRequest.Email));
    }

    [Fact]
    public async Task RegisterValidator_ShortPassword_Fails()
    {
        var validator = new RegisterValidator();
        var result = await validator.ValidateAsync(new RegisterRequest("a@b.com", "A", "1234567890", "short", null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(RegisterRequest.Password));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("abcdefghijklmnopqrstuvwxyz12345678901234567890")]
    public async Task RegisterValidator_InvalidMobile_Fails(string mobile)
    {
        var validator = new RegisterValidator();
        var result = await validator.ValidateAsync(new RegisterRequest("a@b.com", "A", mobile, "Passw0rd!", null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(RegisterRequest.Mobile));
    }

    [Fact]
    public async Task CreateRecordingValidator_EmptyTitle_Fails()
    {
        var validator = new CreateRecordingValidator();
        var result = await validator.ValidateAsync(new CreateRecordingRequest(
            Guid.NewGuid(), "  ", RecordingType.Audio, null, null, null, null, null, null, false, false, null, "en", null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateRecordingRequest.Title));
    }

    [Fact]
    public async Task StartUploadValidator_ZeroChunks_Fails()
    {
        var validator = new StartUploadValidator();
        var result = await validator.ValidateAsync(new StartUploadRequest(Guid.NewGuid(), "a.mp4", RecordingType.Video, 0, "en", 100, null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(StartUploadRequest.TotalChunks));
    }

    [Fact]
    public async Task UploadChunkValidator_InvalidChunkNumber_Fails()
    {
        var validator = new UploadChunkValidator();
        var result = await validator.ValidateAsync(new UploadChunkRequest(Guid.NewGuid(), 0, 5, Guid.NewGuid(), null, null, null, null, null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UploadChunkRequest.ChunkNumber));
    }

    [Fact]
    public async Task ValidRequests_Pass()
    {
        (await new LoginValidator().ValidateAsync(new LoginRequest("a@b.com", "secret123"))).IsValid.Should().BeTrue();
        (await new RegisterValidator().ValidateAsync(new RegisterRequest("a@b.com", "Alice", "+91 90000 00000", "Passw0rd!", null))).IsValid.Should().BeTrue();
        (await new StartUploadValidator().ValidateAsync(new StartUploadRequest(Guid.NewGuid(), "meeting.mp4", RecordingType.Meeting, 4, "en", 400, null))).IsValid.Should().BeTrue();
    }
}
