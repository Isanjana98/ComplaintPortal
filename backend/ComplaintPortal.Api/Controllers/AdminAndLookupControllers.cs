using ComplaintPortal.Api.Dtos;
using ComplaintPortal.Api.Infrastructure;
using ComplaintPortal.Api.Models;
using ComplaintPortal.Api.Repositories;
using ComplaintPortal.Api.Security;
using ComplaintPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace ComplaintPortal.Api.Controllers;

[ApiController]
[Route("api/lookups")]
[Authorize]
public sealed class LookupsController : ControllerBase
{
    private readonly IComplaintRepository _complaints;
    private readonly IUserRepository _users;

    public LookupsController(IComplaintRepository complaints, IUserRepository users)
    {
        _complaints = complaints;
        _users = users;
    }

    [HttpGet("categories")]
    public async Task<IReadOnlyList<Category>> Categories() => await _complaints.GetCategoriesAsync();

    [HttpGet("assignees")]
    [Authorize(Policy = Policies.CanAssign)]
    public async Task<IEnumerable<object>> Assignees() =>
        (await _users.GetAssignableAsync()).Select(u => new { u.Id, u.FullName, u.Role });
}

/* =====================================================================
   User management
   Anti-escalation rules (enforced here, not just in the UI):
     * nobody can change their own role, deactivate themselves or reset their own password here
     * only Admins can create, edit or assign the Admin role / Admin accounts
     * a non-Admin manager can only hand out roles whose permissions they already have
     * the last active Admin can never be demoted or deactivated
   ===================================================================== */
