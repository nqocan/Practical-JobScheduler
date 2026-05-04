using JobScheduler.Core.Enums;
using JobScheduler.Core.Interfaces;

namespace JobScheduler.Worker;

public class Worker(
    IJobQueue jobQueue,
    IJobRepository jobRepository,
    IJobStatusTracker statusTracker,
    ILogger<Worker> logger) : BackgroundService
{
    private const int MaxConcurrency = 5;
    private const int TimeoutSeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var semaphore = new SemaphoreSlim(MaxConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await jobQueue.DequeueAsync(stoppingToken);
            if (job is null) continue;

            await semaphore.WaitAsync(stoppingToken);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessJobAsync(job, stoppingToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(Core.Entities.Job job, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        try
        {
            logger.LogInformation("Starting job {JobId} (type={Type})", job.Id, job.Type);
            job.MarkAsRunning();
            await jobRepository.UpdateAsync(job);
            await statusTracker.SetStatusAsync(job.Id, JobStatus.Running);

            await ExecuteJobAsync(job, cts.Token);

            job.MarkAsCompleted();
            await jobRepository.UpdateAsync(job);
            await statusTracker.SetStatusAsync(job.Id, JobStatus.Completed);
            logger.LogInformation("Completed job {JobId}", job.Id);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} timed out after {Timeout}s", job.Id, TimeoutSeconds);
            await HandleFailureAsync(job, "Job timed out.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed", job.Id);
            await HandleFailureAsync(job, ex.Message);
        }
    }

    private async Task HandleFailureAsync(Core.Entities.Job job, string error)
    {
        job.MarkAsFailed(error);
        await jobRepository.UpdateAsync(job);

        var finalStatus = job.Status == JobStatus.Pending ? JobStatus.Pending : JobStatus.Failed;
        await statusTracker.SetStatusAsync(job.Id, finalStatus);

        if (job.Status == JobStatus.Pending)
        {
            var delay = TimeSpan.FromSeconds(Math.Pow(2, job.RetryCount));
            logger.LogInformation("Retrying job {JobId} in {Delay}s (attempt {Attempt})", job.Id, delay.TotalSeconds, job.RetryCount);
            await Task.Delay(delay);
            await jobQueue.EnqueueAsync(job);
        }
    }

    private static Task ExecuteJobAsync(Core.Entities.Job job, CancellationToken cancellationToken)
    {
        return job.Type switch
        {
            JobType.Email => SimulateAsync(cancellationToken),
            JobType.Export => SimulateAsync(cancellationToken),
            JobType.Sync => SimulateAsync(cancellationToken),
            _ => throw new NotSupportedException($"Unknown job type: {job.Type}")
        };
    }

    private static Task SimulateAsync(CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
}
