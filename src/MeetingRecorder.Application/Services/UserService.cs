using AutoMapper;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain;
using MeetingRecorder.Domain.Constants;
using MeetingRecorder.Domain.Entities;

namespace MeetingRecorder.Application.Services;

public interface IUserService
{
    Task<PagedResult<UserResponse>> GetUsersAsync(QueryParameters query, CancellationToken ct = default);
    Task<UserResponse> GetUserAsync(Guid id, CancellationToken ct = default);
    Task<UserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserResponse> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);
    Task DeleteUserAsync(Guid id, CancellationToken ct = default);
    Task<UserApiKeyStatusResponse> SetApiKeyAsync(Guid? userId, SetApiKeyRequest request, CancellationToken ct = default);
    Task<UserApiKeyStatusResponse> GetApiKeyStatusAsync(Guid? userId, string? email = null, CancellationToken ct = default);
    Task<UserApiKeyStatusResponse> ResetApiKeyAsync(Guid? userId, string? email = null, CancellationToken ct = default);
}

public class UserService : IUserService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISarvamApiService? _sarvamApiService;

    public UserService(
        IUnitOfWork uow,
        IMapper mapper,
        IPasswordHasher passwordHasher,
        ISarvamApiService? sarvamApiService = null)
    {
        _uow = uow;
        _mapper = mapper;
        _passwordHasher = passwordHasher;
        _sarvamApiService = sarvamApiService;
    }

    public Task<PagedResult<UserResponse>> GetUsersAsync(QueryParameters query, CancellationToken ct = default)
    {
        QueryGuard.Validate(query, QueryGuard.AllowedUserSort);

        var repo = _uow.Repository<User>();
        var baseQuery = repo.Query().Where(u => !u.IsDeleted);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            baseQuery = baseQuery.Where(u =>
                u.Name.Contains(term) || u.Email.Contains(term) || u.Mobile.Contains(term));
        }

        var total = baseQuery.Count();   // count respects the search filter

        // Sort whitelist — user input is never interpolated into expressions.
        var sorted = (query.SortBy?.ToLowerInvariant()) switch
        {
            "name" => query.SortOrder == "desc" ? baseQuery.OrderByDescending(u => u.Name) : baseQuery.OrderBy(u => u.Name),
            "email" => query.SortOrder == "desc" ? baseQuery.OrderByDescending(u => u.Email) : baseQuery.OrderBy(u => u.Email),
            _ => query.SortOrder == "desc" ? baseQuery.OrderByDescending(u => u.CreatedDate) : baseQuery.OrderBy(u => u.CreatedDate)
        };

        var items = _mapper.Map<List<UserResponse>>(
            sorted.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList());

        return Task.FromResult(new PagedResult<UserResponse>
        {
            Items = items, Page = query.Page, PageSize = query.PageSize, TotalCount = total
        });
    }

    public async Task<UserResponse> GetUserAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _uow.Repository<User>().FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
        return _mapper.Map<UserResponse>(user ?? throw new NotFoundException(nameof(User), id));
    }

    public async Task<UserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        var email = request.Email.ToLowerInvariant().Trim();
        if (await _uow.Repository<User>().AnyAsync(u => u.Email == email && !u.IsDeleted, ct))
            throw new ConflictException($"A user with email '{email}' already exists.");

        var user = new User
        {
            Email = email,
            Name = request.Name.Trim(),
            Mobile = request.Mobile.Trim(),
            ProfilePhotoUrl = request.ProfilePhotoUrl,
            PasswordHash = _passwordHasher.Hash(request.Password),
            Role = Domain.Constants.Roles.User
        };

        _uow.Repository<User>().Add(user);
        await _uow.SaveChangesAsync(ct);
        return _mapper.Map<UserResponse>(user);
    }

    public async Task<UserResponse> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await _uow.Repository<User>().FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct)
            ?? throw new NotFoundException(nameof(User), id);

        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            var email = request.Email.ToLowerInvariant().Trim();
            if (email != user.Email)
            {
                if (await _uow.Repository<User>().AnyAsync(u => u.Email == email && u.Id != id && !u.IsDeleted, ct))
                    throw new ConflictException($"A user with email '{email}' already exists.");
                user.Email = email;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            user.Name = request.Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Mobile))
        {
            user.Mobile = request.Mobile.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            user.PasswordHash = _passwordHasher.Hash(request.Password);
        }

        if (request.ProfilePhotoUrl != null)
        {
            user.ProfilePhotoUrl = request.ProfilePhotoUrl;
        }

        user.UpdatedDate = DateTime.UtcNow;

        _uow.Repository<User>().Update(user);
        await _uow.SaveChangesAsync(ct);
        return _mapper.Map<UserResponse>(user);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _uow.Repository<User>().FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct)
            ?? throw new NotFoundException(nameof(User), id);

        user.IsDeleted = true;   // soft delete
        user.UpdatedDate = DateTime.UtcNow;
        _uow.Repository<User>().Update(user);

        // Cascade (soft): the user's recordings disappear from lists/streams
        // and all their sessions are revoked immediately.
        var recordings = _uow.Repository<Recording>().Query()
            .Where(r => r.UserId == id && !r.IsDeleted).ToList();
        foreach (var rec in recordings)
        {
            rec.IsDeleted = true;
            rec.UpdatedDate = DateTime.UtcNow;
            _uow.Repository<Recording>().Update(rec);
        }

        var tokens = _uow.Repository<RefreshToken>().Query()
            .Where(t => t.UserId == id && t.RevokedAt == null).ToList();
        foreach (var token in tokens)
        {
            token.RevokedAt = DateTime.UtcNow;
            _uow.Repository<RefreshToken>().Update(token);
        }

        await _uow.SaveChangesAsync(ct);
    }

    public async Task<UserApiKeyStatusResponse> SetApiKeyAsync(Guid? userId, SetApiKeyRequest request, CancellationToken ct = default)
    {
        var key = request.ApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            throw new AppException("API key cannot be empty.", 400, "VALIDATION_ERROR");

        if (request.Validate && _sarvamApiService != null)
        {
            var valResult = await _sarvamApiService.ValidateApiKeyWithDetailsAsync(key, ct);
            if (!valResult.IsValid)
            {
                throw new AppException(valResult.Message, 400, valResult.ErrorCode ?? "INVALID_API_KEY");
            }
        }

        var userRepo = _uow.Repository<User>();
        User? user = null;

        // 1. If email is provided, lookup or create user by email
        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            var cleanEmail = request.Email.Trim().ToLowerInvariant();
            user = await userRepo.FirstOrDefaultAsync(u => u.Email.ToLower() == cleanEmail && !u.IsDeleted, ct);
            if (user == null)
            {
                user = new User
                {
                    Email = cleanEmail,
                    Name = cleanEmail.Split('@')[0],
                    Mobile = string.Empty,
                    PasswordHash = _passwordHasher.Hash(Guid.NewGuid().ToString("N")),
                    Role = Roles.User,
                    CustomApiKey = key,
                    CreatedDate = DateTime.UtcNow,
                    UpdatedDate = DateTime.UtcNow
                };
                userRepo.Add(user);
                await _uow.SaveChangesAsync(ct);
                return BuildApiKeyStatus(user);
            }
        }
        // 2. Otherwise fall back to authenticated userId
        else if (userId.HasValue && userId.Value != Guid.Empty)
        {
            user = await userRepo.FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        }

        if (user == null)
        {
            throw new AppException("User not found. Please provide an email or authenticate.", 404, "NOT_FOUND");
        }

        user.CustomApiKey = key;
        user.UpdatedDate = DateTime.UtcNow;

        userRepo.Update(user);
        await _uow.SaveChangesAsync(ct);

        return BuildApiKeyStatus(user);
    }

    public async Task<UserApiKeyStatusResponse> GetApiKeyStatusAsync(Guid? userId, string? email = null, CancellationToken ct = default)
    {
        var userRepo = _uow.Repository<User>();
        User? user = null;

        if (!string.IsNullOrWhiteSpace(email))
        {
            var cleanEmail = email.Trim().ToLowerInvariant();
            user = await userRepo.FirstOrDefaultAsync(u => u.Email.ToLower() == cleanEmail && !u.IsDeleted, ct);
        }
        else if (userId.HasValue && userId.Value != Guid.Empty)
        {
            user = await userRepo.FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        }

        if (user == null)
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                return new UserApiKeyStatusResponse(
                    HasCustomKey: false,
                    MaskedKey: null,
                    KeySource: "system",
                    IsSystemKeyConfigured: true,
                    UpdatedDate: null,
                    ApiKey: null,
                    Email: email.Trim());
            }

            throw new NotFoundException(nameof(User), userId ?? Guid.Empty);
        }

        return BuildApiKeyStatus(user);
    }

    public async Task<UserApiKeyStatusResponse> ResetApiKeyAsync(Guid? userId, string? email = null, CancellationToken ct = default)
    {
        var userRepo = _uow.Repository<User>();
        User? user = null;

        if (!string.IsNullOrWhiteSpace(email))
        {
            var cleanEmail = email.Trim().ToLowerInvariant();
            user = await userRepo.FirstOrDefaultAsync(u => u.Email.ToLower() == cleanEmail && !u.IsDeleted, ct);
        }
        else if (userId.HasValue && userId.Value != Guid.Empty)
        {
            user = await userRepo.FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        }

        if (user == null)
            throw new NotFoundException(nameof(User), userId ?? Guid.Empty);

        user.CustomApiKey = null;
        user.UpdatedDate = DateTime.UtcNow;

        userRepo.Update(user);
        await _uow.SaveChangesAsync(ct);

        return BuildApiKeyStatus(user);
    }

    private static UserApiKeyStatusResponse BuildApiKeyStatus(User user)
    {
        var hasCustomKey = !string.IsNullOrWhiteSpace(user.CustomApiKey);
        // Return stored API key directly in masked_key field as requested
        var maskedKey = hasCustomKey ? user.CustomApiKey : null;
        var keySource = hasCustomKey ? "custom" : "system";

        return new UserApiKeyStatusResponse(
            HasCustomKey: hasCustomKey,
            MaskedKey: maskedKey,
            KeySource: keySource,
            IsSystemKeyConfigured: true,
            UpdatedDate: user.UpdatedDate,
            ApiKey: user.CustomApiKey,
            Email: user.Email);
    }
}
