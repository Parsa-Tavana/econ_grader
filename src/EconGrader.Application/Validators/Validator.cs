namespace EconGrader.Application.Validators;

/// <summary>Centralized validation — thin helper, not a framework. Replace with FluentValidation if complexity grows.</summary>
public static class Validator
{
    public static void NotEmpty(string? value, string field) =>
        _ = !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"{field} is required", field);

    public static void Length(string? value, string field, int min, int max)
    {
        var len = value?.Length ?? 0;
        if (len < min) throw new ArgumentException($"{field} too short (min {min})", field);
        if (len > max) throw new ArgumentException($"{field} too long (max {max} chars)", field);
    }

    public static void Range(decimal value, string field, decimal min, decimal max)
    {
        if (value < min || value > max)
            throw new ArgumentException($"{field} out of range [{min}..{max}] (got {value})", field);
    }

    public static void Range(int value, string field, int min, int max)
    {
        if (value < min || value > max)
            throw new ArgumentException($"{field} out of range [{min}..{max}] (got {value})", field);
    }

    public static void OneOf<T>(T value, string field, params T[] options) where T : notnull
    {
        if (!options.Contains(value))
            throw new ArgumentException($"{field} must be one of: {string.Join(", ", options)} (got '{value}')", field);
    }
}