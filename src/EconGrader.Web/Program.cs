using Microsoft.EntityFrameworkCore;
using Serilog;
using EconGrader.Application.Data;
using EconGrader.Application.Evaluation;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;
using EconGrader.Infrastructure.Services;
using EconGrader.Infrastructure.Storage;
using EconGrader.Web.Middleware;

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

    // ── MVC / OpenAPI ────────────────────────────────────────────────────────
    builder.Services.AddControllers()
        .AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.WriteIndented = false;
            opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi();
    // Accept large multipart uploads (answer scans)
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    {
        o.MultipartBodyLengthLimit = 50 * 1024 * 1024;
        o.ValueLengthLimit = int.MaxValue;
    });

    var app = builder.Build();

    // ── Pipeline ─────────────────────────────────────────────────────────────
    app.UseSerilogRequestLogging();
    // Single place translating exceptions → HTTP problem responses.
    app.UseMiddleware<ExceptionHandlingMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();
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
