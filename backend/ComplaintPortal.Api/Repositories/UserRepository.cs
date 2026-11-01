using ComplaintPortal.Api.Data;
using ComplaintPortal.Api.Models;
using ComplaintPortal.Api.Security;
using Dapper;

namespace ComplaintPortal.Api.Repositories;

public interface IUserRepository
{
    Task<AppUser?> GetByIdAsync(int id);
    Task<AppUser?> GetByEmailAsync(string email);
    Task<bool> EmailExistsAsync(string email, int? exceptUserId = null);
    Task<bool> AnyAdminAsync();
    Task<int> CountActiveAdminsAsync();
    Task<int> CreateAsync(AppUser user);
    Task<bool> UpdateDetailsAsync(int userId, string fullName, string email, string role);
    Task RecordFailedLoginAsync(int userId, int maxAttempts, int lockoutMinutes);
    Task RecordSuccessfulLoginAsync(int userId);
    Task UpdatePasswordAsync(int userId, string passwordHash, bool mustChangePassword);
    Task<bool> UnlockAsync(int userId);
    Task<(IReadOnlyList<AppUser> Items, int Total)> ListAsync(string? search, string? role, int page, int pageSize);
    Task<IReadOnlyList<AppUser>> GetAssignableAsync();
    Task<bool> UpdateRoleAsync(int userId, string role);
    Task<bool> SetActiveAsync(int userId, bool isActive);
}

