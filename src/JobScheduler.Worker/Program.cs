using JobScheduler.Core.Interfaces;
using JobScheduler.Infrastructure.Persistence;
using JobScheduler.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<JobSchedulerDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
);
builder.Services.AddScoped<IJobRepository, JobRepository>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<JobMaintenanceService>();

var host = builder.Build();
host.Run();
