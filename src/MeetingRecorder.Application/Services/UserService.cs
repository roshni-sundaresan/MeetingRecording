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
}

public class UserService : IUserService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;
    private readonly IPasswordHasher _passwordHasher;

    public UserService(IUnitOfWork uow, IMapper mapper, IPasswordHasher passwordHasher)
    {
        _uow = uow;
        _mapper = mapper;
        _passwordHasher = passwordHasher;
    }

    public Task<PagedResult<UserResponse>> GetUsersAsync(QueryParameters query, CancellationToken ct = default)
    {
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
        await _uow.SaveChangesAsync(ct);
    }
}
