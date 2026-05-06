# Worker Improvements Design

**Date:** 2026-05-05
**Branch target:** `feat/step-7-worker-improvements`
**Scope:** Learning/portfolio

---

## Context

The current `Worker.cs` has two weaknesses identified by comparing with Haravan's `omni-workers`:

1. `Task.Run` has no backpressure — if RabbitMQ delivers jobs faster than they're processed, the worker keeps dequeuing without any throttle.
2. No in-flight tracking — RabbitMQ's at-least-once delivery can cause the same job to be processed twice if a message is redelivered before the first run finishes.

Additionally the `Jobs` table has no cleanup — it grows indefinitely, and jobs stuck in `Running` (from a Worker crash) are never recovered.

---

## Goals

1. Replace `Task.Run` with `Channel<T>` for bounded backpressure
2. Add `ConcurrentDictionary` to track and guard in-flight jobs
3. Add `JobMaintenanceService` — periodic cleanup of old jobs and recovery of stuck jobs

---

## Design

### 1. `Worker.cs` — Channel + In-flight tracking

Split `ExecuteAsync` into two cooperating loops:

```
ProduceAsync  — dequeues from RabbitMQ → writes to Channel (blocks when full)
ConsumeAsync  — reads from Channel → runs up to 5 concurrent ProcessJobAsync calls
```

**Channel configuration:**
```csharp
Channel.CreateBounded<Job>(new BoundedChannelOptions(capacity: 10)
{
    FullMode = BoundedChannelFullMode.Wait,  // producer waits when full
    SingleWriter = true,
    SingleReader = false
});
```

Capacity 10 = 5 active slots + 5 buffered. Producer blocks automatically when all 10 are full — no unbounded dequeue accumulation.

**In-flight guard:**
```csharp
private readonly ConcurrentDictionary<Guid, bool> _inFlight = new();

// in ConsumeAsync, before processing:
if (!_inFlight.TryAdd(job.Id, true)) continue;  // already running, skip

// in ProcessJobAsync finally block:
_inFlight.TryRemove(job.Id, out _);
```

Protects against RabbitMQ redelivery causing double-execution of the same job.

**New `Worker.cs` structure:**
```
ExecuteAsync
  └── Task.WhenAll(
        ProduceAsync(channel.Writer, stoppingToken),
        ConsumeAsync(channel.Reader, stoppingToken)
      )

ProduceAsync(writer, ct)
  loop: job = await DequeueAsync → await writer.WriteAsync(job)

ConsumeAsync(reader, ct)
  loop: job = await reader.ReadAsync
        → semaphore.WaitAsync (max 5)
        → Task.Run(ProcessJobAsync) with finally: semaphore.Release + _inFlight.Remove
```

---

### 2. `JobMaintenanceService.cs` — New BackgroundService

Registered alongside `Worker` in `Program.cs`. Runs two independent periodic loops:

**CleanOldJobsAsync — every 24 hours:**
- Deletes jobs with `Status IN (Completed, Failed, Cancelled)` older than 7 days
- Uses EF Core `ExecuteDeleteAsync` — single DELETE query, no entity tracking

**RecoverStuckJobsAsync — every 5 minutes:**
- Resets jobs with `Status = Running` and `StartedAt < now - 10 minutes` back to `Pending`
- Uses EF Core `ExecuteUpdateAsync` — single UPDATE query, no entity tracking
- Covers the Worker crash scenario: job was mid-processing, never got `BasicAck`, message was redelivered but status is stuck at `Running`

```csharp
public class JobMaintenanceService(IServiceScopeFactory scopeFactory, ILogger<JobMaintenanceService> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(RunCleanLoopAsync(ct), RunRecoveryLoopAsync(ct));
}
```

Uses `IServiceScopeFactory` (not `IJobRepository` directly) because `DbContext` is scoped and `BackgroundService` is singleton.

---

### 3. `IJobRepository` — 2 new methods

```csharp
Task DeleteOldJobsAsync(DateTime olderThan);
Task ResetStuckJobsAsync(DateTime stuckSince);
```

Implemented in `JobRepository` with EF Core bulk operations:
```csharp
// DeleteOldJobsAsync
await db.Jobs
    .Where(j => completedStatuses.Contains(j.Status) && j.CreatedAt < olderThan)
    .ExecuteDeleteAsync();

// ResetStuckJobsAsync
await db.Jobs
    .Where(j => j.Status == JobStatus.Running && j.StartedAt < stuckSince)
    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Pending));
```

---

## Files Changed

| File | Change |
|------|--------|
| `src/JobScheduler.Worker/Worker.cs` | Refactor: Channel + ConcurrentDictionary |
| `src/JobScheduler.Worker/JobMaintenanceService.cs` | New BackgroundService |
| `src/JobScheduler.Worker/Program.cs` | Register `JobMaintenanceService` |
| `src/JobScheduler.Core/Interfaces/IJobRepository.cs` | Add 2 new methods |
| `src/JobScheduler.Infrastructure/Persistence/JobRepository.cs` | Implement 2 new methods |

---

## What is NOT in scope

- Configurable thresholds (cleanup age, stuck timeout) — hardcoded is fine for learning
- Integration tests for maintenance service — out of scope
- Outbox pattern — separate concern, not part of this change
- Idempotency beyond in-flight guard — not needed for learning project
