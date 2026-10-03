---
name: db-migration
description: Cách thay đổi schema Postgres/EF Core an toàn. Dùng mỗi khi thêm/sửa/xóa bảng, cột, index, hoặc đổi số chiều vector.
---

# Thay đổi schema an toàn

Đổi schema là việc phải **dừng lại chờ chủ dự án duyệt kế hoạch** trước khi code (CLAUDE.md mục 9).

1. Sửa entity (`src/ZaloAi.Core/Entities/`) + cấu hình `IEntityTypeConfiguration` trong `src/ZaloAi.Infrastructure/Persistence/Configurations/`.
   - Tên bảng/cột tự sinh snake_case (EFCore.NamingConventions). Đặt `ToTable("...")` tường minh.
   - Enum lưu chữ thường: `.HasConversion<LowercaseEnumConverter<TEnum>>()` + `HasMaxLength`.
   - Id: `Guid` UUID v7, `ValueGeneratedNever()`; `AppDbContext` tự gán `Id`, `CreatedAt`, `UpdatedAt` nếu để trống.
   - Bảng có dữ liệu tenant: theo skill `tenant-safe-feature`.
2. Tạo migration và **đọc lại SQL**:
   ```
   dotnet ef migrations add <TenMigration> -p src/ZaloAi.Infrastructure -s src/ZaloAi.Api -o Persistence/Migrations
   dotnet ef migrations script <MigrationTruoc> <TenMigration> -p src/ZaloAi.Infrastructure -s src/ZaloAi.Api
   ```
   Code migration sinh tự động đã được loại khỏi analyzer (`Persistence/Migrations/.editorconfig`); không sửa tay, không sửa migration cũ.
3. Không xóa cột đang dùng trong cùng một lần deploy: thêm cột mới → deploy code dùng cả hai → migrate dữ liệu → xóa cột cũ ở lần sau.
4. Index trên bảng lớn dùng `CREATE INDEX CONCURRENTLY` (migration riêng, `migrationBuilder.Sql(..., suppressTransaction: true)`).
5. Đổi model hoặc số chiều embedding: tạo cột/bảng vector mới, job re-embed toàn bộ, chuyển truy vấn, rồi mới xóa cái cũ.
6. Bảng `hangfire.*` do Hangfire tự quản, không tạo migration EF cho nó.
7. Cập nhật seed (`Persistence/Seed/DevSeeder.cs`) nếu cần. Ghi DECISIONS.md nếu thay đổi lớn.
8. Kiểm tra: `dotnet test` (test tích hợp chạy migration trên Postgres thật bằng Testcontainers), rồi `dotnet ef database update -p src/ZaloAi.Infrastructure -s src/ZaloAi.Api` cho DB dev. Nhắc chủ dự án chạy `scripts\backup-db.cmd` trước khi áp migration lên dữ liệu thật.
