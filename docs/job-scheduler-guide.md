# Distributed Job Scheduler — Build Guide

> Stack: ASP.NET Core · RabbitMQ · Redis · PostgreSQL · xUnit · Testcontainers  

---

## Problem Statement

You are an engineer at an e-commerce company. The team needs a system to run background jobs — report exports, bulk emails, data syncs — but the current solution uses simple CronJobs that don't scale, have no retry on failure, and provide no visibility into job status.

### System Flow

```
Client sends job request
    → API receives and validates
    → Job is queued into RabbitMQ
    → Worker pool picks up and executes the job
    → Redis tracks status (pending/running/completed/failed)
    → On failure → retry with exponential backoff (max 3 attempts)
    → Client can query job status at any time
```

### API Endpoints

```
POST   /jobs          → create a new job
GET    /jobs/{id}     → get job status
GET    /jobs          → list jobs (filter by status)
DELETE /jobs/{id}     → cancel a pending job
```

### Job Types

```
EmailJob   { to, subject, body }
ExportJob  { entity, filters, format }
SyncJob    { source, destination }
```

### Constraints

- No duplicate job execution (same jobId)
- Max 5 concurrent workers
- Job timeout after 30 seconds
- Sufficient logging to debug failed jobs

---

## Step 1 — Solution Structure + Docker Compose

### Solution Structure

```
JobScheduler/
├── src/
│   ├── JobScheduler.API/            ← controllers, endpoints
│   ├── JobScheduler.Worker/         ← background worker service
│   ├── JobScheduler.Core/           ← entities, enums, interfaces
│   └── JobScheduler.Infrastructure/ ← RabbitMQ, Redis, Postgres implementations
├── tests/
│   └── JobScheduler.IntegrationTests/
├── docker-compose.yml
└── JobScheduler.sln
```

**Dependency flow:**
```
API ──────────────→ Core (interfaces) ← Infrastructure (implementations)
Worker ───────────→ Core
```

### Create Solution

```bash
mkdir JobScheduler && cd JobScheduler

dotnet new sln -n JobScheduler

dotnet new webapi -n JobScheduler.API -o src/JobScheduler.API
dotnet new worker -n JobScheduler.Worker -o src/JobScheduler.Worker
dotnet new classlib -n JobScheduler.Core -o src/JobScheduler.Core
dotnet new classlib -n JobScheduler.Infrastructure -o src/JobScheduler.Infrastructure
dotnet new xunit -n JobScheduler.IntegrationTests -o tests/JobScheduler.IntegrationTests

dotnet sln add src/JobScheduler.API
dotnet sln add src/JobScheduler.Worker
dotnet sln add src/JobScheduler.Core
dotnet sln add src/JobScheduler.Infrastructure
dotnet sln add tests/JobScheduler.IntegrationTests

dotnet add src/JobScheduler.API reference src/JobScheduler.Core
dotnet add src/JobScheduler.API reference src/JobScheduler.Infrastructure
dotnet add src/JobScheduler.Worker reference src/JobScheduler.Core
dotnet add src/JobScheduler.Worker reference src/JobScheduler.Infrastructure
dotnet add src/JobScheduler.Infrastructure reference src/JobScheduler.Core
dotnet add tests/JobScheduler.IntegrationTests reference src/JobScheduler.API
```

### docker-compose.yml

```yaml
version: '3.8'

services:
  rabbitmq:
    image: rabbitmq:3-management
    ports:
      - "5672:5672"
      - "15672:15672"
    environment:
      RABBITMQ_DEFAULT_USER: guest
      RABBITMQ_DEFAULT_PASS: guest
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "ping"]
      interval: 10s
      timeout: 5s
      retries: 5

  redis:
    image: redis:7-alpine
    ports:
      - "6380:6379"   # change to 6379:6379 if no port conflict
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 5s
      retries: 5

  postgres:
    image: postgres:15-alpine
    ports:
      - "5432:5432"
    environment:
      POSTGRES_DB: jobscheduler
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: postgres
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 10s
      timeout: 5s
      retries: 5
```

```bash
docker-compose up -d
docker-compose ps   # all services should show "healthy"
```

RabbitMQ management UI: `http://localhost:15672` (guest/guest)

---

## Step 2 — Domain Models (JobScheduler.Core)

