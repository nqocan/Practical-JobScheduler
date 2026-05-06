using JobScheduler.Core.Interfaces;

namespace JobScheduler.Worker;

public class JobMaintenanceService(IServiceScopeFactory scopeFactory, ILogger<JobMaintenanceService> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            RunCleanLoopAsync(stoppingToken),
            RunRecoveryLoopAsync(stoppingToken)
        );

    private async Task RunCleanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(24), ct);
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            await repository.DeleteOldJobsAsync(DateTime.UtcNow.AddDays(-7));
            logger.LogInformation("Deleted jobs older than 7 days");
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
