using ComplaintPortal.Api.Dtos;
using ComplaintPortal.Api.Models;
using ComplaintPortal.Api.Options;
using ComplaintPortal.Api.Repositories;
using ComplaintPortal.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ComplaintPortal.Api.Services;

public sealed record AuthResult(bool Succeeded, int StatusCode, string? Error = null,
    AuthResponse? Response = null, string? RefreshToken = null, DateTime? RefreshExpiresAtUtc = null)
{
    /// <summary>True when the browser already holds a newer cookie that must not be cleared.</summary>
    public bool PreserveCookie { get; init; }

    public static AuthResult Fail(int status, string error) => new(false, status, error);
}

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request, string? ip);
    Task<AuthResult> LoginAsync(LoginRequest request, string? ip);
    Task<AuthResult> RefreshAsync(string refreshToken, string? ip);
    Task LogoutAsync(string? refreshToken, int? userId, string? ip);
    Task<AuthResult> ChangePasswordAsync(int userId, ChangePasswordRequest request, string? ip);
    Task<UserDto> BuildUserDtoAsync(AppUser user);
}

public sealed class AuthService : IAuthService
{
    private const string InvalidCredentials = "Invalid email or password.";

    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IAuditRepository _audit;
    private readonly ITokenService _tokens;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly IAccessCache _access;
    private readonly JwtOptions _jwt;
    private readonly SecurityOptions _security;

    // Used to burn the same CPU time when the email does not exist (prevents user enumeration via timing).
    private static readonly Lazy<string> DummyHash =
        new(() => new PasswordHasher<AppUser>().HashPassword(new AppUser(), "dummy-password-for-timing-only"));

