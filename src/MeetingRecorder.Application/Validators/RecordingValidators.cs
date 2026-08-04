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
        RuleFor(x => x.Transcript).NotNull().When(x => x.Transcript is not null);
        RuleFor(x => x.Transcript).Must(t => t!.Count <= 2000).When(x => x.Transcript is not null)
            .WithMessage("Transcript cannot contain more than 2000 lines.");
        RuleForEach(x => x.Transcript).ChildRules(line =>
        {
            line.RuleFor(l => l.Speaker).MaximumLength(200);
            line.RuleFor(l => l.Text).MaximumLength(5000);
            line.RuleFor(l => l.TimestampSeconds).GreaterThanOrEqualTo(0).When(l => l.TimestampSeconds.HasValue);
        });
        RuleFor(x => x.Actions).Must(a => a == null || a.Count <= 500).WithMessage("Actions cannot contain more than 500 items.");
        RuleForEach(x => x.Actions).ChildRules(action =>
        {
            action.RuleFor(a => a.Text).NotEmpty().MaximumLength(500);
        });
        RuleFor(x => x.Notes).Must(n => n == null || n.Count <= 1000).WithMessage("Notes cannot contain more than 1000 items.");
        RuleForEach(x => x.Notes).ChildRules(note =>
        {
            note.RuleFor(n => n.EndSeconds).GreaterThanOrEqualTo(n => n.StartSeconds);
            note.RuleFor(n => n.Text).MaximumLength(2000);
        });
        RuleFor(x => x.FilePath).MaximumLength(1000);
        RuleFor(x => x.SourceLanguageCode).MaximumLength(16);
        RuleFor(x => x.TranscriptionStatus).IsInEnum().When(x => x.TranscriptionStatus.HasValue);
        RuleFor(x => x.Duration).GreaterThanOrEqualTo(TimeSpan.Zero).When(x => x.Duration.HasValue);
        RuleFor(x => x.DurationSeconds).GreaterThanOrEqualTo(0).When(x => x.DurationSeconds.HasValue);
    }
}

public class UpdateRecordingValidator : AbstractValidator<DTOs.UpdateRecordingRequest>
{
    public UpdateRecordingValidator()
    {
        RuleFor(x => x.Title).MaximumLength(200).When(x => x.Title is not null);
        RuleFor(x => x.Type).IsInEnum().When(x => x.Type.HasValue);
        RuleFor(x => x.Summary).MaximumLength(4000).When(x => x.Summary is not null);
        RuleFor(x => x.Transcript).Must(t => t!.Count <= 2000).When(x => x.Transcript is not null)
            .WithMessage("Transcript cannot contain more than 2000 lines.");
        RuleForEach(x => x.Transcript).ChildRules(line =>
        {
            line.RuleFor(l => l.Speaker).MaximumLength(200);
            line.RuleFor(l => l.Text).MaximumLength(5000);
            line.RuleFor(l => l.TimestampSeconds).GreaterThanOrEqualTo(0).When(l => l.TimestampSeconds.HasValue);
        }).When(x => x.Transcript is not null);
        RuleFor(x => x.Actions).Must(a => a == null || a.Count <= 500).WithMessage("Actions cannot contain more than 500 items.");
        RuleForEach(x => x.Actions).ChildRules(action =>
        {
            action.RuleFor(a => a.Text).NotEmpty().MaximumLength(500);
        }).When(x => x.Actions is not null);
        RuleFor(x => x.Notes).Must(n => n == null || n.Count <= 1000).WithMessage("Notes cannot contain more than 1000 items.");
        RuleForEach(x => x.Notes).ChildRules(note =>
        {
            note.RuleFor(n => n.EndSeconds).GreaterThanOrEqualTo(n => n.StartSeconds);
            note.RuleFor(n => n.Text).MaximumLength(2000);
        }).When(x => x.Notes is not null);
        RuleFor(x => x.FilePath).MaximumLength(1000).When(x => x.FilePath is not null);
        RuleFor(x => x.SourceLanguageCode).MaximumLength(16).When(x => x.SourceLanguageCode is not null);
        RuleFor(x => x.TranscriptionStatus).IsInEnum().When(x => x.TranscriptionStatus.HasValue);
        RuleFor(x => x.Duration).GreaterThanOrEqualTo(TimeSpan.Zero).When(x => x.Duration.HasValue);
    }
}

public class BatchRecordingsValidator : AbstractValidator<DTOs.BatchRecordingsRequest>
{
    public const int MaxBatchSize = 100;

    public BatchRecordingsValidator()
    {
        RuleFor(x => x.Ids)
            .NotNull()
            .WithMessage("At least one recording id is required.")
            .NotEmpty()
            .WithMessage("At least one recording id is required.")
            .Must(ids => ids.Count <= MaxBatchSize)
            .WithMessage($"Maximum {MaxBatchSize} ids per batch.");
        RuleForEach(x => x.Ids).NotEmpty();
    }
}
