using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Constants;
using MeetingRecorder.Domain.Entities;

namespace MeetingRecorder.Application.Services;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
}

public class AuthService : IAuthService
{
    private readonly IUnitOfWork _uow;
    private readonly ITokenService _tokenService;
    private readonly IPasswordHasher _passwordHasher;

    public AuthService(IUnitOfWork uow, ITokenService tokenService, IPasswordHasher passwordHasher)
    {
        _uow = uow;
        _tokenService = tokenService;
        _passwordHasher = passwordHasher;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await _uow.Repository<User>()
            .FirstOrDefaultAsync(u => u.Email == request.Email.ToLowerInvariant().Trim() && !u.IsDeleted, ct);

        if (user is null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            throw new AppException("Invalid email or password.", 401);

        return BuildAuthResponse(user);
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var email = request.Email.ToLowerInvariant().Trim();
        var exists = await _uow.Repository<User>().AnyAsync(u => u.Email == email && !u.IsDeleted, ct);
        if (exists)
            throw new ConflictException($"A user with email '{email}' already exists.");

        var user = new User
        {
            Email = email,
            Name = request.Name.Trim(),
            Mobile = request.Mobile.Trim(),
            ProfilePhotoUrl = request.ProfilePhotoUrl,
            PasswordHash = _passwordHasher.Hash(request.Password),
            Role = Roles.User
        };

        _uow.Repository<User>().Add(user);
        await _uow.SaveChangesAsync(ct);

        return BuildAuthResponse(user);
    }

    private AuthResponse BuildAuthResponse(User user)
    {
        var (token, expiresAt) = _tokenService.GenerateToken(user.Id, user.Email, user.Name, user.Role);
        return new AuthResponse(token, expiresAt, new UserResponse(
            user.Id, user.Email, user.Name, user.Mobile, user.ProfilePhotoUrl, user.CreatedDate, user.UpdatedDate, user.Role));
    }
}
