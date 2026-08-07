using FluentValidation;

namespace MeetingRecorder.Application.Validators;

public class LoginValidator : AbstractValidator<DTOs.LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public class ForgotPasswordValidator : AbstractValidator<DTOs.ForgotPasswordRequest>
{
    public ForgotPasswordValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

public class ResetPasswordValidator : AbstractValidator<DTOs.ResetPasswordRequest>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.ResetToken).NotEmpty().MaximumLength(256);
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

public class RegisterValidator : AbstractValidator<DTOs.RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^[0-9+\-\s]{7,15}$").WithMessage("Mobile must be a valid phone number (7-15 digits).");
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
        RuleFor(x => x.ProfilePhotoUrl).MaximumLength(1000);
    }
}

public class RefreshTokenValidator : AbstractValidator<DTOs.RefreshTokenRequest>
{
    public RefreshTokenValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
    }
}
