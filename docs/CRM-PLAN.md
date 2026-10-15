# Kế hoạch CRM + AI (chốt hướng 2026-10-15)

> Chủ dự án chốt 2026-10-15: sản phẩm trở thành **CRM đầy đủ cho doanh nghiệp dịch vụ** (lịch hẹn, bán hàng, gói liệu trình,
> kho, nhân viên, báo cáo, nhiều chi nhánh) **kết hợp AI chăm sóc khách qua Zalo**. Làm **đầy đủ** theo `CRM-FEATURES.md`,
> không dừng ở MVP. Hai vai trò trong DN: **chủ** và **nhân viên**, kèm công tắc quyền chi tiết.
>
> Phần chatbot (M1–M6) đã xong code; còn chờ bên ngoài: chạy thật Zalo (DNS), ZNS (docs Zalo).

Nguyên tắc giữ nguyên: CLAUDE.md mục 2 và 8 (tách tenant tuyệt đối, mã hóa dữ liệu nhạy cảm, audit, không bịa). Thêm:
- **AI không bao giờ tự quyết việc có hậu quả tiền bạc hay lịch.** Bot chỉ *đề xuất* (muốn đặt 15:00 dịch vụ X); **hệ thống** kiểm tra chỗ trống, giá, chính sách rồi mới chốt. Nhờ vậy bot không thể nhận trùng lịch hay báo sai giá.
- **Tiền và sức khỏe mã hóa.** Số tiền trên hóa đơn thì không (cần cộng dồn trong SQL), nhưng ghi chú trị liệu, tóm tắt khách, ảnh, số tài khoản thì có.
- **Mọi thao tác tiền/lịch có nhật ký** (ai, lúc nào, trước/sau).
- **Điện thoại trước**: mọi trang mới thiết kế cho điện thoại trước, máy tính sau. Cài được như app (PWA).

---

## Thứ tự các đợt

Mỗi đợt: kế hoạch chi tiết (schema + màn hình) → chủ dự án duyệt → code từng bước → test → chủ dự án test tay → đợt sau.
Ước lượng theo làm toàn thời gian; làm ngoài giờ nhân đôi.

| Đợt | Tên | Thời gian | Kết quả thấy được |
| --- | --- | --- | --- |
| C1 | Nền CRM | 1,5 tuần | Chi nhánh, danh mục bán (dịch vụ/gói/sản phẩm) đồng bộ với kho kiến thức, nhân viên + ca làm, quyền chi tiết |
| C2 | Lịch hẹn | 2 tuần | Lịch ngày/tuần theo nhân viên và giường/phòng, đặt tay, bot tự chốt/đổi/hủy, nhắc lịch, check-in, tóm tắt khách, lịch định kỳ, hàng chờ |
| C3 | Bán hàng & gói | 2 tuần | Hóa đơn, giảm giá có duyệt, thanh toán nhiều cách + mã VietQR, phiếu thu qua Zalo, gói trừ buổi, thẻ trả trước, voucher, cọc, chốt ca |
| C4 | Kho hàng | 1 tuần | Tồn kho theo chi nhánh, nhập/xuất/kiểm kê/chuyển kho, định mức vật tư theo dịch vụ, cảnh báo sắp hết |
| C5 | Nhân viên nâng cao | 1 tuần | Hoa hồng, hiệu suất, chấm công |
| C6 | Báo cáo | 1,5 tuần | Hôm nay, doanh thu đủ chiều, hiệu quả AI, lấp đầy lịch, giữ chân, công nợ, báo cáo tuần tự động, hỏi số liệu bằng tiếng Việt |
| C7 | AI dùng dữ liệu CRM | 1,5 tuần | Chăm sóc sau buổi theo hóa đơn thật, khảo sát hài lòng, gói sắp hết, kéo khách lâu không về, sinh nhật, giới thiệu bạn, báo giá khi chủ động nhắn |
| C8 | Trải nghiệm & mở rộng | 1,5 tuần | PWA + thông báo đẩy, trang đặt lịch online, hạng thành viên, ảnh trước/sau, phiếu cam kết ký điện tử |
| **Tổng** | | **≈ 12 tuần** | |

