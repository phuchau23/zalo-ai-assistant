# zalo-ai-assistant
Transform your customer service on Zalo! Our AI assistant works 24/7 to answer inquiries, send timely appointment reminders, follow up after purchases, and reactivate past customers—automatically. Save costs, boost sales, and keep your customers coming back. Try it today!

## Chạy local (dev)

Cần: .NET 10 SDK, Docker Desktop (đang chạy).

```
docker compose up -d                        # Postgres (pgvector) cổng 5432, Redis cổng 6379
dotnet build
dotnet test
dotnet run --project src/ZaloAi.Api         # http://localhost:4000/health
dotnet run --project src/ZaloAi.Worker
docker compose down                         # tắt (dữ liệu vẫn giữ trong volume)
```

Tài liệu dự án: `CLAUDE.md`, `docs/`.
