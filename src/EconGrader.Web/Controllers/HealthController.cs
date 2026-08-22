using Microsoft.AspNetCore.Mvc;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;

    public HealthController(IHttpClientFactory httpFactory, IConfiguration config)
    {
        _httpFactory = httpFactory;
        _config = config;
    }

    /// <summary>Checks the API process plus reachability of the Python grading service.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var baseUrl = _config["GradingService:BaseUrl"] ?? "http://localhost:5001";
        bool pythonUp;
        try
        {
            var client = _httpFactory.CreateClient("grading");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var resp = await client.GetAsync($"{baseUrl}/health", cts.Token);
            pythonUp = resp.IsSuccessStatusCode;
        }
        catch
        {
            pythonUp = false;
        }

        return Ok(new
        {
            status = "ok",
            service = "EconGrader.Web",
            timestamp = DateTime.UtcNow,
            dependencies = new { gradingService = new { url = baseUrl, up = pythonUp } },
        });
    }
}
