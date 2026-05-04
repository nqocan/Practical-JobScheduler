using JobScheduler.Core.Enums;

namespace JobScheduler.Core.Interfaces;

public interface IJobStatusTracker
{
    Task SetStatusAsync(Guid jobId, JobStatus status);
    Task<JobStatus?> GetStatusAsync(Guid jobId);
}
