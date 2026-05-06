using System.Text;
using System.Text.Json;
using JobScheduler.Core.Entities;
using JobScheduler.Core.Interfaces;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace JobScheduler.Infrastructure.Messaging;

public class RabbitMqJobQueue : IJobQueue, IAsyncDisposable
{
    private const string QueueName = "jobs";

    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly ILogger<RabbitMqJobQueue> _logger;

    private RabbitMqJobQueue(
        IConnection connection,
        IChannel channel,
        ILogger<RabbitMqJobQueue> logger
    )
    {
        _connection = connection;
        _channel = channel;
        _logger = logger;
    }

    public static async Task<RabbitMqJobQueue> CreateAsync(
        IConnectionFactory factory,
        ILogger<RabbitMqJobQueue> logger
    )
    {
        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(
            QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false
        );
        return new RabbitMqJobQueue(connection, channel, logger);
    }

    public async Task EnqueueAsync(Job job)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job));
        var props = new BasicProperties { Persistent = true };
        await _channel.BasicPublishAsync(
            exchange: "",
            routingKey: QueueName,
            mandatory: false,
            basicProperties: props,
            body: body
        );
        _logger.LogInformation("Enqueued job {JobId}", job.Id);
    }

    public async Task<Job?> DequeueAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<Job?>();
        cancellationToken.Register(() => tcs.TrySetResult(null));

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var job = JsonSerializer.Deserialize<Job>(ea.Body.Span);
            await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            tcs.TrySetResult(job);
        };

        await _channel.BasicConsumeAsync(
            QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken
        );
        return await tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
