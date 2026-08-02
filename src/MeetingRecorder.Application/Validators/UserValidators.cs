using FluentValidation;

namespace MeetingRecorder.Application.Validators;

public class CreateUserValidator : AbstractValidator<DTOs.CreateUserRequest>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^[0-9+\-\s]{7,15}$").WithMessage("Mobile must be a valid phone number (7-15 digits).");
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
        RuleFor(x => x.ProfilePhotoUrl).MaximumLength(1000);
    }
}

public class UpdateUserValidator : AbstractValidator<DTOs.UpdateUserRequest>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^[0-9+\-\s]{7,15}$").WithMessage("Mobile must be a valid phone number (7-15 digits).");
        RuleFor(x => x.ProfilePhotoUrl).MaximumLength(1000);
    }
}