[ApiController]
[Route("api/admin/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IAccessCache _access;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly IAuditRepository _audit;

    public UsersController(IUserRepository users, IRoleRepository roles, IRefreshTokenRepository refreshTokens,
        IAccessCache access, IPasswordHasher<AppUser> hasher, IAuditRepository audit)
    {
        _users = users;
        _roles = roles;
        _refreshTokens = refreshTokens;
        _access = access;
        _hasher = hasher;
        _audit = audit;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.UsersView)]
    public async Task<PagedResult<AdminUserDto>> List(
        [FromQuery] string? search, [FromQuery] string? role, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        page = Math.Clamp(page, 1, 10_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (search is { Length: > 100 }) search = search[..100];
        if (role is { Length: > 50 }) role = role[..50];

        var (items, total) = await _users.ListAsync(search, role, page, pageSize);
        var now = DateTime.UtcNow;
        var dtos = items.Select(u => new AdminUserDto(u.Id, u.FullName, u.Email, u.Role, u.IsActive,
            u.LockoutEndUtc > now, u.MustChangePassword, u.LastLoginUtc, u.CreatedAtUtc)).ToList();
        return new PagedResult<AdminUserDto>(dtos, total, page, pageSize);
    }

    [HttpPost]
    [Authorize(Policy = Permissions.UsersManage)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request)
    {
        var (role, roleError) = await ResolveAssignableRoleAsync(request.Role);
        if (roleError is not null) return roleError;

        var email = request.Email.Trim().ToLowerInvariant();
        if (await _users.EmailExistsAsync(email))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "A user with this email already exists.");

        var password = string.IsNullOrEmpty(request.TemporaryPassword) ? PasswordGenerator.Generate() : request.TemporaryPassword;
        var user = new AppUser
        {
            FullName = request.FullName.Trim(),
            Email = email,
            Role = role!,
            MustChangePassword = true, // the user must choose their own password at first sign-in
            CreatedByUserId = User.GetUserId(),
        };
        user.PasswordHash = _hasher.HashPassword(user, password);
        user.Id = await _users.CreateAsync(user);

        await _audit.LogAsync(User.GetUserId(), "admin.user_created", "User", user.Id.ToString(),
            HttpContext.GetClientIp(), $"{email} as {role}");

        // The temporary password is returned exactly once and never stored in plain text.
        return StatusCode(StatusCodes.Status201Created, new TemporaryPasswordResponse(user.Id, email, password));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.UsersManage)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserRequest request)
    {
        var target = await _users.GetByIdAsync(id);
        if (target is null) return NotFound();
        if (CannotManage(target) is { } denied) return denied;

        var email = request.Email.Trim().ToLowerInvariant();
        if (await _users.EmailExistsAsync(email, exceptUserId: id))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Another user already uses this email.");

        var role = target.Role;
        if (!string.Equals(request.Role, target.Role, StringComparison.OrdinalIgnoreCase))
        {
            if (id == User.GetUserId())
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "You cannot change your own role.");
            if (await LastAdminGuardAsync(target) is { } lastAdmin) return lastAdmin;

            var (resolved, roleError) = await ResolveAssignableRoleAsync(request.Role);
            if (roleError is not null) return roleError;
            role = resolved!;
        }

        await _users.UpdateDetailsAsync(id, request.FullName.Trim(), email, role);
        _access.InvalidateUser(id);

        var changes = new List<string>();
        if (target.FullName != request.FullName.Trim()) changes.Add("name");
        if (target.Email != email) changes.Add($"email {target.Email} -> {email}");
        if (target.Role != role) changes.Add($"role {target.Role} -> {role}");
        await _audit.LogAsync(User.GetUserId(), "admin.user_updated", "User", id.ToString(), HttpContext.GetClientIp(),
            changes.Count == 0 ? "No changes" : string.Join("; ", changes));
        return NoContent();
    }

    [HttpPatch("{id:int}/role")]
    [Authorize(Policy = Permissions.UsersManage)]
    public async Task<IActionResult> ChangeRole(int id, [FromBody] UpdateUserRoleBody request)
    {
        var target = await _users.GetByIdAsync(id);
        if (target is null) return NotFound();
        if (id == User.GetUserId())
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "You cannot change your own role.");
        if (CannotManage(target) is { } denied) return denied;
        if (await LastAdminGuardAsync(target) is { } lastAdmin) return lastAdmin;

        var (role, roleError) = await ResolveAssignableRoleAsync(request.Role);
        if (roleError is not null) return roleError;

        await _users.UpdateRoleAsync(id, role!);
        _access.InvalidateUser(id);
        await _audit.LogAsync(User.GetUserId(), "admin.role_changed", "User", id.ToString(), HttpContext.GetClientIp(),
            $"{target.Role} -> {role}");
        return NoContent();
    }

    [HttpPatch("{id:int}/status")]
    [Authorize(Policy = Permissions.UsersManage)]
    public async Task<IActionResult> ChangeStatus(int id, [FromBody] UpdateUserStatusRequest request)
    {
        var target = await _users.GetByIdAsync(id);
        if (target is null) return NotFound();
        if (id == User.GetUserId())
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "You cannot deactivate your own account.");
        if (CannotManage(target) is { } denied) return denied;
        if (!request.IsActive && await LastAdminGuardAsync(target) is { } lastAdmin) return lastAdmin;

        await _users.SetActiveAsync(id, request.IsActive);
        _access.InvalidateUser(id);
        await _audit.LogAsync(User.GetUserId(), request.IsActive ? "admin.user_activated" : "admin.user_deactivated",
            "User", id.ToString(), HttpContext.GetClientIp());
        return NoContent();
    }

    [HttpPost("{id:int}/reset-password")]
    [Authorize(Policy = Permissions.UsersManage)]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest request)
    {
        var target = await _users.GetByIdAsync(id);
        if (target is null) return NotFound();
        if (id == User.GetUserId())
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Use 'My account' to change your own password.");
        if (CannotManage(target) is { } denied) return denied;

        var password = string.IsNullOrEmpty(request.TemporaryPassword) ? PasswordGenerator.Generate() : request.TemporaryPassword;
        await _users.UpdatePasswordAsync(id, _hasher.HashPassword(target, password), mustChangePassword: true);
        await _refreshTokens.RevokeAllForUserAsync(id); // sign the user out everywhere
        _access.InvalidateUser(id);

        await _audit.LogAsync(User.GetUserId(), "admin.password_reset", "User", id.ToString(), HttpContext.GetClientIp());
        return Ok(new TemporaryPasswordResponse(id, target.Email, password));
    }

    [HttpPost("{id:int}/unlock")]
    [Authorize(Policy = Permissions.UsersManage)]
    public async Task<IActionResult> Unlock(int id)
    {
        var target = await _users.GetByIdAsync(id);
        if (target is null) return NotFound();
        if (CannotManage(target) is { } denied) return denied;

        await _users.UnlockAsync(id);
        await _audit.LogAsync(User.GetUserId(), "admin.user_unlocked", "User", id.ToString(), HttpContext.GetClientIp());
        return NoContent();
    }

    /* ---------------- helpers ---------------- */

    private IActionResult? CannotManage(AppUser target) =>
        target.Role == Roles.Admin && !User.IsAdmin()
            ? Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only administrators can manage administrator accounts.")
            : null;

    private async Task<IActionResult?> LastAdminGuardAsync(AppUser target)
    {
        if (target.Role == Roles.Admin && target.IsActive && await _users.CountActiveAdminsAsync() <= 1)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "This is the last active administrator and cannot be demoted or deactivated.");
        return null;
    }

    /// <summary>Checks the role exists and that the current user is allowed to grant it.</summary>
    private async Task<(string? Role, IActionResult? Error)> ResolveAssignableRoleAsync(string requested)
    {
        var role = await _roles.GetByNameAsync(requested.Trim());
        if (role is null)
            return (null, Problem(statusCode: StatusCodes.Status400BadRequest, title: "The selected role does not exist."));

        if (User.IsAdmin()) return (role.Name, null);

        if (role.Name == Roles.Admin)
            return (null, Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only administrators can grant the Admin role."));

        var mine = User.GetPermissions();
        var granted = await _access.GetRolePermissionsAsync(role.Name);
        if (!granted.All(mine.Contains))
            return (null, Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "You cannot grant a role that has permissions you don't have yourself."));

        return (role.Name, null);
    }
}

