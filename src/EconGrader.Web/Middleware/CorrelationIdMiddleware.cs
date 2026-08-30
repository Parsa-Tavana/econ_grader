namespace EconGrader.Web.Middleware;

/// <summary>
/// Assigns a correlation ID to every request (honours an incoming
/// X-Correlation-Id), exposes it via the X-Correlation-Id response header,
/// and makes it available through IHttpContextAccessor as
/// "CorrelationId" for Serilog enrichment and error payloads.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId =
            context.Request.Headers[HeaderName].FirstOrDefault()
            ?? context.TraceIdentifier;

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        await _next(context);
    }
}