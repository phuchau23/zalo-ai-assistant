# Vận hành

Ghi chú vận hành cho chủ dự án: báo lỗi, backup, CI. Cập nhật khi deploy staging/production (tuần 7–8).

## 1. Báo lỗi (Sentry)

- Project Sentry: `zaloai-backend`, vùng dữ liệu **EU** (Đức). Api và Worker gửi lỗi về cùng project, phân biệt bằng tag `environment` (development, staging, production).
- Bật bằng `Sentry:Dsn` (env `Sentry__Dsn`, dev dùng `dotnet user-secrets`). Để trống = tắt.
- **Gửi gì:** log mức Error trở lên (lỗi không xử lý được, job hết lượt thử lại...), kèm vài log Information ngay trước đó làm dấu vết.
- **Không gửi:** IP, cookie, header, body, query string của request; SĐT và email trong message bị thay bằng `***` (`SentryScrubber`).
- **Thử:** đăng nhập rồi gọi `POST /api/dev/errors/test` (chỉ có ở môi trường dev) → trên Sentry có lỗi "Lỗi thử Sentry, SĐT giả *** ...".
- **Khi nhận email lỗi:** mở issue trên Sentry → xem stack trace + breadcrumbs → sửa → bấm Resolve. Lỗi quay lại sau khi Resolve, Sentry báo lại.
- Gói Free: khoảng 5.000 lỗi/tháng. Một lỗi lặp lại nhiều lần vẫn tính từng lần → sửa lỗi lặp sớm.
- **Pháp lý:** dữ liệu lỗi gửi ra nước ngoài (EU). Đưa Sentry vào hồ sơ chuyển dữ liệu cá nhân ra nước ngoài và danh sách bên xử lý dữ liệu khi làm với luật sư.

## 2. Backup database

### Dev (máy chủ dự án)
```
scripts\backup-db.cmd                                   # → backups\zaloai-YYYYMMDD-HHMMSS.dump, giữ 14 bản mới nhất
scripts\restore-db.cmd backups\zaloai-....dump          # GHI ĐÈ database dev, hỏi xác nhận; tắt API/Worker trước
```
Backup gồm cả bảng Hangfire (job đang chờ). File backup chứa dữ liệu khách: không gửi cho ai, không đưa lên git.

### Production (làm ở tuần 7–8)
- Dùng backup tự động của nhà cung cấp (Railway/VPS) **và** một bản `pg_dump` hằng ngày đẩy sang nơi lưu trữ khác (object storage khác nhà cung cấp), giữ ≥ 14 ngày.
- **Mỗi tháng thử khôi phục** vào một database tạm và kiểm tra số dòng các bảng chính. Backup chưa từng thử khôi phục thì coi như chưa có.
- **Khóa mã hóa** (`Security__EncryptionKey`) cất riêng trong trình quản lý mật khẩu. Mất khóa = token Zalo, SĐT, nội dung tin trong backup không đọc được nữa. Lộ khóa + lộ backup = lộ hết.

## 3. CI (GitHub Actions)

- BE `.github/workflows/ci.yml`: `dotnet format --verify-no-changes`, build Release, test (có test tích hợp chạy Postgres thật bằng Testcontainers).
- FE `.github/workflows/ci.yml`: `pnpm lint`, `typecheck`, `test`, `build`.
- Chạy khi push lên `main`/`dev` và khi mở/cập nhật PR. PR có dấu ❌ thì xem log ở tab **Actions**, sửa rồi mới merge.
- Nên bật trên GitHub: **Settings → Branches → Add branch protection rule** cho `dev` và `main`: "Require status checks to pass before merging" → chọn job `build-test` (BE) / `check` (FE).
