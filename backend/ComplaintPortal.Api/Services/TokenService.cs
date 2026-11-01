using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ComplaintPortal.Api.Models;
using ComplaintPortal.Api.Options;
using ComplaintPortal.Api.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ComplaintPortal.Api.Services;

public interface ITokenService
{
    (string Token, DateTime ExpiresAtUtc) CreateAccessToken(AppUser user);
    string GenerateRefreshToken();
    string HashToken(string token);
}

public sealed class TokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(AppUser user)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = _credentials,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(AppClaims.Sub, user.Id.ToString()),
                new Claim(AppClaims.Email, user.Email),
                new Claim(AppClaims.Name, user.FullName),
                new Claim(AppClaims.Role, user.Role),
                new Claim(AppClaims.SecurityStamp, user.SecurityStamp.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            }),
        };

        return (_handler.CreateToken(descriptor), expires);
    }

    /// <summary>256-bit cryptographically random, URL-safe token.</summary>
    public string GenerateRefreshToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Only the SHA-256 hash is stored, so a DB leak does not leak usable tokens.</summary>
    public string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
