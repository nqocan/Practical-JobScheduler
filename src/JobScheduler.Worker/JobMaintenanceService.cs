using JobScheduler.Core.Interfaces;

namespace JobScheduler.Worker;

public class JobMaintenanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<JobMaintenanceService> logger
) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RunCleanLoopAsync(stoppingToken), RunRecoveryLoopAsync(stoppingToken));

    private async Task RunCleanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next4Am = now.Date.AddHours(4);
            if (now >= next4Am)
                next4Am = next4Am.AddDays(1);

            await Task.Delay(next4Am - now, ct);

            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            await repository.DeleteOldJobsAsync(DateTime.UtcNow.AddDays(-1));
            logger.LogInformation("Deleted completed/failed jobs not updated in the last 24 hours");
        }
    }

    private async Task RunRecoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            await repository.ResetStuckJobsAsync(DateTime.UtcNow.AddMinutes(-10));
            logger.LogInformation("Reset stuck jobs older than 10 minutes");
        }
    }
}
