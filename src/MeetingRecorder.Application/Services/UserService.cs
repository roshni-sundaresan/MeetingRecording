using AutoMapper;
using MeetingRecorder.Application.DTOs;
using MeetingRecorder.Application.DTOs.Common;
using MeetingRecorder.Application.Exceptions;
using MeetingRecorder.Application.Interfaces;
using MeetingRecorder.Domain.Entities;

namespace MeetingRecorder.Application.Services;

public interface IUserService
{
    Task<PagedResult<UserResponse>> GetUsersAsync(QueryParameters query, CancellationToken ct = default);
    Task<UserResponse> GetUserAsync(Guid id, CancellationToken ct = default);
    Task<UserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserResponse> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);
    Task DeleteUserAsync(Guid id, CancellationToken ct = default);
    Task<UserApiKeyStatusResponse> SetApiKeyAsync(Guid userId, SetApiKeyRequest request, CancellationToken ct = default);
    Task<UserApiKeyStatusResponse> GetApiKeyStatusAsync(Guid userId, CancellationToken ct = default);
    Task<UserApiKeyStatusResponse> ResetApiKeyAsync(Guid userId, CancellationToken ct = default);
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

        user.Name = request.Name.Trim();
        user.Mobile = request.Mobile.Trim();
        user.ProfilePhotoUrl = request.ProfilePhotoUrl;
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

    public async Task<UserApiKeyStatusResponse> SetApiKeyAsync(Guid userId, SetApiKeyRequest request, CancellationToken ct = default)
    {
        var user = await _uow.Repository<User>().FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
            ?? throw new NotFoundException(nameof(User), userId);

        var key = request.ApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            throw new AppException("API key cannot be empty.", 400, "VALIDATION_ERROR");

        if (request.Validate && _sarvamApiService != null)
        {
            var isValid = await _sarvamApiService.ValidateApiKeyAsync(key, ct);
            if (!isValid)
            {
                throw new AppException("The provided Sarvam API key is invalid or unauthorized.", 400, "INVALID_API_KEY");
            }
        }

        user.CustomApiKey = key;
        user.UpdatedDate = DateTime.UtcNow;

        _uow.Repository<User>().Update(user);
        await _uow.SaveChangesAsync(ct);

        return BuildApiKeyStatus(user);
    }

    public async Task<UserApiKeyStatusResponse> GetApiKeyStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _uow.Repository<User>().FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
            ?? throw new NotFoundException(nameof(User), userId);

        return BuildApiKeyStatus(user);
    }

    public async Task<UserApiKeyStatusResponse> ResetApiKeyAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _uow.Repository<User>().FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
            ?? throw new NotFoundException(nameof(User), userId);

        user.CustomApiKey = null;
        user.UpdatedDate = DateTime.UtcNow;

        _uow.Repository<User>().Update(user);
        await _uow.SaveChangesAsync(ct);

        return BuildApiKeyStatus(user);
    }

    private static UserApiKeyStatusResponse BuildApiKeyStatus(User user)
    {
        var hasCustomKey = !string.IsNullOrWhiteSpace(user.CustomApiKey);
        var maskedKey = hasCustomKey ? MaskApiKey(user.CustomApiKey!) : null;
        var keySource = hasCustomKey ? "custom" : "system";

        return new UserApiKeyStatusResponse(
            HasCustomKey: hasCustomKey,
            MaskedKey: maskedKey,
            KeySource: keySource,
            IsSystemKeyConfigured: true,
            UpdatedDate: user.UpdatedDate);
    }

    private static string MaskApiKey(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length <= 8)
            return new string('*', trimmed.Length);

        var prefixLen = Math.Min(6, trimmed.Length / 3);
        var suffixLen = Math.Min(4, trimmed.Length / 3);
        var prefix = trimmed[..prefixLen];
        var suffix = trimmed[^suffixLen..];
        return $"{prefix}****{suffix}";
    }
}
