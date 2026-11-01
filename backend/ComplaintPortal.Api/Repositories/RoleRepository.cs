using ComplaintPortal.Api.Data;
using ComplaintPortal.Api.Models;
using Dapper;

namespace ComplaintPortal.Api.Repositories;

public interface IRoleRepository
{
    Task<IReadOnlyList<RoleRow>> ListAsync();
    Task<RoleRow?> GetByIdAsync(int id);
    Task<RoleRow?> GetByNameAsync(string name);
    Task<bool> NameExistsAsync(string name, int? exceptRoleId = null);
    Task<IReadOnlyList<string>> GetPermissionsAsync(string roleName);
    Task<IReadOnlyDictionary<int, List<string>>> GetAllPermissionsAsync();
    Task<int> CreateAsync(string name, string? description, IReadOnlyCollection<string> permissions);
    Task UpdateAsync(int id, string name, string? description, IReadOnlyCollection<string> permissions);
    Task<bool> DeleteAsync(int id);
}

public sealed class RoleRepository : IRoleRepository
{
    private const string SelectRole = """
        SELECT r.Id, r.Name, r.Description, r.IsSystem, r.CreatedAtUtc, r.UpdatedAtUtc,
               (SELECT COUNT(*) FROM dbo.Users u WHERE u.Role = r.Name) AS UserCount
          FROM dbo.Roles r
        """;

    private readonly IDbConnectionFactory _db;
    public RoleRepository(IDbConnectionFactory db) => _db = db;

    public async Task<IReadOnlyList<RoleRow>> ListAsync()
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return (await conn.QueryAsync<RoleRow>($"{SelectRole} ORDER BY r.IsSystem DESC, r.Name")).ToList();
    }

    public async Task<RoleRow?> GetByIdAsync(int id)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<RoleRow>($"{SelectRole} WHERE r.Id = @id", new { id });
    }

    public async Task<RoleRow?> GetByNameAsync(string name)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<RoleRow>($"{SelectRole} WHERE r.Name = @name", new { name });
    }

    public async Task<bool> NameExistsAsync(string name, int? exceptRoleId = null)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<bool>("""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM dbo.Roles WHERE Name = @name AND (@exceptRoleId IS NULL OR Id <> @exceptRoleId)
            ) THEN 1 ELSE 0 END
            """, new { name, exceptRoleId });
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(string roleName)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        return (await conn.QueryAsync<string>("""
            SELECT rp.Permission
              FROM dbo.RolePermissions rp
              JOIN dbo.Roles r ON r.Id = rp.RoleId
             WHERE r.Name = @roleName;
            """, new { roleName })).ToList();
    }

    public async Task<IReadOnlyDictionary<int, List<string>>> GetAllPermissionsAsync()
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        var rows = await conn.QueryAsync<(int RoleId, string Permission)>(
            "SELECT RoleId, Permission FROM dbo.RolePermissions");
        return rows.GroupBy(r => r.RoleId).ToDictionary(g => g.Key, g => g.Select(x => x.Permission).ToList());
    }

    public async Task<int> CreateAsync(string name, string? description, IReadOnlyCollection<string> permissions)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        using var tx = conn.BeginTransaction();

        var id = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO dbo.Roles (Name, Description, IsSystem)
            OUTPUT INSERTED.Id
            VALUES (@name, @description, 0);
            """, new { name, description }, tx);

        await InsertPermissionsAsync(conn, tx, id, permissions);
        tx.Commit();
        return id;
    }

    public async Task UpdateAsync(int id, string name, string? description, IReadOnlyCollection<string> permissions)
    {
        using var conn = await _db.CreateOpenConnectionAsync();
        using var tx = conn.BeginTransaction();

        // Renaming cascades to dbo.Users.Role through FK_Users_Role (ON UPDATE CASCADE).
        // Users of the renamed role get a new security stamp so their old tokens (which carry the old name) stop working.
        await conn.ExecuteAsync("""
            DECLARE @oldName NVARCHAR(50) = (SELECT Name FROM dbo.Roles WHERE Id = @id);
            UPDATE dbo.Roles SET Name = @name, Description = @description, UpdatedAtUtc = SYSUTCDATETIME() WHERE Id = @id;
            IF @oldName <> @name
                UPDATE dbo.Users SET SecurityStamp = NEWID() WHERE Role = @name;
            DELETE FROM dbo.RolePermissions WHERE RoleId = @id;
            """, new { id, name, description }, tx);

        await InsertPermissionsAsync(conn, tx, id, permissions);
        tx.Commit();
    }

    public async Task<bool> DeleteAsync(int id)
    {
        // Only custom roles with no users can be deleted (checked again here to avoid races).
        using var conn = await _db.CreateOpenConnectionAsync();
        var rows = await conn.ExecuteAsync("""
            DELETE r FROM dbo.Roles r
             WHERE r.Id = @id AND r.IsSystem = 0
               AND NOT EXISTS (SELECT 1 FROM dbo.Users u WHERE u.Role = r.Name);
            """, new { id });
        return rows >= 1;
    }

    private static async Task InsertPermissionsAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        int roleId, IReadOnlyCollection<string> permissions)
    {
        if (permissions.Count == 0) return;
        await conn.ExecuteAsync(
            "INSERT INTO dbo.RolePermissions (RoleId, Permission) VALUES (@roleId, @permission);",
            permissions.Select(p => new { roleId, permission = p }), tx);
    }
}
