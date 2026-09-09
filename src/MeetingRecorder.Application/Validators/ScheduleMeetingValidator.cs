using FluentValidation;
using MeetingRecorder.Application.Common;
using MeetingRecorder.Application.DTOs;

namespace MeetingRecorder.Application.Validators;

public class ScheduleMeetingValidator : AbstractValidator<ScheduleMeetingRequest>
{
    public ScheduleMeetingValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("'title' is required.")
            .MaximumLength(200).WithMessage("'title' cannot exceed 200 characters.");

        RuleFor(x => x.Provider)
            .IsInEnum().WithMessage("'provider' must be a valid meeting provider (google_meet or teams).");

        RuleFor(x => x.StartTime)
            .NotEmpty().WithMessage("'start_time' is required.")
            .Must(BeValidTime).WithMessage("'start_time' is not a valid date-time format (expected format like 'YYYY-MM-DDTHH:mm:ss').");

        RuleFor(x => x.EndTime)
            .NotEmpty().WithMessage("'end_time' is required.")
            .Must(BeValidTime).WithMessage("'end_time' is not a valid date-time format (expected format like 'YYYY-MM-DDTHH:mm:ss').")
            .Must((req, endTime) =>
            {
                try
                {
                    var s = MeetingTimeHelper.Parse(req.StartTime, req.TimeZone);
                    var e = MeetingTimeHelper.Parse(endTime, req.TimeZone);
                    return e.UtcDateTime > s.UtcDateTime;
                }
                catch
                {
                    return false;
                }
            })
            .WithMessage("'end_time' must be after 'start_time'.");

        RuleFor(x => x.Description)
            .MaximumLength(4000).WithMessage("'description' cannot exceed 4000 characters.");

        RuleFor(x => x.TimeZone)
            .MaximumLength(100).WithMessage("'time_zone' cannot exceed 100 characters.");

        RuleFor(x => x.Attendees)
            .Must(a => a == null || a.Count <= 100)
            .WithMessage("Attendees list cannot exceed 100 participants.");

        RuleForEach(x => x.Attendees)
            .EmailAddress().WithMessage(attendee => $"'{attendee}' is not a valid email address.");

        RuleFor(x => x.MicrosoftAuth)
            .MaximumLength(8000).WithMessage("'microsoft_auth' cannot exceed 8000 characters.");

        RuleFor(x => x.GoogleAuth)
            .MaximumLength(8000).WithMessage("'google_auth' cannot exceed 8000 characters.");
    }

    private static bool BeValidTime(string timeStr)
    {
        if (string.IsNullOrWhiteSpace(timeStr)) return false;
        try
        {
            MeetingTimeHelper.Parse(timeStr, "UTC");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