public sealed class UpdateUserRoleBody
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(50)]
    public string Role { get; set; } = string.Empty;
}

/* =====================================================================
   Role management
   ===================================================================== */
[ApiController]
[Route("api/admin/roles")]
public sealed class RolesController : ControllerBase
{
    private readonly IRoleRepository _roles;
    private readonly IAccessCache _access;
    private readonly IAuditRepository _audit;

    public RolesController(IRoleRepository roles, IAccessCache access, IAuditRepository audit)
    {
        _roles = roles;
        _access = access;
        _audit = audit;
    }

    [HttpGet]
    [Authorize(Policy = Policies.CanReadRoles)]
    public async Task<IEnumerable<RoleDto>> List()
    {
        var roles = await _roles.ListAsync();
        var map = await _roles.GetAllPermissionsAsync();
        var mine = User.GetPermissions();

        return roles.Select(r =>
        {
            IReadOnlyCollection<string> perms = r.Name == Roles.Admin
                ? Permissions.All.OrderBy(p => p).ToList()
                : (map.TryGetValue(r.Id, out var list) ? list.Where(Permissions.IsValid).OrderBy(p => p).ToList() : new List<string>());
            return new RoleDto(r.Id, r.Name, r.Description, r.IsSystem, r.UserCount, perms, CanEdit(r, perms, mine));
        });
    }

    [HttpGet("permissions")]
    [Authorize(Policy = Policies.CanReadRoles)]
    public IEnumerable<PermissionDto> AllPermissions() =>
        Permissions.Definitions.Select(d => new PermissionDto(d.Name, d.Group, d.Description));

    [HttpPost]
    [Authorize(Policy = Permissions.RolesManage)]
    public async Task<IActionResult> Create([FromBody] SaveRoleRequest request)
    {
        var name = request.Name.Trim();
        var (perms, error) = ValidatePermissions(request.Permissions);
        if (error is not null) return error;

        if (await _roles.NameExistsAsync(name))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "A role with this name already exists.");

        var id = await _roles.CreateAsync(name, Clean(request.Description), perms!);
        _access.InvalidateAllRoles();

