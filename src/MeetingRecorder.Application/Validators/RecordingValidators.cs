using FluentValidation;

namespace MeetingRecorder.Application.Validators;

public class CreateRecordingValidator : AbstractValidator<DTOs.CreateRecordingRequest>
{
    public CreateRecordingValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Summary).MaximumLength(4000);
        RuleFor(x => x.Transcript).MaximumLength(20000);
        RuleFor(x => x.Actions).MaximumLength(4000);
        RuleFor(x => x.Notes).MaximumLength(4000);
        RuleFor(x => x.FilePath).MaximumLength(1000);
        RuleFor(x => x.SourceLanguageCode).MaximumLength(16);
        RuleFor(x => x.TranscriptionStatus).IsInEnum().When(x => x.TranscriptionStatus.HasValue);
        RuleFor(x => x.Duration).GreaterThanOrEqualTo(TimeSpan.Zero).When(x => x.Duration.HasValue);
    }
}

public class UpdateRecordingValidator : AbstractValidator<DTOs.UpdateRecordingRequest>
{
    public UpdateRecordingValidator()
    {
        RuleFor(x => x.Title).MaximumLength(200).When(x => x.Title is not null);
        RuleFor(x => x.Type).IsInEnum().When(x => x.Type.HasValue);
        RuleFor(x => x.Summary).MaximumLength(4000).When(x => x.Summary is not null);
        RuleFor(x => x.Transcript).MaximumLength(20000).When(x => x.Transcript is not null);
        RuleFor(x => x.Actions).MaximumLength(4000).When(x => x.Actions is not null);
        RuleFor(x => x.Notes).MaximumLength(4000).When(x => x.Notes is not null);
        RuleFor(x => x.FilePath).MaximumLength(1000).When(x => x.FilePath is not null);
        RuleFor(x => x.SourceLanguageCode).MaximumLength(16).When(x => x.SourceLanguageCode is not null);
        RuleFor(x => x.TranscriptionStatus).IsInEnum().When(x => x.TranscriptionStatus.HasValue);
        RuleFor(x => x.Duration).GreaterThanOrEqualTo(TimeSpan.Zero).When(x => x.Duration.HasValue);
    }
}
