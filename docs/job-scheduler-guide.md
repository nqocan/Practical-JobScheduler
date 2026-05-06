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

------------

## liệu có drawback nào ở cách tiếp cận này, thay vì dùng rabitmq thì tôi dùng cronjob xong đó đọc query trực tiếp xuống db với status rồi xử lí được không

Được, nhưng có những drawback đáng kể so với RabbitMQ:

Cách bạn đề xuất (Polling DB)

Worker: mỗi N giây → SELECT * FROM Jobs WHERE Status = 'Pending' → xử lý
Vấn đề:

1. Race condition với multiple workers
Nếu chạy 2 instance Worker, cả 2 cùng query ra job abc-123 ở cùng thời điểm → cùng xử lý 1 job 2 lần. Cần thêm SELECT FOR UPDATE SKIP LOCKED (PostgreSQL-specific) để fix — tức là bạn tự build lại một phần của message queue.

2. Polling delay
Job tạo lúc T, Worker chạy lúc T + interval. Interval càng ngắn → càng tốn DB query. Interval càng dài → job xử lý càng trễ. RabbitMQ push ngay lập tức, không có tradeoff này.

3. DB trở thành bottleneck
Hàng nghìn jobs + Worker polling mỗi giây = load DB không cần thiết. RabbitMQ sinh ra để làm việc này, DB thì không.

4. Mất job khi Worker crash
Với RabbitMQ: message chưa BasicAck sẽ tự động re-queue khi Worker disconnect. Với polling: bạn phải tự handle — update status Running rồi crash → job bị kẹt ở Running mãi, cần thêm logic "stuck job recovery".

Khi nào polling DB hợp lý?
Project nhỏ, không có RabbitMQ, muốn đơn giản
Job volume thấp (vài chục/ngày)
Chỉ có 1 Worker instance duy nhất
Ví dụ thực tế: Hangfire làm đúng kiểu này — polling PostgreSQL với SELECT FOR UPDATE SKIP LOCKED — và hoạt động tốt ở quy mô vừa.

Kết luận
RabbitMQ	Polling DB
Push ngay lập tức	✅	❌
Multi-worker an toàn	✅	⚠️ cần SKIP LOCKED
Tự recover khi crash	✅	❌ cần tự xử lý
Độ phức tạp setup	Cao hơn	Thấp hơn
Thêm infrastructure	RabbitMQ	Không cần
Nếu bạn muốn đơn giản hóa mà vẫn production-ready, dùng Hangfire + PostgreSQL — nó giải quyết hết các vấn đề trên mà không cần RabbitMQ.

#### Vậy có draw back nào khi dùng rabbitmq không

Có, RabbitMQ có những drawback thực tế:

1. Thêm infrastructure phải maintain
Thêm 1 service cần:

Monitor uptime
Handle khi RabbitMQ restart/crash
Setup clustering nếu muốn high availability
Quản lý disk (message tích tụ nếu Worker chết)
Với polling DB, bạn chỉ cần thứ đã có sẵn.

2. Message có thể bị xử lý 2 lần (At-least-once delivery)
RabbitMQ đảm bảo message không bị mất, nhưng không đảm bảo chỉ xử lý đúng 1 lần.

Scenario thực tế:


Worker nhận job → xử lý xong → crash trước khi BasicAck
→ RabbitMQ re-queue → Worker khác nhận lại → xử lý lần 2
Hệ thống hiện tại chưa có idempotency check — gửi email 2 lần là vấn đề thật.

3. Khó debug và quan sát
Khi job bị stuck hay mất:

Với DB: SELECT * FROM Jobs → thấy ngay
Với RabbitMQ: phải vào Management UI (:15672), check queue depth, check unacked messages, check dead letter queue
Và nếu message bị malformed (deserialize lỗi), nó có thể nằm im trong dead letter queue mà không ai biết.

4. Ordering không được đảm bảo hoàn toàn
Với multiple consumers, job tạo trước chưa chắc xử lý trước. Trong hầu hết trường hợp không quan trọng, nhưng nếu business logic phụ thuộc thứ tự thì là vấn đề.

