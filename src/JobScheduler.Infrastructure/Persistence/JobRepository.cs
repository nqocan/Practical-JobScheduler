using JobScheduler.Core.Entities;
using JobScheduler.Core.Enums;
using JobScheduler.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Persistence;

public class JobRepository(JobSchedulerDbContext db) : IJobRepository
{
    public async Task<Job> GetByIdAsync(Guid id)
    {
        return await db.Jobs.FindAsync(id)
            ?? throw new KeyNotFoundException($"Job {id} not found.");
    }

    public async Task<IEnumerable<Job>> GetAllAsync(JobStatus? status = null)
    {
        var query = db.Jobs.AsQueryable();
        if (status.HasValue)
            query = query.Where(j => j.Status == status.Value);
        return await query.ToListAsync();
    }

    public async Task AddAsync(Job job)
    {
        await db.Jobs.AddAsync(job);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Job job)
    {
        db.Jobs.Update(job);
        await db.SaveChangesAsync();
    }
}
