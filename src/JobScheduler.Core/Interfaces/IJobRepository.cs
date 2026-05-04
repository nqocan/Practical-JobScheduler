using JobScheduler.Core.Entities;
using JobScheduler.Core.Enums;

namespace JobScheduler.Core.Interfaces;

public interface IJobRepository
{
    Task<Job> GetByIdAsync(Guid id);
    Task<IEnumerable<Job>> GetAllAsync(JobStatus? status = null);
    Task AddAsync(Job job);
    Task UpdateAsync(Job job);
}
