using ComplaintPortal.Api.Data;
using ComplaintPortal.Api.Models;
using Dapper;

namespace ComplaintPortal.Api.Repositories;

public interface IRefreshTokenRepository
{
    Task CreateAsync(int userId, string tokenHash, DateTime expiresAtUtc, string? ip);
    Task<RefreshToken?> GetByHashAsync(string tokenHash);
    Task<bool> RotateAsync(string oldHash, string newHash, int userId, DateTime newExpiresAtUtc, string? ip);
    Task RevokeAsync(string tokenHash);
    Task RevokeAllForUserAsync(int userId);
}

public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly IDbConnectionFactory _db;
    public RefreshTokenRepository(IDbConnectionFactory db) => _db = db;

    public async Task CreateAsync(int userId, string tokenHash, DateTime expiresAtUtc, string? ip)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO dbo.RefreshTokens (UserId, TokenHash, ExpiresAtUtc, CreatedByIp)
            VALUES (@userId, @tokenHash, @expiresAtUtc, @ip);
            """, new { userId, tokenHash, expiresAtUtc, ip });
    }

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<RefreshToken>(
            "SELECT * FROM dbo.RefreshTokens WHERE TokenHash = @tokenHash",
            new { tokenHash = new DbString { Value = tokenHash, IsAnsi = true, IsFixedLength = true, Length = 64 } });
    }

    public async Task<bool> RotateAsync(string oldHash, string newHash, int userId, DateTime newExpiresAtUtc, string? ip)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        using var tx = conn.BeginTransaction();

        // Only succeeds if the old token is still un-revoked (prevents two parallel refreshes both winning).
        var revoked = await conn.ExecuteAsync("""
            UPDATE dbo.RefreshTokens
               SET RevokedAtUtc = SYSUTCDATETIME(), ReplacedByTokenHash = @newHash
             WHERE TokenHash = @oldHash AND RevokedAtUtc IS NULL;
            """, new { oldHash, newHash }, tx);

        if (revoked != 1)
        {
            tx.Rollback();
            return false;
        }

        await conn.ExecuteAsync("""
            INSERT INTO dbo.RefreshTokens (UserId, TokenHash, ExpiresAtUtc, CreatedByIp)
            VALUES (@userId, @newHash, @newExpiresAtUtc, @ip);
            """, new { userId, newHash, newExpiresAtUtc, ip }, tx);

        tx.Commit();
        return true;
    }

    public async Task RevokeAsync(string tokenHash)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE dbo.RefreshTokens SET RevokedAtUtc = SYSUTCDATETIME() WHERE TokenHash = @tokenHash AND RevokedAtUtc IS NULL",
            new { tokenHash });
    }

    public async Task RevokeAllForUserAsync(int userId)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE dbo.RefreshTokens SET RevokedAtUtc = SYSUTCDATETIME() WHERE UserId = @userId AND RevokedAtUtc IS NULL",
            new { userId });
    }
}

public interface IAuditRepository
{
    Task LogAsync(int? userId, string action, string? entityType = null, string? entityId = null,
        string? ip = null, string? details = null);
    Task<(IReadOnlyList<AuditLogRow> Items, int Total)> ListAsync(string? action, int page, int pageSize);
}

public sealed class AuditRepository : IAuditRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<AuditRepository> _logger;

    public AuditRepository(IDbConnectionFactory db, ILogger<AuditRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(int? userId, string action, string? entityType = null, string? entityId = null,
        string? ip = null, string? details = null)
    {
        try
        {
            using var conn = await _db.CreateOpenConnectionAsync();
            await conn.ExecuteAsync("""
                INSERT INTO dbo.AuditLogs (UserId, Action, EntityType, EntityId, IpAddress, Details)
                VALUES (@userId, @action, @entityType, @entityId, @ip, @details);
                """, new
            {
                userId, action, entityType, entityId, ip,
                details = details is { Length: > 1000 } ? details[..1000] : details
            });
        }
        catch (Exception ex)
        {
            // Auditing must never break the user's request, but the failure must be visible to operators.
            _logger.LogError(ex, "Failed to write audit log entry {Action}", action);
        }
    }

    public async Task<(IReadOnlyList<AuditLogRow> Items, int Total)> ListAsync(string? action, int page, int pageSize)
    {
        var where = string.IsNullOrWhiteSpace(action) ? string.Empty : "WHERE l.Action = @action";
        var sql = $"""
            SELECT COUNT(*) FROM dbo.AuditLogs l {where};
            SELECT l.Id, l.UserId, u.Email AS UserEmail, l.Action, l.EntityType, l.EntityId, l.IpAddress, l.Details, l.CreatedAtUtc
              FROM dbo.AuditLogs l
              LEFT JOIN dbo.Users u ON u.Id = l.UserId
              {where}
             ORDER BY l.Id DESC
             OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;
        using var conn = await _db.CreateOpenConnectionAsync();
        using var multi = await conn.QueryMultipleAsync(sql, new { action, offset = (page - 1) * pageSize, pageSize });
        var total = await multi.ReadSingleAsync<int>();
        var items = (await multi.ReadAsync<AuditLogRow>()).ToList();
        return (items, total);
    }
}
