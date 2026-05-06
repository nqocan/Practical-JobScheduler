using JobScheduler.Core.Interfaces;
using JobScheduler.Infrastructure.Messaging;
using JobScheduler.Infrastructure.Persistence;
using JobScheduler.Worker;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// PostgreSQL
builder.Services.AddDbContext<JobSchedulerDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
);
builder.Services.AddScoped<IJobRepository, JobRepository>();

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!)
);
builder.Services.AddScoped<IJobStatusTracker, RedisJobStatusTracker>();

// RabbitMQ
builder.Services.AddSingleton<IConnectionFactory>(_ => new ConnectionFactory
{
    Uri = new Uri(builder.Configuration.GetConnectionString("RabbitMq")!),
});
builder.Services.AddSingleton<IJobQueue>(sp =>
    RabbitMqJobQueue
        .CreateAsync(
            sp.GetRequiredService<IConnectionFactory>(),
            sp.GetRequiredService<ILogger<RabbitMqJobQueue>>()
        )
        .GetAwaiter()
        .GetResult()
);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