### Structure

```
JobScheduler.Core/
├── Entities/
│   └── Job.cs
├── Enums/
│   ├── JobStatus.cs
│   └── JobType.cs
├── Interfaces/
│   ├── IJobRepository.cs
│   ├── IJobQueue.cs
│   └── IJobStatusTracker.cs
└── Models/
    └── JobPayload.cs
```

### Enums

```csharp
// Enums/JobStatus.cs
namespace JobScheduler.Core.Enums;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}
```

```csharp
// Enums/JobType.cs
namespace JobScheduler.Core.Enums;

public enum JobType
{
    Email,
    Export,
    Sync
}
```

### Job Entity

```csharp
// Entities/Job.cs
namespace JobScheduler.Core.Entities;

public class Job
{
    public Guid Id { get; private set; }
    public JobType Type { get; private set; }
    public JobStatus Status { get; private set; }
    public string Payload { get; private set; }
    public int RetryCount { get; private set; }
    public int MaxRetries { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private Job() { }

    public static Job Create(JobType type, string payload, int maxRetries = 3)
    {
        return new Job
        {
            Id = Guid.NewGuid(),
            Type = type,
            Status = JobStatus.Pending,
            Payload = payload,
            RetryCount = 0,
            MaxRetries = maxRetries,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void MarkAsRunning()
    {
        Status = JobStatus.Running;
        StartedAt = DateTime.UtcNow;
    }

    public void MarkAsCompleted()
    {
        Status = JobStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkAsFailed(string errorMessage)
    {
        ErrorMessage = errorMessage;
        RetryCount++;

        if (RetryCount >= MaxRetries)
            Status = JobStatus.Failed;
        else
            Status = JobStatus.Pending; // will be retried
    }

    public void MarkAsCancelled()
    {
        if (Status != JobStatus.Pending)
            throw new InvalidOperationException("Only pending jobs can be cancelled.");

        Status = JobStatus.Cancelled;
    }
}
```

### Job Payloads

```csharp
// Models/JobPayload.cs
namespace JobScheduler.Core.Models;

public record EmailPayload(
    string To,
    string Subject,
    string Body
);

public record ExportPayload(
    string Entity,
    string Format,       // "csv" | "excel"
    Dictionary<string, string> Filters
);

public record SyncPayload(
    string Source,
    string Destination
);
```

### Interfaces

```csharp
// Interfaces/IJobRepository.cs
namespace JobScheduler.Core.Interfaces;

public interface IJobRepository
{
    Task<Job> GetByIdAsync(Guid id);
    Task<IEnumerable<Job>> GetAllAsync(JobStatus? status = null);
    Task AddAsync(Job job);
    Task UpdateAsync(Job job);
}
```

```csharp
// Interfaces/IJobQueue.cs
namespace JobScheduler.Core.Interfaces;

public interface IJobQueue
{
    Task EnqueueAsync(Job job);
    Task<Job?> DequeueAsync(CancellationToken cancellationToken);
}
```

```csharp
// Interfaces/IJobStatusTracker.cs
namespace JobScheduler.Core.Interfaces;

public interface IJobStatusTracker
{
    Task SetStatusAsync(Guid jobId, JobStatus status);
    Task<JobStatus?> GetStatusAsync(Guid jobId);
}
```

```bash
dotnet build src/JobScheduler.Core
```

---

## Step 3 — Infrastructure (coming next)

- PostgreSQL + EF Core → implements `IJobRepository`
- Redis → implements `IJobStatusTracker`
- RabbitMQ → implements `IJobQueue`

---

## Step 4 — API Endpoints (coming next)

- `POST /jobs`
- `GET /jobs/{id}`
- `GET /jobs`
- `DELETE /jobs/{id}`

---

## Step 5 — Worker Service (coming next)

- RabbitMQ consumer
- Concurrent worker pool (max 5)
- Exponential backoff retry
- Job timeout after 30s

---

## Step 6 — Integration Tests (coming next)

- Testcontainers setup
- Test scenarios: success, fail + retry, cancel, duplicate prevention

---

## CV Bullet (use after completion)

> "Built a distributed job scheduler with RabbitMQ-based worker pool, Redis-backed deduplication, and exponential backoff retry — integration tested with Testcontainers"