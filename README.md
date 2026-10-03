# zalo-ai-assistant
Transform your customer service on Zalo! Our AI assistant works 24/7 to answer inquiries, send timely appointment reminders, follow up after purchases, and reactivate past customers—automatically. Save costs, boost sales, and keep your customers coming back. Try it today!

## Chạy local (dev)

Cần: .NET 10 SDK, Docker Desktop (đang chạy).

**Cách nhanh (Windows):** chạy `dev.cmd` ở thư mục gốc repo (gõ `dev` trong terminal hoặc bấm đúp). Script bật Postgres + Redis, build rồi mở 3 cửa sổ: API (cổng 4000), Worker, FE `../zalo-ai-portal` (cổng 3000). Tắt: chạy `stop` (tắt API, Worker, FE) hoặc `stop all` (tắt thêm Postgres/Redis, dữ liệu vẫn giữ).

Chạy từng phần bằng tay:

```
docker compose up -d                        # Postgres (pgvector) cổng 5432, Redis cổng 6379
dotnet build
dotnet test
dotnet run --project src/ZaloAi.Api         # http://localhost:4000/health
dotnet run --project src/ZaloAi.Worker
docker compose down                         # tắt (dữ liệu vẫn giữ trong volume)
```

Backup database dev: `scriptsbackup-db.cmd`, khôi phục: `scriptsestore-db.cmd <file>` (xem `docs/OPERATIONS.md`).

Tài liệu dự án: `CLAUDE.md`, `docs/`.
