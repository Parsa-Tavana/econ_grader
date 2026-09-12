using System.Net.Http.Json;
using System.Text.Json;
using EconGrader.Application.DTOs;
using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EconGrader.Infrastructure.Services;

/// <summary>
/// HTTP client to the internal Python grading microservice — the ONLY class
/// allowed to talk to that service. Every other class goes through this.
/// </summary>
public sealed class GradingServiceOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5001";
    public string? InternalKey { get; set; }
}

public sealed class GradingClient : IGradingClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GradingClient> _logger;
    private readonly JsonSerializerOptions _json;
    private readonly JsonSerializerOptions _extractJson;

    public GradingClient(HttpClient http, ILogger<GradingClient> logger, Microsoft.Extensions.Options.IOptions<GradingServiceOptions> options)
    {
        _http = http;
        _logger = logger;
        // Internal service auth: when configured, every request to the Python
        // service carries X-Internal-Key (verified by app/internal_auth.py).
        var internalKey = options.Value.InternalKey;
        if (!string.IsNullOrWhiteSpace(internalKey))
            _http.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);
        // Python service speaks snake_case (pydantic models without aliases) —
        // bind ai_score/is_valid/... onto our PascalCase records.
        _json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        // Extraction responses bind via explicit JsonPropertyName attributes
        // (short keys like "id" that SnakeCaseLower would mangle).
        _extractJson = new JsonSerializerOptions();
    }

    public async Task<GradingServiceResponse> GradeAsync(GradingServiceRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            student_id = request.StudentId,
            question_id = request.QuestionId,
            question_text = request.QuestionText ?? string.Empty,
            rubric = new { criteria = request.Rubric.Criteria.Select(c => new { id = c.Id, description = c.Description, max_score = c.MaxScore }) },
            answer_image_paths = request.AnswerImagePaths,
            question_image_paths = request.QuestionImagePaths,
            max_score = request.MaxScore,
            temperature = request.Temperature,
            prompt_version = request.PromptVersion,
        };

        _logger.LogInformation("Sending grading request for student {StudentId} / question {QuestionId}",
            request.StudentId, request.QuestionId);

        var resp = await _http.PostAsJsonAsync("/grade", payload, _json, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Grading service returned {Status}: {Body}", resp.StatusCode, body);
            throw new HttpRequestException($"Grading service error {resp.StatusCode}: {body}");
        }

        var parsed = JsonSerializer.Deserialize<GradingServiceResponse>(body, _json);
        if (parsed is null)
            throw new DependencyException("GradingService", "Failed to deserialize grading response");
        return parsed;
    }

    public async Task<ExtractionServiceResponse> ExtractAsync(string absoluteFilePath, string fileName, CancellationToken ct = default)
    {
        // The extraction contract uses short snake_case keys (id, max_score) —
        // deserialize with explicit attribute mappings, not the global naming
        // policy (which would expect criterion_id and mangle the binding).
        var payload = new
        {
            file_paths = new[] { absoluteFilePath },
            document_name = fileName,
            temperature = 0.0,
            prompt_version = "extract",
        };

        _logger.LogInformation("Sending extraction request for rubric document {FileName}", fileName);

        var resp = await _http.PostAsJsonAsync("/extract", payload, _json, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Extraction service returned {Status}: {Body}", resp.StatusCode, body);
            throw new HttpRequestException($"Grading service extraction error {resp.StatusCode}: {body}");
        }

        var parsed = JsonSerializer.Deserialize<ExtractionServiceResponse>(body, _extractJson);
        if (parsed is null)
            throw new DependencyException("GradingService", "Failed to deserialize extraction response");
        return parsed;
    }

    public async Task<TEvaluationResult?> EvaluateAsync(IEnumerable<(decimal TeacherScore, decimal AiScore)> runs, CancellationToken ct = default)
    {
        var payload = new
        {
            runs = runs.Select(r => new { teacher_score = r.TeacherScore, ai_score = r.AiScore })
        };

        var resp = await _http.PostAsJsonAsync("/evaluate", payload, _json, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<TEvaluationResult>(body, _json);
    }

    public async Task<IReadOnlyList<string>> GetPromptVersionsAsync(CancellationToken ct = default)
    {
        var body = await _http.GetStringAsync("/prompts", ct);
        var doc = JsonSerializer.Deserialize<JsonElement>(body, _json);
        var versions = new List<string>();
        if (doc.TryGetProperty("prompts", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                var s = item.GetString();
                if (s != null) versions.Add(s);
            }
        }
        return versions;
    }
}