5. Mất đồng bộ giữa RabbitMQ và DB
Trong code hiện tại:


await repository.AddAsync(job);   // INSERT PostgreSQL
await queue.EnqueueAsync(job);    // Publish RabbitMQ
Nếu dòng 2 fail → job có trong DB nhưng không có trong queue → không bao giờ được xử lý, status mãi là Pending. Không có transaction bao quanh cả 2.

Fix đúng là Outbox Pattern — lưu message vào DB trong cùng transaction, rồi một process khác mới relay sang RabbitMQ.

Tóm lại
Drawback	Mức độ nghiêm trọng
Thêm infrastructure	Trung bình
At-least-once delivery	Cao — cần idempotency
Khó debug	Trung bình
DB + Queue không sync	Cao — cần Outbox Pattern
Ordering	Thấp (thường không quan trọng)
Drawback lớn nhất trong project này là không có Outbox Pattern và chưa handle duplicate execution. Đây là 2 thứ production system thực tế phải có.

Haravan (DB Polling) vs JobScheduler (RabbitMQ)
Haravan làm tốt hơn
1. TPL Dataflow BufferBlock thay vì Task.Run trực tiếp


Haravan:     QueueTask → BufferBlock → ProcessTask (backpressure tự nhiên)
JobScheduler: DequeueAsync → Task.Run (không có backpressure)
BufferBlock tốt hơn vì nếu processing chậm hơn dequeue, queue tự động chậm lại — JobScheduler hiện tại có thể dequeue nhanh hơn xử lý.

2. ConcurrentDictionary track in-flight jobs
Haravan biết chính xác job nào đang chạy → tránh pick lại job đang xử lý. JobScheduler không có cơ chế này.

3. CleanOldJob — dọn dẹp định kỳ
Haravan có job tự xóa records cũ. JobScheduler không có → bảng Jobs sẽ phình to mãi.

JobScheduler làm tốt hơn
1. Retry với exponential backoff
Haravan: fail → Status = Fail, xong. Không tự retry.
JobScheduler: fail → re-enqueue với delay 2s/4s/8s.

2. Job timeout
Haravan không có timeout trên job execution — một job bị stuck có thể chiếm slot mãi mãi.
JobScheduler: CancelAfter(30s).

3. Không mất job khi Worker crash
RabbitMQ giữ message cho đến khi BasicAck — Worker crash thì message được re-queue tự động.
Haravan polling DB: nếu Worker crash giữa chừng, job kẹt ở Status = Processing mãi.

Điểm yếu chung của cả 2
Vấn đề	Haravan	JobScheduler
Outbox Pattern	❌	❌
Idempotency (tránh xử lý 2 lần)	❌	❌
Real-time progress tracking	❌	❌
Distributed lock (multi-instance)	❌ in-memory only	✅ RabbitMQ handles it
Stuck job recovery	❌	❌
Nếu muốn tối ưu JobScheduler từ bài học của Haravan
1. Thêm BufferBlock hoặc Channel<T> thay Task.Run


// thay vì
_ = Task.Run(async () => await ProcessJobAsync(job));

// dùng
var channel = Channel.CreateBounded<Job>(capacity: 10);
// producer ghi vào channel, consumer đọc với concurrency control
2. Thêm CleanOldJob background service


// xóa job completed/failed quá 7 ngày
DELETE FROM Jobs WHERE Status IN ('Completed','Failed') AND CreatedAt < NOW() - INTERVAL '7 days'
3. Thêm stuck job recovery


// jobs nào Running quá 5 phút mà không complete → reset về Pending
UPDATE Jobs SET Status = 'Pending' WHERE Status = 'Running' AND StartedAt < NOW() - INTERVAL '5 minutes'
Kết luận
Haravan phù hợp hơn cho long-running jobs (export hàng nghìn dòng, không cần phản hồi nhanh). JobScheduler phù hợp hơn cho short jobs với retry requirement. Nếu merge ưu điểm của cả 2 thì lý tưởng nhất.