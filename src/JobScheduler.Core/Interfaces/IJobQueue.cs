using JobScheduler.Core.Entities;

namespace JobScheduler.Core.Interfaces;

public interface IJobQueue
{
    Task EnqueueAsync(Job job);
    Task<Job?> DequeueAsync(CancellationToken cancellationToken);
}
