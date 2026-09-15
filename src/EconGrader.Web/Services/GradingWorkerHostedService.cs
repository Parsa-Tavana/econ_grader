using EconGrader.Application.Data;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace EconGrader.Web.Services;

/// <summary>
/// Worker-role background loop (M2). Runs only when AppRole=worker — the api
/// role serves HTTP without the loop; `docker compose --scale worker=N` adds
/// failover/throughput headroom, while the GLOBAL in-flight cap inside
/// GradingJobService.ClaimNextAsync keeps total concurrent model calls ≤ N.
/// </summary>
public sealed class GradingWorkerHostedService(
    IServiceProvider services,
    ILogger<GradingWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for DB readiness (auto-migrate on startup in the api role races
        // this when the worker boots first on a fresh volume).
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ready = false;
            for (var attempt = 0; attempt < 60 && !stoppingToken.IsCancellationRequested; attempt++)
            {
                try { ready = await db.Database.CanConnectAsync(stoppingToken); }
                catch { ready = false; }
                if (ready) break;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            if (!ready)
            {
                logger.LogError("Grading worker: database unreachable after retries — worker stopping");
                return;
            }
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Fresh scope per loop pass so a transient failure never
                // poisons the long-lived worker with a broken DbContext.
                using var scope = services.CreateScope();
                var jobService = scope.ServiceProvider.GetRequiredService<IGradingJobService>();
                await jobService.RunWorkerLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Grading worker crashed — restarting loop in 10s");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
