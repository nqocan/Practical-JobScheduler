# Knowledge Notes — Job Scheduler Concepts

Tổng hợp các khái niệm và câu hỏi kỹ thuật trong quá trình xây dựng project.

---

## 1. Tại sao dùng GUID thay vì auto-increment ID?

**3 lý do chính:**

**Distributed safety** — Job ID được tạo ngay trong API trước khi INSERT vào DB, dùng để enqueue RabbitMQ ngay lập tức. Với auto-increment phải INSERT trước, lấy ID sau rồi mới enqueue — thứ tự bắt buộc và không linh hoạt.

**Không lộ thông tin** — `GET /jobs/1`, `GET /jobs/2`... ai cũng có thể enumerate toàn bộ jobs. GUID ngăn điều này.

**Merge-friendly** — Nhiều instance hoặc nhiều môi trường không bị collision ID.

**Tradeoff:** GUID có index performance kém hơn integer (lớn hơn, random → index fragmentation). Giải pháp nếu cần tối ưu: dùng **UUID v7** hoặc **ULID** — time-ordered, giữ được distributed benefits nhưng index locality tốt hơn.

---

## 2. DB Polling vs RabbitMQ — So sánh

### DB Polling (cách Haravan làm)

```
Worker: mỗi N giây → SELECT * FROM Jobs WHERE Status = 'Pending' → xử lý
```

**Ưu điểm:**
- Không cần thêm infrastructure
- Không có Outbox Problem (chỉ có 1 write operation: INSERT)
- Đơn giản, dễ debug (`SELECT * FROM Jobs` là thấy hết)

**Nhược điểm:**
- Polling delay (job tạo lúc T, Worker thấy lúc T+5s)
- DB bị query liên tục dù không có jobs
- Multi-instance cần `SELECT FOR UPDATE SKIP LOCKED` để tránh 2 workers lấy cùng job

### RabbitMQ

**Ưu điểm:**
- Push ngay lập tức (< 10ms delay)
- Multi-instance tự động safe — RabbitMQ distribute, mỗi message chỉ đến 1 Worker
- Job không mất khi Worker crash (message chỉ bị xóa sau `BasicAck`)
- Scale Worker độc lập với API

**Nhược điểm:**
- Thêm 1 service phải maintain
- **At-least-once delivery** — có thể xử lý cùng 1 job 2 lần (cần idempotency)
- **Outbox Problem** — INSERT DB và Publish RabbitMQ không nằm trong cùng transaction
- Consumer timeout 30 phút — không phù hợp với long-running jobs
- Khó debug hơn DB (phải vào Management UI)

---

## 3. Outbox Problem

**Vấn đề:**
```csharp
await repository.AddAsync(job);   // INSERT PostgreSQL ✅
await queue.EnqueueAsync(job);    // Publish RabbitMQ — nếu crash ở đây?
```
Job có trong DB nhưng không có trong queue → không bao giờ được xử lý, mãi là `Pending`.

**3 cách giải quyết:**

**Approach 1 — Transactional Outbox (chuẩn production):**
```
INSERT Jobs + INSERT OutboxMessages  ← cùng 1 transaction
OutboxRelayService → đọc OutboxMessages → publish RabbitMQ → DELETE
```
Đảm bảo 100%, nhưng thêm bảng và thêm service.

**Approach 2 — Polling Fallback (đơn giản):**
Mở rộng StuckJobRecovery: job `Pending` quá 5 phút mà không được xử lý → re-enqueue. Có thể duplicate nhưng in-flight guard xử lý được.

**Approach 3 — Bỏ RabbitMQ, dùng DB polling:**
API chỉ `INSERT Jobs`. Worker tự poll DB. Không có Outbox Problem vì chỉ có 1 write operation. Đây là cách Haravan làm.

---

## 4. Khi nào nên dùng RabbitMQ?

Nên dùng khi có **ít nhất 2-3** trong số này:

**Jobs ngắn, volume cao (hàng nghìn/giây)**
DB polling có lag tối đa = poll interval. Với 10,000 jobs/giây, tăng tần suất poll → hammered DB. RabbitMQ push ngay lập tức, không tốn DB query.

**Nhiều service khác nhau cần publish jobs**
```
OrderService ──┐
InventoryService ──┼──▶ RabbitMQ ──▶ Worker
NotificationService ──┘
```
Nếu dùng DB polling, 3 service phải cùng connect vào 1 DB → coupling chặt. Với RabbitMQ, mỗi service chỉ cần biết publish message, không cần biết DB schema của Worker.

