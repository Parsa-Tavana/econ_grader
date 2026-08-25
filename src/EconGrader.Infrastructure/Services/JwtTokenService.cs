using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EconGrader.Application.Services;
using EconGrader.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace EconGrader.Infrastructure.Services;

/// <summary>Issues HS256-signed access tokens from Jwt:SigningKey config.
/// Claims: sub (UserId), role, and studentId for Role=Student users.</summary>
public sealed class JwtTokenService : ITokenService
{
    private readonly string _signingKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _ttlMinutes;

    public JwtTokenService(IConfiguration config)
    {
        _signingKey = config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Missing Jwt:SigningKey — generate one with: openssl rand -base64 48");
        _issuer = config["Jwt:Issuer"] ?? "econgrader";
        _audience = config["Jwt:Audience"] ?? "econgrader-api";
        _ttlMinutes = int.TryParse(config["Jwt:TtlMinutes"], out var m) ? m : 480;
    }

    public (string Token, int ExpiresInSeconds) Issue(User user)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            // GUID subject also as name claim so User.FindFirstValue(ClaimTypes.NameIdentifier) works.
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (user.Role == UserRole.Student && user.Student is { } student)
            claims.Add(new Claim("studentId", student.Id.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = now.AddMinutes(_ttlMinutes);

        var token = new JwtSecurityToken(_issuer, _audience, claims, notBefore: now, expires: expires, signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(token), (int)(expires - now).TotalSeconds);
    }

    /// <summary>Cryptographically random key for dev bootstrap / first-run generation.</summary>
    public static string GenerateSigningKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
}
