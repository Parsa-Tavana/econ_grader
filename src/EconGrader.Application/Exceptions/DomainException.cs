namespace EconGrader.Application.Exceptions;

/// <summary>Base exception for domain/business rule violations — safe to return to client.</summary>
public abstract class DomainException : Exception
{
    public int StatusCode { get; }
    public string ErrorCode { get; }

    protected DomainException(string message, int statusCode, string errorCode)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}

/// <summary>Resource not found (404).</summary>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string resource, object id)
        : base($"{resource} with id '{id}' not found", 404, "NOT_FOUND") { }
}

/// <summary>Business rule violated (400/409/422).</summary>
public sealed class BusinessRuleException : DomainException
{
    public BusinessRuleException(string message, string errorCode = "BUSINESS_RULE_VIOLATION")
        : base(message, 400, errorCode) { }
}

/// <summary>External dependency failure (502/503).</summary>
public sealed class DependencyException : DomainException
{
    public DependencyException(string service, string message, int statusCode = 502)
        : base($"{service} unavailable: {message}", statusCode, "DEPENDENCY_UNAVAILABLE") { }
}