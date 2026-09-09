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
        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email must be a valid email address.")
            .MaximumLength(320).WithMessage("Email must not exceed 320 characters.")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty.")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters.")
            .When(x => x.Name != null);

        RuleFor(x => x.Mobile)
            .Matches(@"^[0-9+\-\s]{7,15}$").WithMessage("Mobile must be a valid phone number (7-15 digits).")
            .When(x => !string.IsNullOrWhiteSpace(x.Mobile));

        RuleFor(x => x.Password)
            .MinimumLength(8).WithMessage("Password must be at least 8 characters long.")
            .MaximumLength(128).WithMessage("Password must not exceed 128 characters.")
            .When(x => !string.IsNullOrWhiteSpace(x.Password));

        RuleFor(x => x.ProfilePhotoUrl)
            .MaximumLength(1000).WithMessage("Profile photo URL must not exceed 1000 characters.")
            .When(x => x.ProfilePhotoUrl != null);
    }
}

public class UpdateProfileValidator : AbstractValidator<DTOs.UpdateProfileRequest>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email must be a valid email address.")
            .MaximumLength(320).WithMessage("Email must not exceed 320 characters.")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty.")
            .MaximumLength(100).WithMessage("Name must not exceed 100 characters.")
            .When(x => x.Name != null);

        RuleFor(x => x.Mobile)
            .Matches(@"^[0-9+\-\s]{7,15}$").WithMessage("Mobile must be a valid phone number (7-15 digits).")
            .When(x => !string.IsNullOrWhiteSpace(x.Mobile));

        RuleFor(x => x.Password)
            .MinimumLength(8).WithMessage("Password must be at least 8 characters long.")
            .MaximumLength(128).WithMessage("Password must not exceed 128 characters.")
            .When(x => !string.IsNullOrWhiteSpace(x.Password));

        RuleFor(x => x.ProfilePhotoUrl)
            .MaximumLength(1000).WithMessage("Profile photo URL must not exceed 1000 characters.")
            .When(x => x.ProfilePhotoUrl != null);
    }
}

public class SetApiKeyValidator : AbstractValidator<DTOs.SetApiKeyRequest>
{
    public SetApiKeyValidator()
    {
        RuleFor(x => x.ApiKey)
            .NotEmpty().WithMessage("API key is required.")
            .MinimumLength(8).WithMessage("API key must be at least 8 characters long.")
            .MaximumLength(500).WithMessage("API key must not exceed 500 characters.");

        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email must be a valid email address.")
            .MaximumLength(320).WithMessage("Email must not exceed 320 characters.")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public class ValidateApiKeyValidator : AbstractValidator<DTOs.ValidateApiKeyRequest>
{
    public ValidateApiKeyValidator()
    {
        RuleFor(x => x.ApiKey)
            .NotEmpty().WithMessage("API key is required.")
            .MinimumLength(8).WithMessage("API key must be at least 8 characters long.")
            .MaximumLength(500).WithMessage("API key must not exceed 500 characters.");
    }
}
