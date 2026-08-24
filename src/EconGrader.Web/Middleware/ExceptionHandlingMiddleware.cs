using System.Text.Json;
using EconGrader.Application.Exceptions;

namespace EconGrader.Web.Middleware;

/// <summary>
/// Single place that converts exceptions to HTTP responses.
/// Controllers never try/catch — they throw, this translates.
/// Every error payload carries the correlation ID so a frontend-visible
/// failure can be traced to the exact backend log entry.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try { await _next(context); }
        catch (Exception ex) { await HandleAsync(context, ex); }
    }

    private async Task HandleAsync(HttpContext context, Exception ex)
    {
        var correlationId = context.Items[CorrelationIdMiddleware.ItemKey] as string ?? context.TraceIdentifier;

        var (status, code, message) = ex switch
        {
            DomainException de => (de.StatusCode, de.ErrorCode, de.Message),
            ArgumentException => (400, "VALIDATION_ERROR", ex.Message),
            // Real authorization failures only. NOTE: filesystem permission
            // problems used to land here via UnauthorizedAccessException and
            // were misreported as 403 Forbidden — see StorageException below.
            UnauthorizedAccessException when ex.Source != "System.IO.FileSystem"
                => (403, "FORBIDDEN", "Access denied"),
            // Storage/IO failures are server-side configuration problems,
            // not client authorization issues → 503 with actionable code.
            UnauthorizedAccessException => (503, "STORAGE_ACCESS_DENIED",
                "File storage is not accessible — server configuration issue"),
            IOException io when IsStorageRelated(io)
                => (503, "STORAGE_UNAVAILABLE", "File storage is unavailable"),
            HttpRequestException hre => (502, "DEPENDENCY_UNAVAILABLE", $"Grading service unavailable: {hre.Message}"),
            TaskCanceledException when !context.RequestAborted.IsCancellationRequested
                => (504, "TIMEOUT", "Upstream service timed out"),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested
                => throw ex, // client disconnected; let Kestrel handle it
            _ => (500, "INTERNAL_ERROR", "An unexpected error occurred"),
        };

        // Structured log with correlation + request context for traceability.
        if (status >= 500)
        {
            _logger.LogError(ex,
                "Unhandled exception {ErrorCode} {StatusCode} {Method} {Path} CorrelationId={CorrelationId}",
                code, status, context.Request.Method, context.Request.Path, correlationId);
        }
        else if (status != 404)
        {
            _logger.LogWarning(
                "Request rejected {ErrorCode} {StatusCode} {Method} {Path} CorrelationId={CorrelationId}",
                code, status, context.Request.Method, context.Request.Path, correlationId);
        }

        if (context.Response.HasStarted) return;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        var payload = new ProblemDetailsBody(status, code, message, correlationId);
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOpts));
    }

    private static bool IsStorageRelated(IOException _) => true;

    private sealed record ProblemDetailsBody(int Status, string Code, string Message, string TraceId);
}