Sau C8 mới quay lại tuần 7–8 cũ (deploy staging/production, pilot). **Khuyến nghị:** vẫn cho 1 DN dùng thử bot (phần đã xong) song song từ bây giờ để lấy góp ý.

**Chờ bên thứ ba (không phải code):** hóa đơn điện tử (nhà cung cấp được cấp phép — hỏi kế toán), tự đọc biến động số dư ngân hàng (dịch vụ trung gian), ZNS/chiến dịch hàng loạt (docs Zalo), Zalo Mini App (xét duyệt Zalo).

---

## C1 — Nền CRM (chi tiết để duyệt)

### Bảng dữ liệu mới (đều có `tenant_id`, lọc tenant, test cô lập)

```
branches(id, tenant_id, code, name, address, phone, is_default, status, created_at, updated_at)
branch_hours(id, tenant_id, branch_id, day_of_week 0-6, open_time, close_time)          -- nhiều khung/ngày được
branch_closures(id, tenant_id, branch_id, date, reason)                                  -- ngày nghỉ, lễ

catalog_items(id, tenant_id, kind: service|package|product, code UNIQUE(tenant), name, group_name,
              price, price_note, duration_minutes, buffer_minutes,                        -- dịch vụ
              session_count, validity_days,                                              -- gói
              sku, unit, cost_price, track_inventory,                                    -- sản phẩm
              knowledge_item_id?, is_active, sort_order, created_at, updated_at)
catalog_item_branches(catalog_item_id, branch_id)                                        -- trống = mọi chi nhánh
package_contents(package_id, service_id, session_count)                                  -- gói gồm dịch vụ nào, mấy buổi

staff(id, tenant_id, user_id?, branch_id, display_name, phone_enc, color, title,
      bookable, status: active|inactive, created_at, updated_at)                          -- kỹ thuật viên không cần tài khoản đăng nhập
staff_services(staff_id, catalog_item_id)                                                -- làm được dịch vụ nào
staff_shift_templates(id, tenant_id, staff_id, day_of_week, start_time, end_time)        -- ca cố định hằng tuần
staff_shifts(id, tenant_id, staff_id, branch_id, date, start_time, end_time, source: template|manual)
staff_time_off(id, tenant_id, staff_id, from_at, to_at, reason)

memberships: + permissions jsonb (quyền thêm cho nhân viên), + default_branch_id
tenants: + settings_json (múi giờ, tiền tệ, bước lịch 15/30 phút, ngưỡng giảm giá cần duyệt...)
```

Đồng bộ danh mục ↔ kho kiến thức: mục `Dịch vụ` / `Gói liệu trình` trong kho kiến thức (mã, tên, giá, thời lượng, số buổi) **tự tạo/cập nhật** `catalog_items` cùng mã khi áp dụng nhập file; sửa giá trên trang Danh mục cũng cập nhật ngược kho kiến thức (một nguồn sự thật = `catalog_items`, kho kiến thức là bản để bot đọc). Sản phẩm bán lẻ: thêm loại `Sản phẩm` vào mẫu dữ liệu (sheet mới) để bot cũng tư vấn được.

### Quyền (công tắc sếp bật cho từng nhân viên)
`view_company_revenue`, `view_all_branches`, `manage_catalog`, `manage_staff`, `apply_discount_over_limit`, `void_invoice`,
`refund`, `export_data`, `delete_customer`, `manage_inventory`, `view_reports`. Chủ = tất cả. Mặc định nhân viên: không có gì
ngoài quyền cơ bản (xem lịch/khách của chi nhánh mình, tạo lịch, tạo hóa đơn, xem doanh số của mình).

### Màn hình (FE)
- Cài đặt → **Chi nhánh**: danh sách, giờ mở cửa theo thứ, ngày nghỉ.
- **Danh mục**: 3 tab Dịch vụ / Gói / Sản phẩm; sửa nhanh giá, thời lượng; gói gồm dịch vụ nào; bật/tắt; thuộc chi nhánh nào.
- Cài đặt → **Nhân viên** (mở rộng trang hiện có): hồ sơ, chi nhánh, màu, dịch vụ làm được, ca cố định, nghỉ phép; gắn tài khoản đăng nhập (tùy chọn); công tắc quyền.
- Thanh bên: chọn chi nhánh đang xem (người có `view_all_branches`).