**Cần scale Worker độc lập với API**
```
Black Friday: API ×1, Worker ×10
```
Spin up thêm Worker → tự nhận jobs từ queue, không cần config gì thêm. DB polling cần `FOR UPDATE SKIP LOCKED` — tức là tự build lại message queue.

**Cần routing phức tạp**
- Different queues: `jobs.high-priority` → 10 Workers, `jobs.low-priority` → 2 Workers
- Dead letter queue: job fail 3 lần → tự chuyển sang queue riêng để review
- Priority queue: job VIP chạy trước job thường — built-in trong RabbitMQ

**Tóm lại:** RabbitMQ giải quyết tốt bài toán *"nhiều nguồn tạo jobs, nhiều worker xử lý, cần phân loại và phân phối thông minh"*. Còn nếu chỉ có 1 nguồn, 1 loại job, 1 worker — DB polling đơn giản hơn và không thua kém gì.

---

## 5. Long-running Jobs — Không phù hợp với RabbitMQ

RabbitMQ có **default consumer timeout là 30 phút**. Jobs chạy lâu (export 1M records ~30 phút) sẽ bị disconnect giữa chừng.

**Giải pháp đúng cho long-running jobs:** DB polling + `FOR UPDATE SKIP LOCKED`.

Pattern này được dùng bởi: Haravan, Hangfire (.NET), GoodJob (Ruby), Sidekiq Pro.

---

## 6. Backpressure — Channel vs Task.Run

**Vấn đề với Task.Run:**
```
DequeueAsync → Task.Run(ProcessJobAsync)
```
Nếu dequeue nhanh hơn xử lý, Worker tiếp tục dequeue không giới hạn → memory tăng dần.

**Channel giải quyết:**
```csharp
Channel.CreateBounded<Job>(capacity: 10)
// Producer blocks khi channel đầy
// Consumer xử lý với concurrency control
```
Khi 10 slot đầy, producer tự chờ — không có unbounded accumulation. Đây là pattern mà Haravan dùng với `BufferBlock<T>` (TPL Dataflow).

---

## 7. Scale API vs Worker

API và Worker là **2 process độc lập** → có thể scale riêng biệt:

```
API ×2 + Worker ×1  → hoàn toàn ok
API ×1 + Worker ×2  → ok với RabbitMQ, cần distributed lock để an toàn tuyệt đối
```

**Vấn đề khi Worker ×2:**
`ConcurrentDictionary` chỉ bảo vệ trong 1 process. Nếu RabbitMQ redeliver message, 2 Worker instances đều nhận → xử lý trùng. Fix thật sự cần **Redis distributed lock**.

Với learning project: 1 Worker instance là đủ và an toàn. Multi-instance Worker là bài toán production thật.

---

## 8. DB Polling với Multi-instance — SKIP LOCKED

Nếu dùng DB polling với nhiều Worker instances:

```sql
SELECT * FROM Jobs
WHERE Status = 'Pending'
ORDER BY CreatedAt
LIMIT 10
FOR UPDATE SKIP LOCKED   ← PostgreSQL/MySQL feature
```

`SKIP LOCKED` bỏ qua các rows đang bị lock bởi transaction khác → 2 Workers không bao giờ lấy cùng 1 job. Đây là cách Hangfire và Haravan giải quyết multi-instance safety mà không cần message queue.

---

## 9. Bài học từ Haravan (omni-workers)

So sánh với `JobScheduler` project:

| | Haravan | JobScheduler |
|--|---------|-------------|
| Queue mechanism | DB polling (MySQL) | RabbitMQ |
| Backpressure | BufferBlock (TPL Dataflow) | Task.Run (không có) |
| In-flight tracking | ConcurrentDictionary | Không có |
| Retry | Không có | Exponential backoff |
| Job timeout | Không có | 30 giây |
| Cleanup jobs cũ | Có (CleanOldJob service) | Không có |
| Stuck job recovery | Không có | Không có |
| Multi-instance | SemaphoreSlim(1,1) — chỉ 1 instance | RabbitMQ handles it |
| Outbox Problem | Không có (DB only) | Có |

**Điểm Haravan làm tốt hơn:** BufferBlock backpressure, ConcurrentDictionary in-flight, CleanOldJob.

**Điểm JobScheduler làm tốt hơn:** Retry tự động, job timeout, không mất job khi crash.
