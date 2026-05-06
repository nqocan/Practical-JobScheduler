using JobScheduler.Core.Entities;
using JobScheduler.Core.Enums;

namespace JobScheduler.API.Models;

public record JobResponse(
    Guid Id,
    JobType Type,
    JobStatus Status,
    int RetryCount,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt
)
{
    public static JobResponse From(Job job) =>
        new(
            job.Id,
            job.Type,
            job.Status,
            job.RetryCount,
            job.ErrorMessage,
            job.CreatedAt,
            job.UpdatedAt,
            job.StartedAt,
            job.CompletedAt
        );
}
