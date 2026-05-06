# Distributed Job Scheduler — Project Overview

> Stack: ASP.NET Core · RabbitMQ · Redis · PostgreSQL · xUnit · Testcontainers
> Branch: `feat/step-6-integration-tests` | All 6 steps complete | 6/6 integration tests passing

---

## System Flow

```
Client
  → POST /jobs          (API validates, persists to Postgres, enqueues to RabbitMQ)
  → Worker dequeues     (max 5 concurrent, 30s timeout)
  → Worker executes     (simulates Email / Export / Sync)
  → Redis tracks status (Pending → Running → Completed / Failed)
  → On failure          (exponential backoff retry, max 3 attempts)
  → Client polls        GET /jobs/{id}
```

---

## Solution Structure

```
JobScheduler/
├── src/
│   ├── JobScheduler.Core/           ← Domain (entities, enums, interfaces)
│   ├── JobScheduler.Infrastructure/ ← Data + messaging implementations
│   ├── JobScheduler.API/            ← HTTP endpoints
│   └── JobScheduler.Worker/         ← Background job processor
├── tests/
│   └── JobScheduler.IntegrationTests/
├── docker-compose.yml               ← PostgreSQL · Redis · RabbitMQ
└── JobScheduler.sln
```

### Dependency flow

```
API ──────────────→ Core ←── Infrastructure
Worker ───────────→ Core
IntegrationTests ─→ API (via WebApplicationFactory)
```

---

## API Endpoints

| Method   | Route           | Description                        |
|----------|-----------------|------------------------------------|
| `POST`   | `/jobs`         | Create and enqueue a new job       |
| `GET`    | `/jobs/{id}`    | Get job by ID                      |
| `GET`    | `/jobs`         | List all jobs (filter: `?status=`) |
| `DELETE` | `/jobs/{id}`    | Cancel a pending job               |

### Job types

```json
EmailJob:  { "type": "Email",  "payload": { "to": "", "subject": "", "body": "" } }
ExportJob: { "type": "Export", "payload": { "entity": "", "format": "", "filters": {} } }
SyncJob:   { "type": "Sync",   "payload": { "source": "", "destination": "" } }
```

---

## Step-by-Step Breakdown

### Step 1 — Solution Structure + Docker Compose

**Branch:** `main`

**What was done:**
- Created the 5-project .NET solution (`API`, `Worker`, `Core`, `Infrastructure`, `IntegrationTests`)
- Added project references following the dependency flow above
- Configured `docker-compose.yml` with health checks for all three services

**Key files:**
- [`docker-compose.yml`](../docker-compose.yml) — RabbitMQ (5672/15672), Redis (6380), PostgreSQL (5432)
- [`JobScheduler.sln`](../JobScheduler.sln)

---

### Step 2 — Domain Models (`JobScheduler.Core`)

**Branch:** `feat/step-2`

**What was done:**
- Defined the core `Job` entity with private setters and factory method
- Implemented state transitions: `MarkAsRunning`, `MarkAsCompleted`, `MarkAsFailed`, `MarkAsCancelled`
- `MarkAsFailed` handles retry logic: increments `RetryCount`, re-sets status to `Pending` if retries remain, otherwise `Failed`
- Defined 3 typed payload records (`EmailPayload`, `ExportPayload`, `SyncPayload`)
- Defined 3 interfaces consumed by Infrastructure and API

**Key files:**

| File | Purpose |
|------|---------|
| [`Entities/Job.cs`](../src/JobScheduler.Core/Entities/Job.cs) | Aggregate root with state machine |
| [`Enums/JobStatus.cs`](../src/JobScheduler.Core/Enums/JobStatus.cs) | `Pending · Running · Completed · Failed · Cancelled` |
| [`Enums/JobType.cs`](../src/JobScheduler.Core/Enums/JobType.cs) | `Email · Export · Sync` |
| [`Interfaces/IJobRepository.cs`](../src/JobScheduler.Core/Interfaces/IJobRepository.cs) | CRUD contract for persistence |
| [`Interfaces/IJobQueue.cs`](../src/JobScheduler.Core/Interfaces/IJobQueue.cs) | Enqueue / dequeue contract |
| [`Interfaces/IJobStatusTracker.cs`](../src/JobScheduler.Core/Interfaces/IJobStatusTracker.cs) | Redis status contract |
| [`Models/JobPayload.cs`](../src/JobScheduler.Core/Models/JobPayload.cs) | Typed payload records |

