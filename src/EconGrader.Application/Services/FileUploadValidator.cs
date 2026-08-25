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
            // Excel: browsers send different MIME types for .xls/.xlsx depending on
            // OS/browser, so accept every common variant (see IsAllowedContentType).
            ["xlsx"] = new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            ["xls"] = new[] { "application/vnd.ms-excel" },
        };

    /// <summary>Human-readable list of accepted file types, used in error messages.</summary>
    public const string AcceptedTypesDisplay = "PDF, PNG, JPG, DOCX, XLSX or XLS";

    public static bool IsAllowedContentType(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        // Some browsers/OSes report generic or legacy MIME types for Office files;
        // those are accepted only when ExtensionForFile resolves a safe extension.
        if (contentType.ToLowerInvariant() is "application/octet-stream" or "application/excel" or "application/x-excel")
            return true;
        return AllowedByCategory.Values.SelectMany(v => v).Contains(contentType, StringComparer.OrdinalIgnoreCase);
    }

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
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.ms-excel" => ".xls",
        "application/octet-stream" or "application/excel" or "application/x-excel" => null,
        _ => null,
    };

    /// <summary>
    /// Resolves the storage extension from the content type first, then from the
    /// original file name. Returns null when neither maps to an allowed type.
    /// Callers that accept generic MIME types must use this instead of ExtensionFor.
    /// </summary>
    public static string? ExtensionForFile(string contentType, string? fileName)
    {
        var byType = ExtensionFor(contentType);
        if (byType != null) return byType;

        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".pdf" or ".docx" or ".xlsx" or ".xls"
            ? (ext == ".jpeg" ? ".jpg" : ext)
            : null;
    }
}
