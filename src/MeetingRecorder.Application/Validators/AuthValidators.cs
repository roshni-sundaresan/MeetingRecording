using FluentValidation;

namespace MeetingRecorder.Application.Validators;

public class LoginValidator : AbstractValidator<DTOs.LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("'password' is required when provider_name, oauth_key, microsoft_auth, or google_auth is not provided.")
            .MaximumLength(128)
            .When(x => string.IsNullOrWhiteSpace(x.ProviderName) &&
                       string.IsNullOrWhiteSpace(x.OAuthKey) &&
                       string.IsNullOrWhiteSpace(x.MicrosoftAuth) &&
                       string.IsNullOrWhiteSpace(x.GoogleAuth));

        RuleFor(x => x.ProviderName)
            .MaximumLength(50);

        RuleFor(x => x.OAuthKey)
            .MaximumLength(8000);

        RuleFor(x => x.MicrosoftAuth)
            .MaximumLength(8000);

        RuleFor(x => x.GoogleAuth)
            .MaximumLength(8000);
    }
}

public class PasswordResetRequestValidator : AbstractValidator<DTOs.PasswordResetRequestRequest>
{
    public PasswordResetRequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

public class VerifyOtpValidator : AbstractValidator<DTOs.VerifyOtpRequest>
{
    public VerifyOtpValidator()
    {
        RuleFor(x => x.ResetRequestId)
            .NotEmpty()
            .MaximumLength(64)
            .When(x => string.IsNullOrWhiteSpace(x.Email))
            .WithMessage("Either ResetRequestId or Email must be provided.");

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320)
            .When(x => string.IsNullOrWhiteSpace(x.ResetRequestId))
            .WithMessage("Either ResetRequestId or a valid Email must be provided.");

        RuleFor(x => x.Otp).NotEmpty().Matches(@"^\d{6}$").WithMessage("OTP must be a 6-digit code.");
    }
}

public class ResendOtpValidator : AbstractValidator<DTOs.ResendOtpRequest>
{
    public ResendOtpValidator()
    {
        RuleFor(x => x.Username).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

public class CompleteResetValidator : AbstractValidator<DTOs.CompleteResetRequest>
{
    public CompleteResetValidator()
    {
        RuleFor(x => x.ResetToken)
            .NotEmpty()
            .MaximumLength(512)
            .When(x => string.IsNullOrWhiteSpace(x.Email) || string.IsNullOrWhiteSpace(x.Otp))
            .WithMessage("Either ResetToken, or Email and OTP, must be provided.");

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320)
            .When(x => string.IsNullOrWhiteSpace(x.ResetToken))
            .WithMessage("Either ResetToken or Email must be provided.");

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
