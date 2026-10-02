using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;

namespace NeuralBridge.Infrastructure.Identity;

/// <summary>
/// Process-memory user data for the InMemory persistence provider (demos, tests). Accounts are
/// lost on restart. Use SQLite or PostgreSQL for real deployments.
/// </summary>
public sealed class InMemoryUserDatabase
{
    internal ConcurrentDictionary<string, ApplicationUser> Users { get; } = new(StringComparer.Ordinal);

    internal ConcurrentDictionary<string, List<UserLoginInfo>> Logins { get; } = new(StringComparer.Ordinal);

    internal Lock Gate { get; } = new();
}

/// <summary>ASP.NET Core Identity store over <see cref="InMemoryUserDatabase"/> (passwords, emails, external logins, lockout, security stamps).</summary>
public sealed class InMemoryUserStore :
    IUserPasswordStore<ApplicationUser>,
    IUserEmailStore<ApplicationUser>,
    IUserLoginStore<ApplicationUser>,
    IUserSecurityStampStore<ApplicationUser>,
    IUserLockoutStore<ApplicationUser>
{
    private readonly InMemoryUserDatabase _db;

    public InMemoryUserStore(InMemoryUserDatabase db) => _db = db;

    public void Dispose()
    {
    }

    // ---- IUserStore
    public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);

    public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken)
    {
        user.UserName = userName;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        lock (_db.Gate)
        {
            if (_db.Users.Values.Any(u => u.NormalizedUserName == user.NormalizedUserName))
            {
                return Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = "That account already exists." }));
            }

            _db.Users[user.Id] = user;
            _db.Logins.TryAdd(user.Id, []);
            return Task.FromResult(IdentityResult.Success);
        }
    }

    public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        lock (_db.Gate)
        {
            if (!_db.Users.ContainsKey(user.Id))
            {
                return Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "NotFound", Description = "Account not found." }));
            }

            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            _db.Users[user.Id] = user;
            return Task.FromResult(IdentityResult.Success);
        }
    }

    public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        lock (_db.Gate)
        {
            _db.Users.TryRemove(user.Id, out _);
            _db.Logins.TryRemove(user.Id, out _);
            return Task.FromResult(IdentityResult.Success);
        }
    }

    public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult(_db.Users.TryGetValue(userId, out var user) ? user : null);

    public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        Task.FromResult(_db.Users.Values.FirstOrDefault(u => u.NormalizedUserName == normalizedUserName));

    // ---- Password
    public Task SetPasswordHashAsync(ApplicationUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);

    public Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash is not null);

    // ---- Email
    public Task SetEmailAsync(ApplicationUser user, string? email, CancellationToken cancellationToken)
    {
        user.Email = email;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.Email);

    public Task<bool> GetEmailConfirmedAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.EmailConfirmed);

    public Task SetEmailConfirmedAsync(ApplicationUser user, bool confirmed, CancellationToken cancellationToken)
    {
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(_db.Users.Values.FirstOrDefault(u => u.NormalizedEmail == normalizedEmail));

    public Task<string?> GetNormalizedEmailAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedEmail);

    public Task SetNormalizedEmailAsync(ApplicationUser user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    // ---- External logins
    public Task AddLoginAsync(ApplicationUser user, UserLoginInfo login, CancellationToken cancellationToken)
    {
        lock (_db.Gate)
        {
            var logins = _db.Logins.GetOrAdd(user.Id, _ => []);
            if (!logins.Any(l => l.LoginProvider == login.LoginProvider && l.ProviderKey == login.ProviderKey))
            {
                logins.Add(login);
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveLoginAsync(ApplicationUser user, string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        lock (_db.Gate)
        {
            if (_db.Logins.TryGetValue(user.Id, out var logins))
            {
                logins.RemoveAll(l => l.LoginProvider == loginProvider && l.ProviderKey == providerKey);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IList<UserLoginInfo>> GetLoginsAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        lock (_db.Gate)
        {
            IList<UserLoginInfo> result = _db.Logins.TryGetValue(user.Id, out var logins) ? logins.ToList() : [];
            return Task.FromResult(result);
        }
    }

    public Task<ApplicationUser?> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        lock (_db.Gate)
        {
            var match = _db.Logins.FirstOrDefault(kv => kv.Value.Any(l => l.LoginProvider == loginProvider && l.ProviderKey == providerKey));
            return Task.FromResult(match.Key is null ? null : _db.Users.GetValueOrDefault(match.Key));
        }
    }

    // ---- Security stamp
    public Task SetSecurityStampAsync(ApplicationUser user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.SecurityStamp);

    // ---- Lockout
    public Task<DateTimeOffset?> GetLockoutEndDateAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(ApplicationUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(++user.AccessFailedCount);

    public Task ResetAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.AccessFailedCount);

    public Task<bool> GetLockoutEnabledAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnabled);

    public Task SetLockoutEnabledAsync(ApplicationUser user, bool enabled, CancellationToken cancellationToken)
    {
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }
}