---

### Step 3 — Infrastructure (`JobScheduler.Infrastructure`)

**Branch:** `feat/step-3-infrastructure`

**What was done:**
- Implemented `IJobRepository` with EF Core + Npgsql (PostgreSQL)
- Implemented `IJobStatusTracker` with StackExchange.Redis (key: `job:status:{id}`)
- Implemented `IJobQueue` with RabbitMQ.Client 7.x async API (durable queue, persistent messages, manual ack)
- `RabbitMqJobQueue` is `IAsyncDisposable` and created via async factory method to handle channel setup

**Key files:**

| File | Purpose |
|------|---------|
| [`Persistence/JobSchedulerDbContext.cs`](../src/JobScheduler.Infrastructure/Persistence/JobSchedulerDbContext.cs) | EF Core DbContext, enums stored as strings |
| [`Persistence/JobRepository.cs`](../src/JobScheduler.Infrastructure/Persistence/JobRepository.cs) | `IJobRepository` — PostgreSQL via EF Core |
| [`Messaging/RedisJobStatusTracker.cs`](../src/JobScheduler.Infrastructure/Messaging/RedisJobStatusTracker.cs) | `IJobStatusTracker` — Redis string keys |
| [`Messaging/RabbitMqJobQueue.cs`](../src/JobScheduler.Infrastructure/Messaging/RabbitMqJobQueue.cs) | `IJobQueue` — RabbitMQ durable queue |

---

### Step 4 — API Endpoints (`JobScheduler.API`)

**Branch:** `feat/step-4-api`

**What was done:**
- Implemented `JobsController` with all 4 endpoints
- `POST /jobs` — deserializes payload as `object` (type-erased), persists job, enqueues to RabbitMQ, returns `201 Created`
- `DELETE /jobs/{id}` — calls `MarkAsCancelled()`, returns `409 Conflict` if job is not pending
- Configured `JsonStringEnumConverter` so enums serialize/deserialize as strings (`"Pending"` not `0`)
- Wired all services in `Program.cs` (PostgreSQL, Redis, RabbitMQ singletons/scoped)

**Key files:**

| File | Purpose |
|------|---------|
| [`Controllers/JobsController.cs`](../src/JobScheduler.API/Controllers/JobsController.cs) | All 4 REST endpoints |
| [`Models/CreateJobRequest.cs`](../src/JobScheduler.API/Models/CreateJobRequest.cs) | `{ type, payload }` request DTO |
| [`Models/JobResponse.cs`](../src/JobScheduler.API/Models/JobResponse.cs) | Response DTO with `From(Job)` factory |
| [`Program.cs`](../src/JobScheduler.API/Program.cs) | DI wiring + JSON enum serialization |
| [`appsettings.json`](../src/JobScheduler.API/appsettings.json) | Connection strings |

---

### Step 5 — Worker Service (`JobScheduler.Worker`)

**Branch:** `feat/step-5-worker`

**What was done:**
- Implemented `Worker` as a `BackgroundService` that runs indefinitely
- Concurrency: `SemaphoreSlim(5)` caps parallel job execution at 5
- Timeout: `CancellationTokenSource.CreateLinkedTokenSource` with 30s `CancelAfter`
- Retry: `MarkAsFailed` delegates retry tracking to the `Job` entity; on re-queue, delay is `2^retryCount` seconds (exponential backoff: 2s, 4s, 8s)
- Status updates written to both PostgreSQL (persistent) and Redis (fast lookup) on each transition
- `ExecuteJobAsync` is a dispatch table over `JobType` (currently simulates with 1s delay — real implementations slot in here)

