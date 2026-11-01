using System.ComponentModel.DataAnnotations;

namespace ComplaintPortal.Api.Dtos;

/* ---------------- Auth ---------------- */

public sealed class RegisterRequest
{
    [Required, StringLength(100, MinimumLength = 2)]
    [RegularExpression(@"^[\p{L}\p{M}' .\-]+$", ErrorMessage = "Name contains invalid characters.")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    // Min 8 chars, upper, lower, digit and symbol.
    [Required, StringLength(128, MinimumLength = 8)]
    [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z\d]).{8,128}$",
        ErrorMessage = "Password must be at least 8 characters and include upper case, lower case, a number and a symbol.")]
    public string Password { get; set; } = string.Empty;
}

public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(128)]
    public string Password { get; set; } = string.Empty;
}

public sealed class ChangePasswordRequest
{
    [Required, StringLength(128)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 8)]
    [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z\d]).{8,128}$",
        ErrorMessage = "Password must be at least 8 characters and include upper case, lower case, a number and a symbol.")]
    public string NewPassword { get; set; } = string.Empty;
}

public sealed record UserDto(int Id, string FullName, string Email, string Role,
    IReadOnlyCollection<string> Permissions, bool MustChangePassword);

public sealed record AuthResponse(string AccessToken, DateTime ExpiresAtUtc, UserDto User);

/* ---------------- Complaints ---------------- */

public sealed class CreateComplaintRequest
{
    [Required, StringLength(200, MinimumLength = 5)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(4000, MinimumLength = 20)]
    public string Description { get; set; } = string.Empty;

    [Range(1, int.MaxValue, ErrorMessage = "Please choose a category.")]
    public int CategoryId { get; set; }

    [Required, RegularExpression("^(Low|Medium|High|Critical)$")]
    public string Priority { get; set; } = "Medium";

    public List<IFormFile>? Files { get; set; }
}

public sealed class ComplaintQuery
{
    [StringLength(20)]
    public string? Status { get; set; }

    [StringLength(10)]
    public string? Priority { get; set; }

    public int? CategoryId { get; set; }

    [StringLength(100)]
    public string? Search { get; set; }

    public bool AssignedToMe { get; set; }

    [RegularExpression("^(newest|oldest|priority|updated)$")]
    public string? Sort { get; set; }

    [Range(1, 10_000)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 10;
}

public sealed class AddCommentRequest
{
    [Required, StringLength(2000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;

    public bool IsInternal { get; set; }
}

public sealed class UpdateStatusRequest
{
    [Required, RegularExpression("^(Open|InProgress|Resolved|Closed|Rejected)$")]
    public string Status { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Note { get; set; }
}

public sealed class AssignRequest
{
    /// <summary>Null un-assigns the complaint.</summary>
    public int? AssignedToUserId { get; set; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public sealed record ComplaintStats(int Total, int Open, int InProgress, int Resolved, int Closed, int Rejected, int Critical);

/* ---------------- Admin: users ---------------- */

public static class Validation
{
    public const string PasswordPattern = @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z\d]).{8,128}$";
    public const string PasswordMessage =
        "Password must be at least 8 characters and include upper case, lower case, a number and a symbol.";
    public const string NamePattern = @"^[\p{L}\p{M}' .\-]+$";
    public const string RoleNamePattern = @"^[A-Za-z][A-Za-z0-9 _\-]{1,49}$";
}

public sealed class CreateUserRequest
{
    [Required, StringLength(100, MinimumLength = 2)]
    [RegularExpression(Validation.NamePattern, ErrorMessage = "Name contains invalid characters.")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(50)]
    public string Role { get; set; } = string.Empty;

    /// <summary>Optional. When empty a strong temporary password is generated and returned once.</summary>
    [StringLength(128)]
    [RegularExpression(Validation.PasswordPattern, ErrorMessage = Validation.PasswordMessage)]
    public string? TemporaryPassword { get; set; }
}

public sealed class UpdateUserRequest
{
    [Required, StringLength(100, MinimumLength = 2)]
    [RegularExpression(Validation.NamePattern, ErrorMessage = "Name contains invalid characters.")]
    public string FullName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(50)]
    public string Role { get; set; } = string.Empty;
}

public sealed class ResetPasswordRequest
{
    [StringLength(128)]
    [RegularExpression(Validation.PasswordPattern, ErrorMessage = Validation.PasswordMessage)]
    public string? TemporaryPassword { get; set; }
}

public sealed class UpdateUserStatusRequest
{
    public bool IsActive { get; set; }
}

public sealed record TemporaryPasswordResponse(int UserId, string Email, string TemporaryPassword);

public sealed record AdminUserDto(int Id, string FullName, string Email, string Role, bool IsActive,
    bool IsLockedOut, bool MustChangePassword, DateTime? LastLoginUtc, DateTime CreatedAtUtc);

/* ---------------- Admin: roles ---------------- */

public sealed class SaveRoleRequest
{
    [Required, StringLength(50, MinimumLength = 2)]
    [RegularExpression(Validation.RoleNamePattern,
        ErrorMessage = "Role name must start with a letter and contain only letters, numbers, spaces, '-' or '_'.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Description { get; set; }

    [Required]
    public List<string> Permissions { get; set; } = new();
}

public sealed record RoleDto(int Id, string Name, string? Description, bool IsSystem, int UserCount,
    IReadOnlyCollection<string> Permissions, bool IsEditable);

public sealed record PermissionDto(string Name, string Group, string Description);