### API (Minimal API, OpenAPI → `pnpm gen:api`)
`/branches` CRUD + hours + closures · `/catalog` CRUD + bulk · `/staff` CRUD + services + shifts + time-off · `/tenant/team/{id}/permissions`.

### Việc kỹ thuật
- Migration `CrmFoundation` (backup DB trước). Seed chi nhánh mặc định cho tenant hiện có; tạo `catalog_items` từ `knowledge_items` hiện có.
- `ITenantContext` thêm `BranchScope` (null = mọi chi nhánh); repository lịch/hóa đơn lọc thêm chi nhánh theo quyền.
- Test: cô lập tenant mọi bảng; nhân viên không có quyền không xem được chi nhánh khác; đồng bộ danh mục ↔ kho kiến thức hai chiều.

---

## C2 — Lịch hẹn (phác thảo, chi tiết khi tới)
```
resources(id, tenant_id, branch_id, name, kind: bed|room|device, status)
appointments(id, tenant_id, branch_id, contact_id, staff_id?, resource_id?, start_at, end_at, status: booked|confirmed|arrived|in_progress|done|no_show|cancelled,
             source: bot|staff|online|walk_in|recurring, booked_by_user_id?, conversation_id?, series_id?,
             note_enc, ai_brief_enc, ai_brief_at, deposit_invoice_id?, cancel_reason, cancelled_by, reminder_24h_at, reminder_2h_at, created_at, updated_at)
appointment_items(appointment_id, catalog_item_id, duration_minutes, price_snapshot, staff_id?)
appointment_events(appointment_id, kind, by_user_id?, by_bot, data_json, at)           -- nhật ký đổi giờ/hủy/check-in
appointment_series(id, tenant_id, rule, until)                                           -- lịch định kỳ
waitlist(id, tenant_id, branch_id, contact_id, catalog_item_id, preferred_from, preferred_to, status)
no_show_counts: tính từ appointments
```
Bot chốt lịch: AI trả `booking_intent {service_code, date, time, staff?, party_size}` → `AvailabilityService` tính khung trống
(giờ mở cửa, ca nhân viên, nghỉ phép, giường, thời lượng + đệm, lịch đã có) → nếu trống thì tạo `appointments` (source bot) và bot báo
"đã đặt"; nếu kín thì trả 2–3 khung gần nhất cho bot gợi ý. Không chốt khi: gói ⚠, khách đang phàn nàn, nhóm ≥ 3, dịch vụ chưa có giá.
Nhắc lịch: job quét mỗi phút, gửi qua kênh (Zalo 7 ngày), khách trả lời "dời/hủy" → bot xử lý theo chính sách.
Tóm tắt khách (`ai_brief_enc`): AI viết lúc chốt lịch và làm mới trước giờ hẹn 2 giờ từ hội thoại + nhật ký + gói + buổi trước.

## C3 — Bán hàng & gói (phác thảo)
```
invoices(id, tenant_id, branch_id, number, contact_id?, appointment_id?, cashier_user_id, status: open|paid|partial|void|refunded,
         subtotal, discount_amount, discount_reason, discount_approved_by?, total, paid_amount, note, issued_at, voided_at, void_reason, voided_by)
invoice_lines(id, invoice_id, catalog_item_id, kind, description, qty, unit_price, discount, line_total, performer_staff_id?, customer_package_id?)
payments(id, tenant_id, invoice_id, method: cash|transfer|card|wallet|voucher, amount, reference, received_at, received_by, qr_payload)
customer_packages(id, tenant_id, contact_id, catalog_item_id, invoice_id, total_sessions, used_sessions, expires_at, status: active|paused|expired|transferred, transferred_to_contact_id?)
package_redemptions(id, customer_package_id, appointment_id?, invoice_line_id?, staff_id?, at)
wallets(contact_id, balance) + wallet_transactions(kind: topup|bonus|spend|refund, amount, invoice_id?, at)
vouchers(id, tenant_id, code, kind: percent|amount, value, min_total, valid_from, valid_to, max_uses, used_count, status)
cash_shifts(id, tenant_id, branch_id, opened_by, opened_at, opening_cash, closed_at, expected_cash, counted_cash, note)
revenue_entries(id, tenant_id, branch_id, date, kind: service|package_recognized|product|other, amount, invoice_id?, redemption_id?)   -- doanh thu ghi nhận theo buổi
tenant_bank_accounts(id, tenant_id, branch_id?, bank_bin, account_no_enc, account_name, is_default)   -- cho VietQR
```
VietQR: sinh chuỗi EMVCo theo chuẩn công bố của NAPAS (kiểm chứng docs trước khi code, ghi `docs/payments-notes.md`). Phiếu thu gửi qua Zalo bằng `IChannelAdapter`.

