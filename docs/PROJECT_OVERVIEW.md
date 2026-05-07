# Distributed Job Scheduler — Project Overview

> Stack: ASP.NET Core · PostgreSQL · xUnit · Testcontainers
> Branch: `feat/step-7-remove-rabbitmq` | 4/4 integration tests passing

---

## System Flow

```
Client
  → POST /jobs               (API validates, persists to PostgreSQL)
  → Worker polls DB          (every 5s, SKIP LOCKED, max 10 per batch)
  → Channel<Job> buffer      (bounded capacity 20, backpressure)
  → Worker executes          (max 5 concurrent, 30s timeout)
  → On failure               (exponential backoff retry: 2s → 4s → 8s, max 3 attempts)
  → Client polls             GET /jobs/{id}
  → JobMaintenanceService    (cleanup at 4 AM daily, stuck job recovery every 5 min)
```

---

## Solution Structure

```
JobScheduler/
├── src/
│   ├── JobScheduler.Core/           ← Domain (entities, enums, interfaces)
│   ├── JobScheduler.Infrastructure/ ← PostgreSQL implementation
│   ├── JobScheduler.API/            ← HTTP endpoints
│   └── JobScheduler.Worker/         ← Background job processor + maintenance
├── tests/
│   └── JobScheduler.IntegrationTests/
├── docker-compose.yml               ← PostgreSQL only
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

| Method | Route        | Description                        |
|--------|--------------|------------------------------------|
| `POST` | `/jobs`      | Create a new job                   |
| `GET`  | `/jobs/{id}` | Get job by ID                      |
| `GET`  | `/jobs`      | List all jobs (filter: `?status=`) |

### Job types

```json
EmailJob:  { "type": "Email",  "payload": { "to": "", "subject": "", "body": "" } }
ExportJob: { "type": "Export", "payload": { "entity": "", "format": "", "filters": {} } }
SyncJob:   { "type": "Sync",   "payload": { "source": "", "destination": "" } }
```

---

## Step-by-Step Breakdown

### Step 1 — Solution Structure + Docker Compose

**What was done:**
- Created the 5-project .NET solution (`API`, `Worker`, `Core`, `Infrastructure`, `IntegrationTests`)
- Added project references following the dependency flow above
- Configured `docker-compose.yml`

**Key files:**
- [`docker-compose.yml`](../docker-compose.yml) — PostgreSQL (5432)
- [`JobScheduler.sln`](../JobScheduler.sln)

---

### Step 2 — Domain Models (`JobScheduler.Core`)

**What was done:**
- Defined the core `Job` entity with private setters and factory method
- Implemented state transitions: `MarkAsRunning`, `MarkAsCompleted`, `MarkAsFailed`, `MarkAsCancelled`
- `MarkAsFailed` handles retry logic: increments `RetryCount`, re-sets status to `Pending` if retries remain, otherwise `Failed`
- All state transitions set `UpdatedAt = UtcNow`
- Defined 3 typed payload records (`EmailPayload`, `ExportPayload`, `SyncPayload`)
- Defined interfaces consumed by Infrastructure and API

**Key files:**

| File | Purpose |
|------|---------|
| [`Entities/Job.cs`](../src/JobScheduler.Core/Entities/Job.cs) | Aggregate root with state machine |
| [`Enums/JobStatus.cs`](../src/JobScheduler.Core/Enums/JobStatus.cs) | `Pending · Running · Completed · Failed · Cancelled` |
| [`Enums/JobType.cs`](../src/JobScheduler.Core/Enums/JobType.cs) | `Email · Export · Sync` |
| [`Interfaces/IJobRepository.cs`](../src/JobScheduler.Core/Interfaces/IJobRepository.cs) | Persistence contract |
| [`Models/JobPayload.cs`](../src/JobScheduler.Core/Models/JobPayload.cs) | Typed payload records |

---

### Step 3 — Infrastructure (`JobScheduler.Infrastructure`)

**What was done:**
- Implemented `IJobRepository` with EF Core + Npgsql (PostgreSQL)
- `GetPendingJobsAsync` uses raw SQL with `FOR UPDATE SKIP LOCKED` — safe for multiple Worker instances
- `DeleteOldJobsAsync` and `ResetStuckJobsAsync` use EF Core bulk operations (`ExecuteDeleteAsync`, `ExecuteUpdateAsync`)

**Key files:**

| File | Purpose |
|------|---------|
| [`Persistence/JobSchedulerDbContext.cs`](../src/JobScheduler.Infrastructure/Persistence/JobSchedulerDbContext.cs) | EF Core DbContext, enums stored as strings |
| [`Persistence/JobRepository.cs`](../src/JobScheduler.Infrastructure/Persistence/JobRepository.cs) | `IJobRepository` — PostgreSQL via EF Core |

---

### Step 4 — API Endpoints (`JobScheduler.API`)

**What was done:**
- Implemented `JobsController` with 3 endpoints
- `POST /jobs` — persists job to PostgreSQL, returns `201 Created` (no queue, Worker polls DB)
- Configured `JsonStringEnumConverter` so enums serialize as strings (`"Pending"` not `0`)

**Key files:**

| File | Purpose |
|------|---------|
| [`Controllers/JobsController.cs`](../src/JobScheduler.API/Controllers/JobsController.cs) | 3 REST endpoints |
| [`Models/CreateJobRequest.cs`](../src/JobScheduler.API/Models/CreateJobRequest.cs) | `{ type, payload }` request DTO |
| [`Models/JobResponse.cs`](../src/JobScheduler.API/Models/JobResponse.cs) | Response DTO with `From(Job)` factory |
| [`Program.cs`](../src/JobScheduler.API/Program.cs) | DI wiring + JSON enum serialization |

---

### Step 5 — Worker Service (`JobScheduler.Worker`)

**What was done:**
- `Worker` — `BackgroundService` with producer/consumer pattern
- `ProduceAsync` — polls DB every 5s with `GetPendingJobsAsync(10)`, writes to `Channel<Job>`
- `ConsumeAsync` — reads from channel, caps at 5 concurrent via `SemaphoreSlim`
- `ConcurrentDictionary<Guid, bool>` tracks in-flight jobs to prevent double-processing
- `Channel.CreateBounded(20)` with `FullMode = Wait` — producer blocks when consumer is busy (backpressure)
- Retry: exponential backoff `2^RetryCount` seconds (2s → 4s → 8s), max 3 attempts
- `JobMaintenanceService` — second `BackgroundService` running two independent loops:
  - **CleanOldJob**: runs at 4 AM UTC daily, deletes jobs with `UpdatedAt < now - 1 day` (Completed/Failed/Cancelled)
  - **StuckJobRecovery**: runs every 5 minutes, resets jobs `Running` for > 10 minutes back to `Pending`

**Key files:**

| File | Purpose |
|------|---------|
| [`Worker.cs`](../src/JobScheduler.Worker/Worker.cs) | Producer/consumer DB polling, concurrency, timeout, retry |
| [`JobMaintenanceService.cs`](../src/JobScheduler.Worker/JobMaintenanceService.cs) | Cleanup at 4 AM + stuck job recovery |
| [`Program.cs`](../src/JobScheduler.Worker/Program.cs) | DI wiring |

**Worker lifecycle per job:**
```
ProduceAsync polls DB (SKIP LOCKED)
  → skip if already in _inFlight
  → write to Channel (blocks if full — backpressure)

