using JobScheduler.Core.Interfaces;
using JobScheduler.Infrastructure.Messaging;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace JobScheduler.IntegrationTests;

public class JobsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:15-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder().WithImage("rabbitmq:3-management").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbit.StartAsync());

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobSchedulerDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public new async Task DisposeAsync()
    {
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync(), _rabbit.StopAsync());
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace DbContext
            var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<JobSchedulerDbContext>));
            if (dbDescriptor != null) services.Remove(dbDescriptor);
            services.AddDbContext<JobSchedulerDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()));

            // Replace Redis
            var redisDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IConnectionMultiplexer));
            if (redisDescriptor != null) services.Remove(redisDescriptor);
            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(_redis.GetConnectionString()));

            // Replace RabbitMQ
            var factoryDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IConnectionFactory));
            if (factoryDescriptor != null) services.Remove(factoryDescriptor);
            var queueDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IJobQueue));
            if (queueDescriptor != null) services.Remove(queueDescriptor);

            services.AddSingleton<IConnectionFactory>(_ => new ConnectionFactory
            {
                Uri = new Uri(_rabbit.GetConnectionString())
            });
            services.AddSingleton<IJobQueue>(sp =>
                RabbitMqJobQueue.CreateAsync(
                    sp.GetRequiredService<IConnectionFactory>(),
                    sp.GetRequiredService<ILogger<RabbitMqJobQueue>>()
                ).GetAwaiter().GetResult());
        });
    }
}
