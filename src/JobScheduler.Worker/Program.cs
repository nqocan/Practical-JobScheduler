using JobScheduler.Core.Interfaces;
using JobScheduler.Infrastructure.Messaging;
using JobScheduler.Infrastructure.Persistence;
using JobScheduler.Worker;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// PostgreSQL
builder.Services.AddDbContext<JobSchedulerDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddScoped<IJobRepository, JobRepository>();

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));
builder.Services.AddScoped<IJobStatusTracker, RedisJobStatusTracker>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<JobMaintenanceService>();

var host = builder.Build();
host.Run();