        await _audit.LogAsync(User.GetUserId(), "admin.role_created", "Role", id.ToString(), HttpContext.GetClientIp(),
            $"{name}: {string.Join(", ", perms!)}");
        return StatusCode(StatusCodes.Status201Created, new { id });
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.RolesManage)]
    public async Task<IActionResult> Update(int id, [FromBody] SaveRoleRequest request)
    {
        var role = await _roles.GetByIdAsync(id);
        if (role is null) return NotFound();

        var current = await _access.GetRolePermissionsAsync(role.Name);
        if (!CanEdit(role, current, User.GetPermissions()))
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "You are not allowed to edit this role.");

        var name = request.Name.Trim();
        if (role.IsSystem && name != role.Name)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Built-in roles cannot be renamed.");
        if (await _roles.NameExistsAsync(name, exceptRoleId: id))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "A role with this name already exists.");

        var (perms, error) = ValidatePermissions(request.Permissions);
        if (error is not null) return error;

        await _roles.UpdateAsync(id, name, Clean(request.Description), perms!);
        _access.InvalidateAllRoles();

        var added = perms!.Except(current).ToList();
        var removed = current.Except(perms!).ToList();
        await _audit.LogAsync(User.GetUserId(), "admin.role_updated", "Role", id.ToString(), HttpContext.GetClientIp(),
            $"{role.Name}{(name != role.Name ? $" renamed to {name}" : "")}; +[{string.Join(", ", added)}] -[{string.Join(", ", removed)}]");
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.RolesManage)]
    public async Task<IActionResult> Delete(int id)
    {
        var role = await _roles.GetByIdAsync(id);
        if (role is null) return NotFound();
        if (role.IsSystem)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Built-in roles cannot be deleted.");
        if (role.UserCount > 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Move the {role.UserCount} user(s) in this role to another role before deleting it.");

        var current = await _access.GetRolePermissionsAsync(role.Name);
        if (!CanEdit(role, current, User.GetPermissions()))
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "You are not allowed to delete this role.");

        if (!await _roles.DeleteAsync(id))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "The role could not be deleted. Refresh and try again.");

        _access.InvalidateAllRoles();
        await _audit.LogAsync(User.GetUserId(), "admin.role_deleted", "Role", id.ToString(), HttpContext.GetClientIp(), role.Name);
        return NoContent();
    }

    /* ---------------- helpers ---------------- */

    /// <summary>
    /// Admin role is never editable (it always has everything). Admins can edit any other role.
    /// Others can't edit their own role and can only touch roles whose permissions they fully hold.
    /// </summary>
    private bool CanEdit(RoleRow role, IEnumerable<string> rolePermissions, IReadOnlySet<string> mine)
    {
        if (role.Name == Roles.Admin) return false;
        if (User.IsAdmin()) return true;
        if (!User.HasPermission(Permissions.RolesManage)) return false;
        if (role.Name == User.GetRole()) return false;
        return rolePermissions.All(mine.Contains);
    }

    private (List<string>? Permissions, IActionResult? Error) ValidatePermissions(IEnumerable<string> requested)
    {
        var perms = requested.Select(p => p.Trim()).Distinct(StringComparer.Ordinal).ToList();

        var unknown = perms.Where(p => !Permissions.IsValid(p)).ToList();
        if (unknown.Count > 0)
            return (null, Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Unknown permission(s): {string.Join(", ", unknown)}"));

        if (!User.IsAdmin())
        {
            var mine = User.GetPermissions();
            var notHeld = perms.Where(p => !mine.Contains(p)).ToList();
            if (notHeld.Count > 0)
                return (null, Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: $"You cannot grant permissions you don't have: {string.Join(", ", notHeld)}"));
        }

        return (perms, null);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

[ApiController]
[Route("api/admin/audit-logs")]
[Authorize(Policy = Permissions.AuditView)]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditRepository _audit;
    public AuditController(IAuditRepository audit) => _audit = audit;

    [HttpGet]
    public async Task<PagedResult<AuditLogRow>> List(
        [FromQuery] string? action, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        page = Math.Clamp(page, 1, 10_000);
        pageSize = Math.Clamp(pageSize, 1, 200);
        if (action is { Length: > 100 }) action = action[..100];

        var (items, total) = await _audit.ListAsync(action, page, pageSize);
        return new PagedResult<AuditLogRow>(items, total, page, pageSize);
    }
}
