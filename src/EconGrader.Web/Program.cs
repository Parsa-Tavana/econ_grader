using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using EconGrader.Application.Data;
using EconGrader.Application.Evaluation;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;
using EconGrader.Infrastructure.Services;
using EconGrader.Infrastructure.Storage;
using EconGrader.Web.Middleware;
using EconGrader.Web.Services;

// ── Serilog (from appsettings Serilog: section) ──────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json")
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
        .Build())
    .CreateLogger();

try
{
    Log.Information("Starting EconGrader.Web");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // ── Config ────────────────────────────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
    var gradingServiceBaseUrl = builder.Configuration["GradingService:BaseUrl"] ?? "http://localhost:5001";
    var fileStorageRoot = builder.Configuration["FileStorage:RootPath"] ?? Path.Combine(AppContext.BaseDirectory, "storage", "images");

    // CORS: comma-separated allowed origins (e.g. "http://localhost:5173,https://grader.example.com").
    // DEFAULT-DENY: when unset we deliberately do NOT fall back to AllowAnyOrigin —
    // same-origin deployments (SPA served behind the same reverse proxy) need no
    // CORS at all. Explicit "*" still enables it (dev escape hatch), with a warning.
    var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (corsOrigins.Length == 0)
    {
        Log.Warning(
            "Cors:AllowedOrigins is not configured — cross-origin browser calls will be rejected " +
            "(correct for same-origin deployments where the proxy serves both SPA and /api). " +
            "Set it to comma-separated origins if the SPA is hosted elsewhere.");
    }
    else if (corsOrigins.Length == 1 && corsOrigins[0] == "*")
    {
        Log.Warning("Cors:AllowedOrigins=\"*\" allows ANY origin — acceptable for local development only.");
    }

    // Behind a reverse proxy (caddy) the socket peer is the proxy container;
    // trust X-Forwarded-For/Proto so audit rows and logs record real client IPs.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Container pattern: accept forwarded headers from any network hop,
        // because the proxy's container IP is unpredictable across deployments.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });

    // Brute-force protection: strict fixed-window limit per client IP on the
    // unauthenticated auth endpoints (login + bootstrap-admin). Applied as the
    // global limiter with a path filter — see UseRateLimiter below.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = static (context, _) =>
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            return ValueTask.CompletedTask;
        };
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var path = httpContext.Request.Path;
            var isAuthAttempt = HttpMethods.IsPost(httpContext.Request.Method)
                && (path.StartsWithSegments("/api/auth/login")
                    || path.StartsWithSegments("/api/auth/bootstrap-admin"));
            if (!isAuthAttempt)
                return RateLimitPartition.GetNoLimiter(string.Empty);

            // UseForwardedHeaders runs first in the pipeline, so RemoteIpAddress
            // is the real client IP (not the proxy) by the time this executes.
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ =>
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0, // reject immediately — no queue on a brute-force surface
                });
        });
    });

    // ── EF Core (SQL Server) ────────────────────────────────────────────────
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(connectionString, sql =>
        {
            sql.EnableRetryOnFailure();
            // Migrations live in the Web project so `dotnet ef` from here works out of the box.
            sql.MigrationsAssembly(typeof(Program).Assembly.GetName().Name);
        }));
    // Bind the app-level interface so services don't need to know the concrete type.
    builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

    // ── File storage ─────────────────────────────────────────────────────────
    builder.Services.Configure<LocalFileStorageOptions>(opts => opts.RootPath = fileStorageRoot);
    builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

    // ── Audit logging (append-only, via EF) ─────────────────────────────────
    builder.Services.AddScoped<IAuditLogger, AuditLogger>();

    // ── Python grading service (HttpClient + abstraction) ────────────────────
    // InternalKey (GRADING_INTERNAL_KEY in .env) is sent as X-Internal-Key on
    // every call; the header is attached in GradingClient's constructor via
    // DefaultRequestHeaders. Empty key + Python ENVIRONMENT=production → the
    // Python side rejects everything (fail-closed).
    builder.Services.Configure<GradingServiceOptions>(opts =>
    {
        opts.BaseUrl = gradingServiceBaseUrl;
        opts.InternalKey = builder.Configuration["GradingService:InternalKey"];
    });
    builder.Services.AddHttpClient<IGradingClient, GradingClient>(client =>
        {
            client.BaseAddress = new Uri(gradingServiceBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(120);
        })
        // Retry once on timeout — grading models can be slow under load.
        .AddStandardResilienceHandler(); // requires Microsoft.Extensions.Http.Resilience (built into .NET 8+)
    // Also register a named client used by HealthController
    builder.Services.AddHttpClient("grading");

    // ── Application services ─────────────────────────────────────────────────
    builder.Services.AddScoped<IExamService, ExamService>();
    builder.Services.AddScoped<IQuestionService, QuestionService>();
    builder.Services.AddScoped<IAnswerService, AnswerService>();
    builder.Services.AddScoped<IGradingOrchestrationService, GradingOrchestrationService>();
    builder.Services.AddScoped<ITeacherReviewService, TeacherReviewService>();
    builder.Services.AddScoped<EvaluationService>();

    // ── Authentication: bearer JWT (identity NEVER from headers) ─────────────
    // Fail-fast on unsafe keys BEFORE any request can be served:
    //   - missing/empty → always fatal;
    //   - the publicly known dev literal → fatal in Production (anyone could
    //     forge an Admin token). Development keeps using it via
    //     appsettings.Development.json so bare `dotnet run` still works.
    var jwtSigningKey = builder.Configuration["Jwt:SigningKey"];
    if (string.IsNullOrWhiteSpace(jwtSigningKey))
        throw new InvalidOperationException(
            "Missing Jwt:SigningKey. Generate one with: openssl rand -base64 48  (or set JWT_SIGNING_KEY in .env)");
    const string DevSigningKeyLiteral =
        "dev-only-signing-key-change-me-in-production-0123456789abcdef0123456789abcdef";
    if (!builder.Environment.IsDevelopment() &&
        string.Equals(jwtSigningKey.Trim(), DevSigningKeyLiteral, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Jwt:SigningKey is set to the PUBLIC dev default while running outside Development — " +
            "anyone could forge an admin token. Generate a real key: openssl rand -base64 48, " +
            "then set JWT_SIGNING_KEY in .env.");
    }
    var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "econgrader";
    var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "econgrader-api";

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtIssuer,
                ValidateAudience = true,
                ValidAudience = jwtAudience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                // Role claim arrives as "role" — map it so [Authorize(Roles=...)] works.
                RoleClaimType = ClaimTypes.Role,
                NameClaimType = ClaimTypes.NameIdentifier,
            };
        });
    builder.Services.AddAuthorization();
    builder.Services.AddSingleton<ITokenService, JwtTokenService>();
    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<IAccessScopeService, AccessScopeService>();
    // Audit rows fall back to this when a service call site has no user id —
    // keeps every mutation attributed to the real authenticated account.
    builder.Services.AddScoped<EconGrader.Application.Interfaces.IAuditUserProvider, AuditUserProvider>();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<CurrentUser>(sp =>
        CurrentUser.From(sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User)
        ?? throw new UnauthorizedAccessException("No authenticated user on request"));

    // ── MVC / OpenAPI ────────────────────────────────────────────────────────
    builder.Services.AddControllers()
        .AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.WriteIndented = false;
            opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi();

    // ── CORS ─────────────────────────────────────────────────────────────────
    builder.Services.AddCors(o =>
    {
        if (corsOrigins.Length == 0)
        {
            // Default-deny: no CORS policy at all. Same-origin requests are
            // unaffected; cross-origin browser calls get no CORS headers.
        }
        else if (corsOrigins.Length == 1 && corsOrigins[0] == "*")
        {
            o.AddDefaultPolicy(policy => policy.AllowAnyOrigin());
        }
        else
        {
            o.AddDefaultPolicy(policy =>
                policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod());
        }
    });
    // Accept large multipart uploads (answer scans)
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    {
        o.MultipartBodyLengthLimit = 50 * 1024 * 1024;
        o.ValueLengthLimit = int.MaxValue;
    });

    var app = builder.Build();

    // ── Pipeline ─────────────────────────────────────────────────────────────
    // Forwarded headers MUST run first: every downstream component (request
    // logging, audit attribution, rate limiter partitioning) needs the real
    // client IP/proto, not the proxy container's.
    app.UseForwardedHeaders();
    // Correlation ID so every downstream log line and error payload
    // can carry it (frontend shows it on failures for easy tracing).
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();
    // Single place translating exceptions → HTTP problem responses.
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    // Auth endpoints (login/bootstrap) are IP-rate-limited; everything else
    // passes through the global NoLimiter partition untouched.
    app.UseRateLimiter();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }
    else
    {
        // .dev is HSTS-preloaded; the proxy terminates TLS but the API must
        // still declare the policy for direct-HTTP clients.
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    // Auto-migrate the database at startup (safe: idempotent).
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            await db.Database.MigrateAsync();
            Log.Information("Database migrated successfully");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Database migration failed — will retry on next start. Message: {Message}", ex.Message);
        }
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fatal startup failure");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
