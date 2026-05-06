using JobScheduler.Core.Enums;

namespace JobScheduler.Core.Entities;

public class Job
{
    public Guid Id { get; private set; }
    public JobType Type { get; private set; }
    public JobStatus Status { get; private set; }
    public string Payload { get; private set; } = null!;
    public int RetryCount { get; private set; }
    public int MaxRetries { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private Job() { }

    public static Job Create(JobType type, string payload, int maxRetries = 3)
    {
        return new Job
        {
            Id = Guid.NewGuid(),
            Type = type,
            Status = JobStatus.Pending,
            Payload = payload,
            RetryCount = 0,
            MaxRetries = maxRetries,
            CreatedAt = DateTime.UtcNow,
        };
    }

    public void MarkAsRunning()
    {
        Status = JobStatus.Running;
        StartedAt = DateTime.UtcNow;
    }

    public void MarkAsCompleted()
    {
        Status = JobStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkAsFailed(string errorMessage)
    {
        ErrorMessage = errorMessage;
        RetryCount++;

        if (RetryCount >= MaxRetries)
            Status = JobStatus.Failed;
        else
            Status = JobStatus.Pending;
    }

    public void MarkAsCancelled()
    {
        if (Status != JobStatus.Pending)
            throw new InvalidOperationException("Only pending jobs can be cancelled.");

        Status = JobStatus.Cancelled;
    }
}
