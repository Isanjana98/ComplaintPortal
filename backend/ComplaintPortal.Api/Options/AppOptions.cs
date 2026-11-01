using System.Text;

namespace ComplaintPortal.Api.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>Fail fast at startup instead of running with a weak signing key.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("Jwt:Issuer and Jwt:Audience must be configured.");
        if (Encoding.UTF8.GetByteCount(Key) < 32)
            throw new InvalidOperationException(
                "Jwt:Key must be at least 32 bytes. Set it with 'dotnet user-secrets set Jwt:Key <value>' or an environment variable.");
        if (AccessTokenMinutes is < 1 or > 60)
            throw new InvalidOperationException("Jwt:AccessTokenMinutes must be between 1 and 60.");
    }
}

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    public string RootPath { get; set; } = "App_Data/uploads";
    public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxFilesPerComplaint { get; set; } = 3;
}

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public string AdminEmail { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public string AdminName { get; set; } = "System Administrator";
}
