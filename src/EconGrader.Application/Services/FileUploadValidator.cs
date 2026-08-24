namespace EconGrader.Application.Services;

/// <summary>
/// Central validation for uploaded files: allowed content types and size cap.
/// Used by question, rubric and answer upload endpoints so rules stay in sync.
/// </summary>
public static class FileUploadValidator
{
    /// <summary>20 MB — matches AnswersController's existing RequestSizeLimit.</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>MIME types accepted for any attachment.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedByCategory =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // category -> [content types]
            ["image"] = new[] { "image/png", "image/jpeg" },
            ["pdf"] = new[] { "application/pdf" },
            ["docx"] = new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        };

    public static bool IsAllowedContentType(string contentType) =>
        AllowedByCategory.Values.SelectMany(v => v).Contains(contentType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a content type to a canonical extension used for storage keys.
    /// Returns null when the type is not allowed.
    /// </summary>
    public static string? ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "application/pdf" => ".pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        _ => null,
    };
}