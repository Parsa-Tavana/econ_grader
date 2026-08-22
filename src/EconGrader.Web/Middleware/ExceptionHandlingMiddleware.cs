using System.Text.Json;
using EconGrader.Application.Exceptions;

namespace EconGrader.Web.Middleware;

/// <summary>
/// Single place that converts exceptions to HTTP responses.
/// Controllers never try/catch — they throw, this translates.
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
        var (status, code, message) = ex switch
        {
            DomainException de => (de.StatusCode, de.ErrorCode, de.Message),
            ArgumentException => (400, "VALIDATION_ERROR", ex.Message),
            UnauthorizedAccessException => (403, "FORBIDDEN", "Access denied"),
            HttpRequestException hre => (502, "DEPENDENCY_UNAVAILABLE", $"Grading service unavailable: {hre.Message}"),
            TaskCanceledException when !context.RequestAborted.IsCancellationRequested
                => (504, "TIMEOUT", "Upstream service timed out"),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested
                => throw ex, // client disconnected; let Kestrel handle it
            _ => (500, "INTERNAL_ERROR", "An unexpected error occurred"),
        };

        if (status >= 500) _logger.LogError(ex, "Unhandled exception: {Message}", ex.Message);
        else if (status != 404) _logger.LogWarning("Request rejected ({Code}): {Message}", code, message);

        if (context.Response.HasStarted) return;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var payload = new ProblemDetailsBody(status, code, message, context.TraceIdentifier);
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOpts));
    }

    private sealed record ProblemDetailsBody(int Status, string Code, string Message, string TraceId);
}