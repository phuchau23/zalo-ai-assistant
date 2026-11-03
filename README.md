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

## Kết nối Zalo OA khi chạy local (M4)

Zalo phải gọi được vào máy dev qua **https công khai** (callback OAuth + webhook). Dùng Cloudflare Tunnel với tên miền dự án
(DNS của `haulp.io.vn` quản lý ở Cloudflare) để có địa chỉ cố định, khai báo trên Zalo một lần.

1. Cài `cloudflared` (Windows): `winget install --id Cloudflare.cloudflared`
2. Đăng nhập + tạo tunnel (một lần):
   ```
   cloudflared tunnel login
   cloudflared tunnel create zaloai-dev
   cloudflared tunnel route dns zaloai-dev dev-api.haulp.io.vn
   ```
3. Chạy tunnel mỗi khi test Zalo (trỏ về API cổng 4000):
   ```
   cloudflared tunnel run --url http://localhost:4000 zaloai-dev
   ```
4. Trên developers.zalo.me → app → **Official Account → Thiết lập chung**: Callback URL = `https://dev-api.haulp.io.vn/connect/zalo/callback`.
   **Webhook**: URL = `https://dev-api.haulp.io.vn/webhooks/zalo`, tắt "Lọc cú pháp", bật Webhook Retry, bật các sự kiện
   `user_send_*` và `oa_send_*`. Lấy **OA Secret Key** trên trang này.
5. Lưu cấu hình (một lần, dùng chung cho Api và Worker):
   ```
   dotnet user-secrets set "Zalo:AppId" "<App ID>" --project src/ZaloAi.Api
   dotnet user-secrets set "Zalo:AppSecret" "<Khóa bí mật của ứng dụng>" --project src/ZaloAi.Api
   dotnet user-secrets set "Zalo:WebhookSecret" "<OA Secret Key>" --project src/ZaloAi.Api
   dotnet user-secrets set "Zalo:OAuthRedirectUrl" "https://dev-api.haulp.io.vn/connect/zalo/callback" --project src/ZaloAi.Api
   ```
6. Chạy `dev`, vào trang **Kết nối kênh** → "Kết nối Zalo OA" → đồng ý trên Zalo → nhắn thử từ Zalo cá nhân vào OA.

Ghi chú API Zalo đã kiểm chứng: `docs/zalo-api-notes.md`.
