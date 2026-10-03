---
name: tenant-safe-feature
description: Quy trình thêm bảng, endpoint, job hoặc màn hình admin đụng tới dữ liệu khách thuê. Dùng mỗi khi thêm hoặc sửa bất kỳ tính năng nào đọc/ghi dữ liệu của doanh nghiệp, kể cả khi task không nhắc tới "tenant".
---

# Thêm tính năng an toàn theo tenant

Lộ dữ liệu tenant A sang tenant B là lỗi nghiêm trọng nhất của sản phẩm (CLAUDE.md mục 2.4). Làm đủ các bước dưới đây.

1. **Bảng mới:** `tenant_id uuid not null references tenants(id) on delete cascade` + index (thường là index ghép bắt đầu bằng `tenant_id`). Dùng skill `db-migration`.
2. **Entity** implement `ITenantOwned` (`src/ZaloAi.Core/Entities/ITenantOwned.cs`). `AppDbContext` tự áp global query filter và chặn ghi sai tenant cho mọi entity implement interface này — không cần đăng ký tay.
   - Ngoại lệ có chủ đích: `AuditLog` (tenant_id nullable, repository tự lọc), `User` (bảng toàn cục). Bảng mới không được theo ngoại lệ nếu không ghi lý do vào DECISIONS.md.
3. **Repository** `src/ZaloAi.Infrastructure/Repositories/<Name>Repository.cs`, kế thừa `TenantScopedRepository`:
   - mọi hàm public nhận `Guid tenantId` là tham số đầu tiên và gọi `EnsureTenant(tenantId)` đầu hàm;
   - vẫn lọc `tenant_id` tường minh trong truy vấn (không chỉ dựa vào global filter);
   - đăng ký `AddScoped` trong `DependencyInjection.cs`. Mẫu: `MembershipRepository`, `AuditLogRepository`.
   - Cấm dùng `AppDbContext` trực tiếp từ endpoint/job cho bảng có tenant. Cấm `IgnoreQueryFilters()` trừ code super admin/job hệ thống có comment lý do (mẫu: `AccessQueries`).
4. **Endpoint API** lấy tenant từ `ITenantContext.RequireTenantId()` (do `TenantContextMiddleware` set từ cookie), KHÔNG lấy từ body/query/route. Gắn `.RequireTenantRole(TenantRole.Staff|Owner)`. Mẫu: `src/ZaloAi.Api/Tenants/TenantSettingsEndpoints.cs`.
5. **Job Hangfire** nhận `tenantId` là tham số đầu, dòng đầu tiên gọi `TenantContext.Set(tenantId, ...)`. tenantId do API/webhook xác định, không suy ra từ input của khách cuối. Mẫu: `src/ZaloAi.Infrastructure/Jobs/SampleTenantJob.cs`.
6. **Quyền:** owner, staff (theo membership), super admin (cờ trên user, chỉ cho trang quản trị hệ thống).
7. **Test cô lập** trong `tests/ZaloAi.IntegrationTests` (collection `PostgresGroup`, fixture `PostgresFixture` có `CreateTenantAsync`, `AddMemberAsync`, `Api`):
   - tenant A tạo dữ liệu, tenant B gọi cùng API theo id của A → **404** (không phải 403, để không lộ sự tồn tại);
   - danh sách của B không chứa dữ liệu A;
   - B sửa/xóa dữ liệu A → bị chặn, dữ liệu A không đổi.
   Mẫu: `Infrastructure/TenantIsolationTests.cs`, `Api/TenantSettingsEndpointsTests.cs`.
8. **Dữ liệu cá nhân** (SĐT, tên, nội dung tin): mã hóa bằng `IFieldEncryptor`, không log, và thêm vào luồng xóa/xuất dữ liệu (M6).
9. **FE:** chạy `pnpm gen:api` ở `zalo-ai-portal`; không gửi tenantId từ FE.
