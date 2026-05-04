using JobScheduler.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Persistence;

public class JobSchedulerDbContext(DbContextOptions<JobSchedulerDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Type).HasConversion<string>();
            entity.Property(j => j.Status).HasConversion<string>();
            entity.Property(j => j.Payload).IsRequired();
        });
    }
}
