using System.Security.Cryptography;
using ComplaintPortal.Api.Repositories;
using ComplaintPortal.Api.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace ComplaintPortal.Api.Services;

public sealed record UserAccessState(bool IsActive, string SecurityStamp, string Role, bool MustChangePassword);

/// <summary>
/// Short-lived cache of per-user state and per-role permissions, so the security checks done on every
/// request don't hit the database each time. Admin changes evict the relevant entries immediately.
/// (For multiple API instances, swap IMemoryCache for a distributed cache or keep the TTL short.)
/// </summary>
public interface IAccessCache
{
    Task<UserAccessState?> GetUserStateAsync(int userId);
    Task<IReadOnlySet<string>> GetRolePermissionsAsync(string role);
    void InvalidateUser(int userId);
    void InvalidateAllRoles();
}

public sealed class AccessCache : IAccessCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopes;
    private CancellationTokenSource _rolesReset = new();

    public AccessCache(IMemoryCache cache, IServiceScopeFactory scopes)
    {
        _cache = cache;
        _scopes = scopes;
    }

    public async Task<UserAccessState?> GetUserStateAsync(int userId)
    {
        var key = UserKey(userId);
        if (_cache.TryGetValue(key, out UserAccessState? cached)) return cached;

        using var scope = _scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = await users.GetByIdAsync(userId);
        var state = user is null
            ? null
            : new UserAccessState(user.IsActive, user.SecurityStamp.ToString(), user.Role, user.MustChangePassword);

        _cache.Set(key, state, Ttl);
        return state;
    }

    public async Task<IReadOnlySet<string>> GetRolePermissionsAsync(string role)
    {
        // Admin always has every permission - it can never be locked out by a bad role edit.
        if (role == Roles.Admin) return Permissions.All;

        var key = $"role-perms:{role}";
        if (_cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached is not null) return cached;

        using var scope = _scopes.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var perms = (await roles.GetPermissionsAsync(role))
            .Where(Permissions.IsValid)                       // ignore anything unknown in the DB
            .ToHashSet(StringComparer.Ordinal);

        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl };
        options.AddExpirationToken(new CancellationChangeToken(_rolesReset.Token));
        _cache.Set<IReadOnlySet<string>>(key, perms, options);
        return perms;
    }

    public void InvalidateUser(int userId) => _cache.Remove(UserKey(userId));

    public void InvalidateAllRoles()
    {
        var old = Interlocked.Exchange(ref _rolesReset, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    private static string UserKey(int userId) => $"user-state:{userId}";
}

public static class PasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%^&*-_=+?";

    /// <summary>Cryptographically random password that always satisfies the password policy.</summary>
    public static string Generate(int length = 16)
    {
        var all = Upper + Lower + Digits + Symbols;
        var chars = new List<char>
        {
            Upper[RandomNumberGenerator.GetInt32(Upper.Length)],
            Lower[RandomNumberGenerator.GetInt32(Lower.Length)],
            Digits[RandomNumberGenerator.GetInt32(Digits.Length)],
            Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)],
        };
        while (chars.Count < length)
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        // Fisher-Yates shuffle so the guaranteed characters aren't always first.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }
}
