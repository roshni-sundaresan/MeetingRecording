using FluentValidation;

namespace MeetingRecorder.Application.Validators;

public class StartUploadValidator : AbstractValidator<DTOs.StartUploadRequest>
{
    public StartUploadValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.TotalChunks).InclusiveBetween(1, 10_000);
        RuleFor(x => x.SourceLanguageCode).MaximumLength(16);
        RuleFor(x => x.FileSizeBytes).GreaterThan(0).When(x => x.FileSizeBytes.HasValue);
        RuleFor(x => x.Duration).GreaterThanOrEqualTo(TimeSpan.Zero).When(x => x.Duration.HasValue);
        RuleFor(x => x.DurationSeconds).GreaterThanOrEqualTo(0).When(x => x.DurationSeconds.HasValue);
    }
}

public class UploadChunkValidator : AbstractValidator<DTOs.UploadChunkRequest>
{
    public UploadChunkValidator()
    {
        RuleFor(x => x.BatchId).NotEmpty();
        RuleFor(x => x.ChunkNumber).GreaterThanOrEqualTo(1);
        RuleFor(x => x.TotalChunks).GreaterThanOrEqualTo(1);
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Summary).MaximumLength(4000);
        RuleFor(x => x.Transcript).MaximumLength(20000);
        RuleFor(x => x.Actions).MaximumLength(4000);
        RuleFor(x => x.Notes).MaximumLength(4000);
    }
}

public class CompleteUploadValidator : AbstractValidator<Guid>
{
    public CompleteUploadValidator()
    {
        RuleFor(x => x).NotEmpty();
    }
}
