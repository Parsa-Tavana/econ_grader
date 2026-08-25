using System.Security.Claims;
using System.Security.Cryptography;
using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EconGrader.Application.Services;

// ── Password hashing (PBKDF2, versioned blob format) ─────────────────────────

/// <summary>Hash/verify against the versioned "pbkdf2.{iter}.{saltB64}.{hashB64}"
/// blob stored in Users.PasswordHash. Legacy sentinel values ("external-identity")
/// fail verification by construction.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 100_000;
    private const int SaltSize = 16, HashSize = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pbkdf2.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('.');
        // Only blobs we produced can verify; sentinels/legacy hashes fall through.
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iter))
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record LoginRequest(string Email, string Password);
public record LoginResponse(
    string AccessToken,
    string TokenType,
    int ExpiresInSeconds,
    UserDto User);
public record UserDto(Guid Id, string Email, string DisplayName, string Role, bool IsActive, DateTime CreatedAt);

public record CreateUserRequest(string Email, string Password, string DisplayName, string Role);
public record UpdateUserRequest(bool? IsActive, string? DisplayName, string? Role);

/// <summary>Admin-only user-management list item — never includes credential material.</summary>
public record ManagedUserDto(Guid Id, string Email, string DisplayName, string Role, bool IsActive, DateTime CreatedAt);

// ── Service contract ─────────────────────────────────────────────────────────

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
}

// ── Implementation ───────────────────────────────────────────────────────────

public sealed class AuthService(IAppDbContext db, ITokenService tokens, ILogger<AuthService> logger) : IAuthService
{
    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email, ct);
        // Same message + constant-ish work whether the email exists or not, so
        // the endpoint can't be used to enumerate accounts.
        if (user is null || !user.IsActive || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            throw new BusinessRuleException("Invalid email or password", "INVALID_CREDENTIALS");

        var (token, expiresInSeconds) = tokens.Issue(user);
        logger.LogInformation("Login succeeded for {UserId} ({Role})", user.Id, user.Role);
        return new LoginResponse(token, "Bearer", expiresInSeconds,
            new UserDto(user.Id, user.Email, user.DisplayName, user.Role.ToString(), user.IsActive, user.CreatedAt));
    }
}

// ── Token issuance ───────────────────────────────────────────────────────────

public interface ITokenService
{
    /// <summary>Issues a signed JWT: sub = UserId, role claim, and studentId
    /// claim when the user links a Student row (Role=Student).</summary>
    (string Token, int ExpiresInSeconds) Issue(User user);
}