ConsumeAsync reads from Channel
  → acquire SemaphoreSlim (max 5)
  → Task.Run ProcessJobAsync
      → MarkAsRunning → UpdateAsync
      → ExecuteJobAsync (30s timeout)
      → success: MarkAsCompleted → UpdateAsync
      → timeout/error: MarkAsFailed → UpdateAsync
          → retries remain: delay 2^n seconds (status stays Pending → picked up next poll)
          → no retries: status = Failed
  → finally: _inFlight.Remove + semaphore.Release
```

---

### Step 6 — Integration Tests (`JobScheduler.IntegrationTests`)

**What was done:**
- `JobsApiFactory` extends `WebApplicationFactory<Program>`, spins up PostgreSQL via Testcontainers
- Schema created with `EnsureCreatedAsync()` before tests run
- 4 test scenarios covering the full API surface

**Test scenarios:**

| Test | What it verifies |
|------|-----------------|
| `CreateJob_ValidRequest_ReturnsCreated` | POST returns 201 with `status: Pending` |
| `GetJob_ExistingId_ReturnsJob` | GET returns 200 with correct job ID |
| `GetJob_NonExistentId_ReturnsNotFound` | GET returns 404 for unknown ID |
| `ListJobs_FilterByStatus_ReturnsMatchingJobs` | GET ?status=Pending returns only pending jobs |

**Key files:**

| File | Purpose |
|------|---------|
| [`JobsApiFactory.cs`](../tests/JobScheduler.IntegrationTests/JobsApiFactory.cs) | Testcontainers PostgreSQL factory |
| [`JobsEndpoint_test.cs`](../tests/JobScheduler.IntegrationTests/JobsEndpoint_test.cs) | 4 integration tests |

---

### Step 7 — Worker Improvements + Remove RabbitMQ/Redis

**Branch:** `feat/step-7-remove-rabbitmq`

**What was done:**
- Removed RabbitMQ entirely — switched to DB polling with `SKIP LOCKED`
- Removed Redis entirely — status tracked only in PostgreSQL
- Replaced `Task.Run` with `Channel<T>` producer/consumer for backpressure
- Added `ConcurrentDictionary` in-flight tracking against double-processing
- Added `JobMaintenanceService` (4 AM cleanup + 5 min stuck recovery)
- Removed `DELETE /jobs/{id}` endpoint — cleanup handled automatically
- Added `UpdatedAt` to `Job` entity, set on every state transition
- Added Husky pre-commit hooks — CSharpier + dotnet-format on staged `.cs` files

---

## Infrastructure Configuration

```
Postgres: Host=localhost;Port=5432;Database=jobscheduler;Username=postgres;Password=postgres
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

First-time setup (git hooks):
```bash
dotnet tool restore
dotnet husky install
```

---

## Constraints Implemented

| Constraint | Implementation |
|-----------|---------------|
| No duplicate execution (single instance) | `ConcurrentDictionary` in-flight tracking in `Worker.cs` |
| No duplicate execution (multi-instance) | `FOR UPDATE SKIP LOCKED` in `GetPendingJobsAsync` |
| Max 5 concurrent workers | `SemaphoreSlim(5)` in `ConsumeAsync` |
| Backpressure | `Channel.CreateBounded(20)` with `FullMode = Wait` |
| 30s job timeout | Linked `CancellationTokenSource` with `CancelAfter(30s)` |
| Retry with backoff | `Job.MarkAsFailed` + `2^RetryCount` delay, max 3 attempts |
| Stuck job recovery | `JobMaintenanceService` resets `Running` jobs > 10 min every 5 min |
| DB cleanup | `JobMaintenanceService` deletes terminal jobs at 4 AM daily |
| Sufficient logging | `ILogger` at every state transition in `Worker.cs` |