**Key files:**

| File | Purpose |
|------|---------|
| [`Worker.cs`](../src/JobScheduler.Worker/Worker.cs) | `BackgroundService`: dequeue → execute → update status |
| [`Program.cs`](../src/JobScheduler.Worker/Program.cs) | DI wiring (same services as API) |
| [`appsettings.json`](../src/JobScheduler.Worker/appsettings.json) | Connection strings |

**Worker lifecycle per job:**
```
Dequeue from RabbitMQ
  → acquire semaphore slot (max 5)
  → MarkAsRunning (Postgres + Redis)
  → ExecuteJobAsync with 30s timeout
  → success: MarkAsCompleted
  → timeout/error: MarkAsFailed
      → retries remain: re-enqueue after backoff delay
      → no retries left: status = Failed
  → release semaphore slot
```

---

### Step 6 — Integration Tests (`JobScheduler.IntegrationTests`)

**Branch:** `feat/step-6-integration-tests`

**What was done:**
- `JobsApiFactory` extends `WebApplicationFactory<Program>` and spins up real containers via Testcontainers 3.x
- All three infrastructure services (PostgreSQL, Redis, RabbitMQ) are replaced with real containerised instances
- Schema created with `EnsureCreatedAsync()` before tests run
- 6 test scenarios covering the full API surface

**Test scenarios:**

| Test | What it verifies |
|------|-----------------|
| `CreateJob_ValidRequest_ReturnsCreated` | POST returns 201 with `status: Pending` |
| `GetJob_ExistingId_ReturnsJob` | GET returns 200 with correct job ID |
| `GetJob_NonExistentId_ReturnsNotFound` | GET returns 404 for unknown ID |
| `CancelJob_PendingJob_ReturnsNoContent` | DELETE returns 204 for pending job |
| `CancelJob_NonExistentId_ReturnsNotFound` | DELETE returns 404 for unknown ID |
| `ListJobs_FilterByStatus_ReturnsMatchingJobs` | GET ?status=Pending returns only pending jobs |

**Key files:**

| File | Purpose |
|------|---------|
| [`JobsApiFactory.cs`](../tests/JobScheduler.IntegrationTests/JobsApiFactory.cs) | Testcontainers factory, DI overrides |
| [`JobsEndpoint_test.cs`](../tests/JobScheduler.IntegrationTests/JobsEndpoint_test.cs) | 6 integration tests |

**Test infrastructure:**
```
Testcontainers 3.10.0  ← Docker API 1.43 compatible (Docker Desktop 24.0.7)
xUnit 2.9.3
FluentAssertions 8.9.0
Microsoft.AspNetCore.Mvc.Testing 10.0.7
```

---

## Infrastructure Configuration

All connection strings are in each project's `appsettings.json`:

```
Postgres: Host=localhost;Port=5432;Database=jobscheduler;Username=postgres;Password=postgres
Redis:    localhost:6380
RabbitMq: amqp://guest:guest@localhost:5672
```

Run the stack locally:
```bash
docker-compose up -d
dotnet run --project src/JobScheduler.API
dotnet run --project src/JobScheduler.Worker
```

Run tests (requires Docker Desktop running):
```bash
dotnet test tests/JobScheduler.IntegrationTests
```

---

## Constraints Implemented

| Constraint | Implementation |
|-----------|---------------|
| No duplicate execution | Job ID is the primary key; duplicate `POST` creates a new job with a new GUID |
| Max 5 concurrent workers | `SemaphoreSlim(5)` in `Worker.cs` |
| 30s job timeout | Linked `CancellationTokenSource` with `CancelAfter(30s)` |
| Retry with backoff | `Job.MarkAsFailed` + exponential delay before re-enqueue |
| Status visibility | Redis for fast reads, PostgreSQL for durable state |
| Sufficient logging | `ILogger` at every state transition in `Worker.cs` |
