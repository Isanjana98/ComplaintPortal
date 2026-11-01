using System.Security.Claims;

namespace ComplaintPortal.Api.Security;

/// <summary>Built-in (system) role names. Custom roles are stored in dbo.Roles.</summary>
public static class Roles
{
    public const string User = "User";
    public const string Staff = "Staff";
    public const string Admin = "Admin";
}

/// <summary>
/// Every permission the application understands. Authorization policies are generated from this list,
/// so each name is also a policy name usable in [Authorize(Policy = ...)].
/// </summary>
public static class Permissions
{
    public const string ComplaintsCreate        = "complaints.create";
    public const string ComplaintsViewAll       = "complaints.view_all";
    public const string ComplaintsUpdateStatus  = "complaints.update_status";
    public const string ComplaintsAssignSelf    = "complaints.assign_self";
    public const string ComplaintsAssignAny     = "complaints.assign_any";
    public const string ComplaintsInternalNotes = "complaints.internal_notes";
    public const string UsersView               = "users.view";
    public const string UsersManage             = "users.manage";
    public const string RolesManage             = "roles.manage";
    public const string AuditView               = "audit.view";

    public sealed record Definition(string Name, string Group, string Description);

    public static readonly IReadOnlyList<Definition> Definitions = new[]
    {
        new Definition(ComplaintsCreate,        "Complaints", "Submit new complaints"),
        new Definition(ComplaintsViewAll,       "Complaints", "View all complaints (not just their own)"),
        new Definition(ComplaintsUpdateStatus,  "Complaints", "Change complaint status"),
        new Definition(ComplaintsAssignSelf,    "Complaints", "Assign complaints to themselves"),
        new Definition(ComplaintsAssignAny,     "Complaints", "Assign complaints to anyone"),
        new Definition(ComplaintsInternalNotes, "Complaints", "Read and write internal staff notes"),
        new Definition(UsersView,               "Administration", "View the user list"),
        new Definition(UsersManage,             "Administration", "Add users, edit them, reset passwords, change roles"),
        new Definition(RolesManage,             "Administration", "Create, edit and delete roles"),
        new Definition(AuditView,               "Administration", "View the security audit log"),
    };

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(Definitions.Select(d => d.Name), StringComparer.Ordinal);

    public static bool IsValid(string permission) => All.Contains(permission);
}

/// <summary>Policies that accept any one of several permissions.</summary>
public static class Policies
{
    public const string CanReadRoles = "CanReadRoles";       // users.manage OR roles.manage
    public const string CanAssign    = "CanAssign";          // assign_self OR assign_any
}

public static class AppClaims
{
    public const string Sub = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string SecurityStamp = "sstamp";
    public const string Permission = "perm";                  // added per request from the DB, never put in the JWT
    public const string MustChangePassword = "must_change_pwd";
}

public static class ComplaintStatus
{
    public const string Open = "Open";
    public const string InProgress = "InProgress";
    public const string Resolved = "Resolved";
    public const string Closed = "Closed";
    public const string Rejected = "Rejected";

    public static readonly string[] All = { Open, InProgress, Resolved, Closed, Rejected };

    /// <summary>Allowed workflow transitions. Anything not listed is rejected.</summary>
    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [Open]       = new[] { InProgress, Rejected },
        [InProgress] = new[] { Resolved, Rejected, Open },
        [Resolved]   = new[] { Closed, InProgress },
        [Closed]     = Array.Empty<string>(),
        [Rejected]   = new[] { Open },
    };

    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static string[] NextFor(string from) =>
        Transitions.TryGetValue(from, out var allowed) ? allowed : Array.Empty<string>();
}

public static class ClaimsPrincipalExtensions
{
    public static int GetUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(AppClaims.Sub);
        return int.TryParse(value, out var id)
            ? id
            : throw new UnauthorizedAccessException("Missing user id claim.");
    }

    public static string GetRole(this ClaimsPrincipal user) =>
        user.FindFirstValue(AppClaims.Role) ?? Roles.User;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);

    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(AppClaims.Permission, permission);

    public static IReadOnlySet<string> GetPermissions(this ClaimsPrincipal user) =>
        user.FindAll(AppClaims.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
}
