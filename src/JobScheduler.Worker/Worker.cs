using System.Collections.Concurrent;
using System.Threading.Channels;
using JobScheduler.Core.Entities;
using JobScheduler.Core.Enums;
using JobScheduler.Core.Interfaces;

namespace JobScheduler.Worker;

public class Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger) : BackgroundService
{
    private const int MaxConcurrency = 5;
    private const int BatchSize = 10;
    private const int PollIntervalMs = 5000;
    private const int TimeoutSeconds = 30;

    private readonly ConcurrentDictionary<Guid, bool> _inFlight = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(BatchSize * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });

        await Task.WhenAll(
            ProduceAsync(channel.Writer, stoppingToken),
            ConsumeAsync(channel.Reader, stoppingToken)
        );
    }

    private async Task ProduceAsync(ChannelWriter<Job> writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

            var jobs = await repository.GetPendingJobsAsync(BatchSize);
            var newJobs = jobs.Where(j => !_inFlight.ContainsKey(j.Id)).ToList();

            if (newJobs.Count == 0)
            {
                await Task.Delay(PollIntervalMs, ct);
                continue;
            }

            foreach (var job in newJobs)
            {
                if (_inFlight.TryAdd(job.Id, true))
                    await writer.WriteAsync(job, ct);
            }
        }

        writer.Complete();
    }

    private async Task ConsumeAsync(ChannelReader<Job> reader, CancellationToken ct)
    {
        var semaphore = new SemaphoreSlim(MaxConcurrency);

        await foreach (var job in reader.ReadAllAsync(ct))
        {
            await semaphore.WaitAsync(ct);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessJobAsync(job, ct);
                }
                finally
                {
                    _inFlight.TryRemove(job.Id, out _);
                    semaphore.Release();
                }
            }, ct);
        }
    }

    private async Task ProcessJobAsync(Job job, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var statusTracker = scope.ServiceProvider.GetRequiredService<IJobStatusTracker>();

        try
        {
            logger.LogInformation("Starting job {JobId} (type={Type})", job.Id, job.Type);
            job.MarkAsRunning();
            await repository.UpdateAsync(job);
            await statusTracker.SetStatusAsync(job.Id, JobStatus.Running);

            await ExecuteJobAsync(job, cts.Token);

            job.MarkAsCompleted();
            await repository.UpdateAsync(job);
            await statusTracker.SetStatusAsync(job.Id, JobStatus.Completed);
            logger.LogInformation("Completed job {JobId}", job.Id);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} timed out after {Timeout}s", job.Id, TimeoutSeconds);
            await HandleFailureAsync(job, "Job timed out.", repository, statusTracker);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed", job.Id);
            await HandleFailureAsync(job, ex.Message, repository, statusTracker);
        }
    }

    private async Task HandleFailureAsync(Job job, string error, IJobRepository repository, IJobStatusTracker statusTracker)
    {
        job.MarkAsFailed(error);
        await repository.UpdateAsync(job);

        var finalStatus = job.Status == JobStatus.Pending ? JobStatus.Pending : JobStatus.Failed;
        await statusTracker.SetStatusAsync(job.Id, finalStatus);

        if (job.Status == JobStatus.Pending)
        {
            var delay = TimeSpan.FromSeconds(Math.Pow(2, job.RetryCount));
            logger.LogInformation("Job {JobId} will retry in {Delay}s (attempt {Attempt})", job.Id, delay.TotalSeconds, job.RetryCount);
            await Task.Delay(delay);
        }
    }

    private static Task ExecuteJobAsync(Job job, CancellationToken cancellationToken) =>
        job.Type switch
        {
            JobType.Email => SimulateAsync(cancellationToken),
            JobType.Export => SimulateAsync(cancellationToken),
            JobType.Sync => SimulateAsync(cancellationToken),
            _ => throw new NotSupportedException($"Unknown job type: {job.Type}")
        };

    private static Task SimulateAsync(CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
}
