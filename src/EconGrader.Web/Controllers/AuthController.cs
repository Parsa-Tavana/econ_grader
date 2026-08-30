using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EconGrader.Application.DTOs;
using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;
using EconGrader.Domain.Entities;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly IAppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService auth, IAppDbContext db, IAuditLogger audit,
        IConfiguration config, ILogger<AuthController> logger)
    {
        _auth = auth;
        _db = db;
        _audit = audit;
        _config = config;
        _logger = logger;
    }

    /// <summary>Email + password → bearer JWT. Anonymous by design.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        try
        {
            var response = await _auth.LoginAsync(request, ct);
            await _audit.WriteAsync("AuthLogin", "User", response.User.Id, response.User.Id,
                new { request.Email }, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            return Ok(response);
        }
        catch (BusinessRuleException ex) when (ex.ErrorCode == "INVALID_CREDENTIALS")
        {
            // Logged, never audited with the attempted email (avoid credential stuffing breadcrumbs).
            _logger.LogWarning("Failed login attempt for {Email} from {Ip}",
                request.Email, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    /// <summary>
    /// One-shot bootstrap for the FIRST admin. Enabled only while no active
    /// Admin exists and a bootstrap key is configured — after the first call
    /// it permanently returns 403.
    /// </summary>
    [HttpPost("bootstrap-admin")]
    [AllowAnonymous]
    public async Task<IActionResult> BootstrapAdmin([FromBody] BootstrapAdminRequest body, CancellationToken ct)
    {
        var expectedKey = _config["Jwt:BootstrapAdminKey"];
        if (string.IsNullOrEmpty(expectedKey))
            return StatusCode(403, new { code = "BOOTSTRAP_DISABLED", message = "Set Jwt:BootstrapAdminKey in configuration to enable first-run bootstrap." });

        var anyActiveAdmin = await _db.Users.AnyAsync(u => u.Role == UserRole.Admin && u.IsActive, ct);
        if (anyActiveAdmin)
            return StatusCode(403, new { code = "BOOTSTRAP_CLOSED", message = "An active admin already exists — use an admin account or POST /api/users." });

        if (!string.Equals(body.BootstrapKey, expectedKey, StringComparison.Ordinal))
            return Unauthorized(new { code = "INVALID_CREDENTIALS", message = "Invalid bootstrap key" });

        var email = body.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Conflict(new { code = "EMAIL_TAKEN", message = $"A user with email '{email}' already exists" });

        var admin = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? "Administrator" : body.DisplayName.Trim(),
            Role = UserRole.Admin,
            PasswordHash = PasswordHasher.Hash(body.Password),
            IsActive = true,
        };
        _db.Users.Add(admin);
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync("AdminBootstrapped", "User", admin.Id, admin.Id,
            new { admin.Email }, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        _logger.LogInformation("First admin bootstrapped: {UserId} {Email}", admin.Id, admin.Email);

        return Ok(await LoginAfterCreateAsync(email, body.Password, ct));
    }

    /// <summary>Issues a fresh token right after account creation so the caller
    /// lands directly in an authenticated session.</summary>
    private async Task<LoginResponse> LoginAfterCreateAsync(string email, string password, CancellationToken ct) =>
        await _auth.LoginAsync(new LoginRequest(email, password), ct);

    // ── Admin-only user management ──────────────────────────────────────────

    [HttpGet("users")]
    [Authorize(Roles = nameof(UserRole.Admin))]
    public async Task<IActionResult> ListUsers(CancellationToken ct) =>
        Ok(await _db.Users
            .OrderBy(u => u.CreatedAt)
            .Select(u => new ManagedUserDto(u.Id, u.Email, u.DisplayName, u.Role.ToString(), u.IsActive, u.CreatedAt))
            .ToListAsync(ct));

    /// <summary>Create any-role account (Admin/Teacher/Corrector/Student).</summary>
    [HttpPost("users")]
    [Authorize(Roles = nameof(UserRole.Admin))]
    public async Task<ActionResult<ManagedUserDto>> CreateUser(
        [FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Conflict(new { code = "EMAIL_TAKEN", message = $"A user with email '{email}' already exists" });
        if (request.Password.Length < 8)
            return BadRequest(new { code = "WEAK_PASSWORD", message = "Password must be at least 8 characters" });
        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
            return BadRequest(new { code = "INVALID_ROLE", message = $"Role must be one of: {string.Join(", ", Enum.GetNames<UserRole>())}" });

        var user = new User
        {
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            Role = role,
            PasswordHash = PasswordHasher.Hash(request.Password),
            IsActive = true,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync("UserCreated", "User", user.Id, CurrentUserId(),
            new { user.Email, Role = user.Role.ToString() }, null, ct);
        return CreatedAtAction(nameof(ListUsers), new { },
            new ManagedUserDto(user.Id, user.Email, user.DisplayName, user.Role.ToString(), user.IsActive, user.CreatedAt));
    }

    /// <summary>Deactivate/reactivate accounts or change display name / role.</summary>
    [HttpPut("users/{id:guid}")]
    [Authorize(Roles = nameof(UserRole.Admin))]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        if (request.IsActive.HasValue)
        {
            // Never let the last active admin lock everyone out.
            if (!request.IsActive.Value && user.Role == UserRole.Admin && user.IsActive &&
                !await _db.Users.AnyAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != user.Id, ct))
                return Conflict(new { code = "LAST_ADMIN", message = "Cannot deactivate the last active admin" });
            user.IsActive = request.IsActive.Value;
        }
        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
                return BadRequest(new { code = "INVALID_ROLE", message = $"Role must be one of: {string.Join(", ", Enum.GetNames<UserRole>())}" });
            if (user.Role == UserRole.Admin && role != UserRole.Admin && user.IsActive &&
                !await _db.Users.AnyAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != user.Id, ct))
                return Conflict(new { code = "LAST_ADMIN", message = "Cannot demote the last active admin" });
            user.Role = role;
        }
        if (!string.IsNullOrWhiteSpace(request.DisplayName)) user.DisplayName = request.DisplayName.Trim();

        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("UserUpdated", "User", user.Id, CurrentUserId(),
            new { user.Email, Role = user.Role.ToString(), user.IsActive }, null, ct);
        return Ok(new ManagedUserDto(user.Id, user.Email, user.DisplayName, user.Role.ToString(), user.IsActive, user.CreatedAt));
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public record BootstrapAdminRequest(string BootstrapKey, string Email, string Password, string? DisplayName);