    public AuthService(IUserRepository users, IRefreshTokenRepository refreshTokens, IAuditRepository audit,
        ITokenService tokens, IPasswordHasher<AppUser> hasher, IAccessCache access,
        IOptions<JwtOptions> jwt, IOptions<SecurityOptions> security)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _audit = audit;
        _tokens = tokens;
        _hasher = hasher;
        _access = access;
        _jwt = jwt.Value;
        _security = security.Value;
    }

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, string? ip)
    {
        var email = NormalizeEmail(request.Email);

        if (await _users.EmailExistsAsync(email))
            return AuthResult.Fail(StatusCodes.Status409Conflict, "An account with this email already exists.");

        var user = new AppUser
        {
            FullName = request.FullName.Trim(),
            Email = email,
            Role = Roles.User, // self-registration can never grant Staff/Admin
        };
        user.PasswordHash = _hasher.HashPassword(user, request.Password);
        user.Id = await _users.CreateAsync(user);

        await _audit.LogAsync(user.Id, "auth.register", "User", user.Id.ToString(), ip);

        var created = await _users.GetByIdAsync(user.Id) ?? user;
        return await IssueTokensAsync(created, ip);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, string? ip)
    {
        var email = NormalizeEmail(request.Email);
        var user = await _users.GetByEmailAsync(email);

        if (user is null)
        {
            _hasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, request.Password);
            await _audit.LogAsync(null, "auth.login.failed", "User", null, ip, "Unknown email");
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, InvalidCredentials);
        }

        if (user.LockoutEndUtc is { } lockoutEnd && lockoutEnd > DateTime.UtcNow)
        {
            await _audit.LogAsync(user.Id, "auth.login.locked", "User", user.Id.ToString(), ip);
            return AuthResult.Fail(StatusCodes.Status423Locked,
                "This account is temporarily locked because of too many failed attempts. Please try again later.");
        }

        var verification = _hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            await _users.RecordFailedLoginAsync(user.Id, _security.MaxFailedLogins, _security.LockoutMinutes);
            await _audit.LogAsync(user.Id, "auth.login.failed", "User", user.Id.ToString(), ip, "Bad password");
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, InvalidCredentials);
        }

        if (!user.IsActive)
        {
            await _audit.LogAsync(user.Id, "auth.login.inactive", "User", user.Id.ToString(), ip);
            return AuthResult.Fail(StatusCodes.Status403Forbidden, "This account has been deactivated.");
        }

        // Transparently upgrade hashes created with older/weaker parameters.
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            await _users.UpdatePasswordAsync(user.Id, _hasher.HashPassword(user, request.Password), user.MustChangePassword);

        await _users.RecordSuccessfulLoginAsync(user.Id);
        await _audit.LogAsync(user.Id, "auth.login.success", "User", user.Id.ToString(), ip);

        var fresh = await _users.GetByIdAsync(user.Id) ?? user;
        return await IssueTokensAsync(fresh, ip);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, string? ip)
    {
        var hash = _tokens.HashToken(refreshToken);
        var stored = await _refreshTokens.GetByHashAsync(hash);

        if (stored is null)
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "Session expired. Please sign in again.");

        // Reuse detection: a revoked token being presented again means it was stolen.
        // A short grace window avoids false alarms when two browser tabs refresh at the same moment.
        if (stored.RevokedAtUtc is { } revokedAt)
        {
            if (stored.ReplacedByTokenHash is not null && DateTime.UtcNow - revokedAt < TimeSpan.FromSeconds(30))
                return AuthResult.Fail(StatusCodes.Status401Unauthorized, "Session was refreshed in another tab.")
                    with { PreserveCookie = true };

            await _refreshTokens.RevokeAllForUserAsync(stored.UserId);
            await _audit.LogAsync(stored.UserId, "auth.refresh.reuse_detected", "User", stored.UserId.ToString(), ip,
                "Revoked refresh token was reused; all sessions revoked.");
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "Session expired. Please sign in again.");
        }

        if (stored.ExpiresAtUtc <= DateTime.UtcNow)
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "Session expired. Please sign in again.");

        var user = await _users.GetByIdAsync(stored.UserId);
        if (user is null || !user.IsActive)
        {
            await _refreshTokens.RevokeAsync(hash);
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "Session expired. Please sign in again.");
        }

        var newToken = _tokens.GenerateRefreshToken();
        var newHash = _tokens.HashToken(newToken);
        var newExpiry = DateTime.UtcNow.AddDays(_jwt.RefreshTokenDays);

        if (!await _refreshTokens.RotateAsync(hash, newHash, user.Id, newExpiry, ip))
            return AuthResult.Fail(StatusCodes.Status401Unauthorized, "Session expired. Please sign in again.");

        var (access, accessExpiry) = _tokens.CreateAccessToken(user);
        return new AuthResult(true, StatusCodes.Status200OK, null,
            new AuthResponse(access, accessExpiry, await BuildUserDtoAsync(user)), newToken, newExpiry);
    }

    public async Task LogoutAsync(string? refreshToken, int? userId, string? ip)
    {
        if (!string.IsNullOrEmpty(refreshToken))
            await _refreshTokens.RevokeAsync(_tokens.HashToken(refreshToken));

        await _audit.LogAsync(userId, "auth.logout", "User", userId?.ToString(), ip);
    }

    public async Task<AuthResult> ChangePasswordAsync(int userId, ChangePasswordRequest request, string? ip)
    {
        var user = await _users.GetByIdAsync(userId);
        if (user is null)
            return AuthResult.Fail(StatusCodes.Status404NotFound, "User not found.");

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            await _audit.LogAsync(userId, "auth.password.change_failed", "User", userId.ToString(), ip);
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "Current password is incorrect.");
        }

        if (request.NewPassword == request.CurrentPassword)
            return AuthResult.Fail(StatusCodes.Status400BadRequest, "The new password must be different from the current one.");

        // Clears the "must change password" flag set by an admin-created account or password reset.
        await _users.UpdatePasswordAsync(userId, _hasher.HashPassword(user, request.NewPassword), mustChangePassword: false);
        await _refreshTokens.RevokeAllForUserAsync(userId); // sign out every other device
        _access.InvalidateUser(userId);
        await _audit.LogAsync(userId, "auth.password.changed", "User", userId.ToString(), ip);

        var fresh = await _users.GetByIdAsync(userId) ?? user;
        return await IssueTokensAsync(fresh, ip);
    }

    private async Task<AuthResult> IssueTokensAsync(AppUser user, string? ip)
    {
        var (access, accessExpiry) = _tokens.CreateAccessToken(user);
        var refresh = _tokens.GenerateRefreshToken();
        var refreshExpiry = DateTime.UtcNow.AddDays(_jwt.RefreshTokenDays);
        await _refreshTokens.CreateAsync(user.Id, _tokens.HashToken(refresh), refreshExpiry, ip);

        return new AuthResult(true, StatusCodes.Status200OK, null,
            new AuthResponse(access, accessExpiry, await BuildUserDtoAsync(user)), refresh, refreshExpiry);
    }

    /// <summary>The client uses the permission list only to show/hide UI; the API re-checks every call.</summary>
    public async Task<UserDto> BuildUserDtoAsync(AppUser u)
    {
        var permissions = await _access.GetRolePermissionsAsync(u.Role);
        return new UserDto(u.Id, u.FullName, u.Email, u.Role, permissions.OrderBy(p => p).ToList(), u.MustChangePassword);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
