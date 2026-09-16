using EconGrader.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace EconGrader.Web.Services;

/// <summary>
/// M6 bulk split worker: same shape as the grading worker — AppRole=worker
/// only, fresh DI scope per pass, DB-readiness wait for a fresh-volume boot.
/// Split jobs are OCR-only (no AI tokens) so they run alongside grading jobs
/// without touching the gateway budget; the batch lease prevents two workers
/// processing the same merged upload.
/// </summary>
public sealed class BulkSplitWorkerHostedService(
    IServiceProvider services,
    ILogger<BulkSplitWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for DB readiness (auto-migrate on startup in the api role
        // races this when the worker boots first on a fresh volume).
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Application.Data.AppDbContext>();
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
                logger.LogError("Bulk split worker: database unreachable after retries — worker stopping");
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
                var splitService = scope.ServiceProvider.GetRequiredService<ISplitService>();
                await splitService.RunWorkerLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bulk split worker crashed — restarting loop in 10s");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
