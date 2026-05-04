using JobScheduler.Core.Enums;
using JobScheduler.Core.Interfaces;
using StackExchange.Redis;

namespace JobScheduler.Infrastructure.Messaging;

public class RedisJobStatusTracker(IConnectionMultiplexer redis) : IJobStatusTracker
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task SetStatusAsync(Guid jobId, JobStatus status)
    {
        await _db.StringSetAsync(Key(jobId), status.ToString());
    }

    public async Task<JobStatus?> GetStatusAsync(Guid jobId)
    {
        var value = await _db.StringGetAsync(Key(jobId));
        if (value.IsNullOrEmpty)
            return null;

        return Enum.Parse<JobStatus>(value!);
    }

    private static string Key(Guid jobId) => $"job:status:{jobId}";
}