## C4 — Kho hàng (phác thảo)
`suppliers`, `stock_levels(branch_id, catalog_item_id, qty, avg_cost)`, `stock_movements(kind: purchase|sale|adjust|transfer_in|transfer_out|consume, qty, unit_cost, ref_type, ref_id, by, at)`, `purchase_orders` + lines, `stock_counts` (kiểm kê), `service_consumables(service_id, product_id, qty_per_session)` → mỗi lần hoàn thành dịch vụ tự trừ kho. Cảnh báo dưới mức tối thiểu.

## C5 — Nhân viên nâng cao (phác thảo)
`commission_rules(scope: service|package|product|all, kind: percent|fixed, value, staff_id?)`, `commission_entries(staff_id, invoice_line_id, amount, period)`, bảng hoa hồng tháng; hiệu suất (doanh số, khách quay lại, điểm hài lòng); `attendance(staff_id, clock_in, clock_out, branch_id)`.

## C6 — Báo cáo (phác thảo)
Truy vấn tổng hợp trong `ReportRepository` (lọc tenant + chi nhánh theo quyền), cache ngắn. Trang: Hôm nay · Doanh thu (ngày/tuần/tháng, so kỳ trước; theo dịch vụ, nhân viên, chi nhánh, nguồn khách, hình thức thanh toán) · Hiệu quả AI (lịch bot chốt, doanh thu từ khách bot chốt, tin bot xử lý) · Lấp đầy lịch theo giờ · Khách (mới/quay lại, giữ chân, chi tiêu TB) · Công nợ & gói chưa dùng · Xuất Excel. Báo cáo tuần tự động (job thứ 2) qua Telegram/Zalo. "Hỏi số liệu bằng tiếng Việt": AI chọn **báo cáo có sẵn + tham số** (không sinh SQL tự do).

## C7 — AI dùng dữ liệu CRM (phác thảo)
Chăm sóc sau buổi từ `appointments.done`/hóa đơn (dùng "Sau khi làm"); khảo sát 1–5 sao qua chat, < 3 → báo sếp; gói còn ≤ 2 buổi hoặc sắp hết hạn → bot gợi ý gia hạn; khách quá chu kỳ quen → kéo về; sinh nhật; giới thiệu bạn (mã); bot báo giá đầy đủ khi chủ động nhắn; hàng chờ có chỗ → bot nhắn. Mọi tin chủ động tuân thủ đồng ý nhận tin + khung giờ + hạn 7 ngày Zalo.

## C8 — Trải nghiệm & mở rộng (phác thảo)
PWA + Web Push (lịch mới bot chốt, khách đòi gặp người, đánh giá xấu); trang đặt lịch online công khai `/book/<slug>`; hạng thành viên (ngưỡng chi tiêu, ưu đãi); ảnh trước/sau (mã hóa, quyền xem, đồng ý khách); phiếu cam kết ký trên điện thoại (lưu PDF mã hóa).

---

## Gói bán (gợi ý, chốt khi định giá)
Cơ bản (CRM) · Pro (CRM + AI Zalo) · Doanh nghiệp (nhiều chi nhánh, hoa hồng, kho). Hạn mức hội thoại/tháng giữ như hiện nay cho phần AI.