/// <summary>
/// All SQL is parameterised through Dapper - user input is never concatenated into a query.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private const string Columns =
        "Id, FullName, Email, PasswordHash, Role, IsActive, SecurityStamp, FailedLoginCount, LockoutEndUtc, " +
        "LastLoginUtc, MustChangePassword, CreatedByUserId, CreatedAtUtc";

    private readonly IDbConnectionFactory _db;

    public UserRepository(IDbConnectionFactory db) => _db = db;

    public async Task<AppUser?> GetByIdAsync(int id)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<AppUser>(
            $"SELECT {Columns} FROM dbo.Users WHERE Id = @id", new { id });
    }

    public async Task<AppUser?> GetByEmailAsync(string email)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<AppUser>(
            $"SELECT {Columns} FROM dbo.Users WHERE Email = @email",
            new { email = new DbString { Value = email, Length = 256, IsAnsi = false } });
    }

    public async Task<bool> EmailExistsAsync(string email, int? exceptUserId = null)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<bool>("""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM dbo.Users WHERE Email = @email AND (@exceptUserId IS NULL OR Id <> @exceptUserId)
            ) THEN 1 ELSE 0 END
            """, new { email, exceptUserId });
    }

    public async Task<bool> AnyAdminAsync()
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Users WHERE Role = @admin) THEN 1 ELSE 0 END",
            new { admin = Roles.Admin });
    }

    public async Task<int> CountActiveAdminsAsync()
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Users WHERE Role = @admin AND IsActive = 1", new { admin = Roles.Admin });
    }

    public async Task<int> CreateAsync(AppUser user)
    {
        const string sql = """
            INSERT INTO dbo.Users (FullName, Email, PasswordHash, Role, IsActive, MustChangePassword, CreatedByUserId)
            OUTPUT INSERTED.Id
            VALUES (@FullName, @Email, @PasswordHash, @Role, 1, @MustChangePassword, @CreatedByUserId);
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(sql, user);
    }

    public async Task<bool> UpdateDetailsAsync(int userId, string fullName, string email, string role)
    {
        // The security stamp only rotates when the role actually changes (forces new permissions immediately).
        const string sql = """
            UPDATE dbo.Users
               SET FullName      = @fullName,
                   Email         = @email,
                   SecurityStamp = CASE WHEN Role <> @role THEN NEWID() ELSE SecurityStamp END,
                   Role          = @role
             WHERE Id = @userId;
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.ExecuteAsync(sql, new { userId, fullName, email, role }) == 1;
    }

    public async Task RecordFailedLoginAsync(int userId, int maxAttempts, int lockoutMinutes)
    {
        // Atomic: increments the counter and locks the account once the threshold is reached.
        const string sql = """
            UPDATE dbo.Users
               SET LockoutEndUtc    = CASE WHEN FailedLoginCount + 1 >= @maxAttempts
                                           THEN DATEADD(MINUTE, @lockoutMinutes, SYSUTCDATETIME())
                                           ELSE LockoutEndUtc END,
                   FailedLoginCount = CASE WHEN FailedLoginCount + 1 >= @maxAttempts
                                           THEN 0 ELSE FailedLoginCount + 1 END
             WHERE Id = @userId;
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(sql, new { userId, maxAttempts, lockoutMinutes });
    }

    public async Task RecordSuccessfulLoginAsync(int userId)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE dbo.Users SET FailedLoginCount = 0, LockoutEndUtc = NULL, LastLoginUtc = SYSUTCDATETIME() WHERE Id = @userId",
            new { userId });
    }

    public async Task UpdatePasswordAsync(int userId, string passwordHash, bool mustChangePassword)
    {
        // Rotating the security stamp invalidates every access token issued before the change.
        using var conn = await _db.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("""
            UPDATE dbo.Users
               SET PasswordHash = @passwordHash, MustChangePassword = @mustChangePassword,
                   SecurityStamp = NEWID(), FailedLoginCount = 0, LockoutEndUtc = NULL
             WHERE Id = @userId;
            """, new { userId, passwordHash, mustChangePassword });
    }

    public async Task<bool> UnlockAsync(int userId)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.ExecuteAsync(
            "UPDATE dbo.Users SET FailedLoginCount = 0, LockoutEndUtc = NULL WHERE Id = @userId",
            new { userId }) == 1;
    }

    public async Task<(IReadOnlyList<AppUser> Items, int Total)> ListAsync(string? search, string? role, int page, int pageSize)
    {
        var where = "WHERE 1 = 1";
        if (!string.IsNullOrWhiteSpace(search))
            where += " AND (FullName LIKE @search ESCAPE '\\' OR Email LIKE @search ESCAPE '\\')";
        if (!string.IsNullOrWhiteSpace(role))
            where += " AND Role = @role";

        var sql = $"""
            SELECT COUNT(*) FROM dbo.Users {where};
            SELECT {Columns} FROM dbo.Users {where}
             ORDER BY CreatedAtUtc DESC
             OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;

        using var conn = await _db.CreateOpenConnectionAsync();
        using var multi = await conn.QueryMultipleAsync(sql, new
        {
            search = SqlLike.Contains(search),
            role,
            offset = (page - 1) * pageSize,
            pageSize
        });
        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<AppUser>()).ToList();
        return (items, total);
    }

    /// <summary>Active users whose role can see all complaints (so they can work on an assigned one).</summary>
    public async Task<IReadOnlyList<AppUser>> GetAssignableAsync()
    {
        var sql = $"""
            SELECT {Columns} FROM dbo.Users u
             WHERE u.IsActive = 1
               AND (u.Role = @admin OR EXISTS (
                       SELECT 1 FROM dbo.RolePermissions rp
                         JOIN dbo.Roles r ON r.Id = rp.RoleId
                        WHERE r.Name = u.Role AND rp.Permission = @perm))
             ORDER BY u.FullName;
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        var rows = await conn.QueryAsync<AppUser>(sql, new { admin = Roles.Admin, perm = Permissions.ComplaintsViewAll });
        return rows.ToList();
    }

    public async Task<bool> UpdateRoleAsync(int userId, string role)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        var rows = await conn.ExecuteAsync(
            "UPDATE dbo.Users SET Role = @role, SecurityStamp = NEWID() WHERE Id = @userId",
            new { userId, role });
        return rows == 1;
    }

    public async Task<bool> SetActiveAsync(int userId, bool isActive)
    {
        const string sql = """
            UPDATE dbo.Users SET IsActive = @isActive, SecurityStamp = NEWID() WHERE Id = @userId;
            IF @isActive = 0
                UPDATE dbo.RefreshTokens SET RevokedAtUtc = SYSUTCDATETIME()
                 WHERE UserId = @userId AND RevokedAtUtc IS NULL;
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        var rows = await conn.ExecuteAsync(sql, new { userId, isActive });
        return rows >= 1;
    }
}

/// <summary>Escapes LIKE wildcards so user input is always matched literally.</summary>
public static class SqlLike
{
    public static string? Contains(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var escaped = input.Trim()
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_")
            .Replace("[", "\\[");
        return $"%{escaped}%";
    }
}
