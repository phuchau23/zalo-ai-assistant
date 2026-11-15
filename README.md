# Remi

Trợ lý AI chăm sóc khách hàng trên Zalo, kèm CRM cho doanh nghiệp dịch vụ (spa, nha khoa, sửa chữa nhà, bán lẻ...).

Bot trả lời khách 24/7 dựa trên dữ liệu riêng mà doanh nghiệp nạp vào, chủ động nhắc lịch và hỏi thăm khách. Khi cần thì bot
chuyển cho nhân viên. Chủ doanh nghiệp quản lý hội thoại, lịch hẹn, bán hàng và báo cáo trên trang quản trị web.

## Tính năng chính

- Trả lời khách tự động theo dữ liệu của doanh nghiệp, không chắc thì chuyển nhân viên
- Hộp thư realtime, nhân viên tiếp quản hoặc trả lại cho bot
- Chăm sóc chủ động: nhắc lịch, hỏi thăm sau buổi, mời khách cũ quay lại
- CRM: lịch hẹn, bán hàng, gói liệu trình, kho, nhân viên, báo cáo
- Kênh: Zalo OA, Zalo cá nhân, khung chat thử
- Nhiều doanh nghiệp dùng chung hệ thống, dữ liệu tách riêng

## Công nghệ

Backend .NET 10, PostgreSQL, Redis, Hangfire. Trang quản trị Next.js ở repo riêng `zalo-ai-portal`.

## Chạy local

Cần .NET 10 SDK và Docker Desktop.

```
docker compose up -d
dotnet build
dotnet test
dotnet run --project src/ZaloAi.Api
dotnet run --project src/ZaloAi.Worker
```

Trên Windows có thể chạy `dev` để bật tất cả, `stop` để tắt. Biến môi trường xem trong `.env.example`.
