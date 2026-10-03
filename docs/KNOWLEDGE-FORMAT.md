# Định dạng dữ liệu kiến thức (mẫu chuẩn)

Doanh nghiệp nạp kiến thức cho bot bằng **file theo mẫu chuẩn** (Excel `.xlsx` hoặc JSON). Mỗi mục có **Mã** riêng, nhờ đó hệ thống so sánh được bản mới với bản đang có và cho duyệt từng thay đổi trước khi áp dụng (giống review pull request).

Tài liệu tự do (PDF, Word, Excel không theo mẫu, TXT, MD) vẫn nạp được dưới dạng "tài liệu tham khảo", nhưng **không so sánh từng mục** được.

Phiên bản định dạng hiện tại: **1**.

---

## 1. Năm loại mục

| Loại | Sheet Excel | Khóa JSON | Mỗi mục là |
| --- | --- | --- | --- |
| Thông tin chung | `Thông tin chung` | `info` | một thông tin: địa chỉ chi nhánh, hotline, giờ mở cửa, cách đặt lịch, thanh toán... |
| Dịch vụ | `Dịch vụ` | `services` | một dịch vụ lẻ có giá |
| Gói / liệu trình | `Gói liệu trình` | `packages` | một gói nhiều buổi / combo |
| Câu hỏi thường gặp | `Câu hỏi thường gặp` | `faqs` | một cặp hỏi – đáp |
| Chính sách | `Chính sách` | `policies` | một chính sách: đặt lịch, hủy/đổi lịch, hoàn tiền, bảo hành, ưu đãi... |

File Excel mẫu còn có sheet `Hướng dẫn` (chỉ để đọc, hệ thống bỏ qua).

## 2. Cột / trường

`*` = bắt buộc. Các cột khác để trống được.

### Thông tin chung
| Cột Excel | Trường JSON | Kiểu | Ghi chú |
| --- | --- | --- | --- |
| Mã * | `code` | mã | ví dụ `TT-HOTLINE-Q12` |
| Tiêu đề * | `title` | chữ ≤ 200 | ví dụ "Hotline chi nhánh Quận 12" |
| Nội dung * | `content` | chữ ≤ 4000 | |

### Dịch vụ
| Cột Excel | Trường JSON | Kiểu | Ghi chú |
| --- | --- | --- | --- |
| Mã * | `code` | mã | ví dụ `DV-MASSAGE-DY-60` |
| Nhóm | `group` | chữ ≤ 100 | ví dụ "Trị liệu chuyên sâu" |
| Tên dịch vụ * | `name` | chữ ≤ 200 | |
| Thời lượng (phút) | `durationMinutes` | số nguyên > 0 | |
| Giá (VNĐ) | `price` | số nguyên ≥ 0 | để trống nếu "liên hệ" / giá thay đổi — ghi lý do ở Ghi chú giá |
| Đơn vị giá | `priceUnit` | chữ ≤ 50 | mặc định "lượt"; ví dụ "m²", "buổi", "tháng" |
| Ghi chú giá | `priceNote` | chữ ≤ 500 | ví dụ "Liên hệ báo giá sau khi khảo sát" |
| Mô tả | `description` | chữ ≤ 4000 | quy trình, bao gồm những gì |
| Phù hợp với | `suitableFor` | chữ ≤ 2000 | |
| Không nên làm | `notSuitableFor` | chữ ≤ 2000 | chống chỉ định, trường hợp phải hỏi chuyên viên |
| Chi nhánh | `branches` | chữ ≤ 200 | để trống = mọi chi nhánh |
| Ghi chú | `note` | chữ ≤ 2000 | ghi chú nội bộ cho bot |

### Gói liệu trình
| Cột Excel | Trường JSON | Kiểu | Ghi chú |
| --- | --- | --- | --- |
| Mã * | `code` | mã | ví dụ `GOI-GIAM-MO-BUNG` |
| Nhóm | `group` | chữ ≤ 100 | |
| Tên gói * | `name` | chữ ≤ 200 | |
| Số buổi | `sessions` | số nguyên > 0 | |
| Phút mỗi buổi | `minutesPerSession` | số nguyên > 0 | |
| Giá (VNĐ) | `price` | số nguyên ≥ 0 | giá cả gói |
| Ghi chú giá | `priceNote` | chữ ≤ 500 | |
| Mô tả | `description` | chữ ≤ 4000 | |
| Phù hợp với | `suitableFor` | chữ ≤ 2000 | |
| Không nên làm | `notSuitableFor` | chữ ≤ 2000 | |
| Ghi chú | `note` | chữ ≤ 2000 | |

### Câu hỏi thường gặp
| Cột Excel | Trường JSON | Kiểu |
| --- | --- | --- |
| Mã * | `code` | mã |
| Câu hỏi * | `question` | chữ ≤ 500 |
| Trả lời * | `answer` | chữ ≤ 4000 |

### Chính sách
| Cột Excel | Trường JSON | Kiểu |
| --- | --- | --- |
| Mã * | `code` | mã |
| Chủ đề * | `topic` | chữ ≤ 200 |
| Nội dung * | `content` | chữ ≤ 4000 |

## 3. Quy tắc về Mã

- Chỉ gồm **chữ in hoa không dấu A–Z, số 0–9, dấu gạch ngang `-`**, dài 2–50 ký tự. Ví dụ hợp lệ: `DV-TRI-LIEU-CVG-60`. Hệ thống tự viết hoa và bỏ khoảng trắng hai đầu.
- **Không trùng** trong cùng một file (kể cả giữa các sheet).
- **Giữ nguyên mã** khi sửa nội dung một mục. Đổi mã = hệ thống coi là mục mới + mục cũ "không còn trong file".
- Gợi ý tiền tố: `TT-` thông tin chung, `DV-` dịch vụ, `GOI-` gói, `FAQ-` câu hỏi, `CS-` chính sách.

## 4. Định dạng JSON

```json
{
  "format": "zaloai-knowledge",
  "version": 1,
  "info": [
    { "code": "TT-HOTLINE-Q12", "title": "Hotline chi nhánh Quận 12", "content": "0559.669.663" }
  ],
  "services": [
    {
      "code": "DV-MASSAGE-DY-60",
      "group": "Gội đầu & massage",
      "name": "Massage Đông y 60 phút",
      "durationMinutes": 60,
      "price": 450000,
      "priceUnit": "lượt",
      "description": "..."
    }
  ],
  "packages": [],
  "faqs": [
    { "code": "FAQ-CO-DAU-KHONG", "question": "Bấm huyệt có đau không?", "answer": "..." }
  ],
  "policies": []
}
```

- File UTF-8. Trường không dùng có thể bỏ hẳn hoặc để `null`.
- `price` là **số nguyên VNĐ**, không dấu chấm/phẩy, không chữ "đ" (`450000`, không phải `"450.000đ"`). File Excel thì ô Giá có thể là số hoặc chữ dạng `450.000` / `450.000đ` — hệ thống tự đọc.

## 5. Khi nhập file (so sánh và gộp)

Nhập file **không thay đổi dữ liệu ngay**. Hệ thống tạo **bản xem trước**:

| Trạng thái | Nghĩa | Mặc định |
| --- | --- | --- |
| Thêm mới | Mã chưa có | chọn |
| Thay đổi | Mã đã có, nội dung khác — hiện từng trường cũ → mới | chọn |
| Có thể trùng | Mã mới nhưng tên/câu hỏi gần giống một mục đang có | không chọn — phải quyết: gộp vào mục cũ hay giữ cả hai |
| Không còn trong file | Mục đang có nhưng file không có | không chọn (giữ lại); chọn = xóa |
| Không đổi | Giống hệt | ẩn |

Lỗi định dạng (thiếu cột bắt buộc, mã sai, trùng mã, giá không phải số...) được liệt kê theo **sheet + dòng** và không tạo bản xem trước cho tới khi sửa xong.

## 6. Nhờ AI điền mẫu

Doanh nghiệp có tài liệu lộn xộn (bảng giá ảnh chụp, file Word, nội dung website) có thể nhờ ChatGPT / Claude / Gemini chuyển sang mẫu: dán **câu lệnh dưới đây** + tài liệu của mình, rồi lưu kết quả thành file `.json` và nhập vào hệ thống. Luôn đọc lại kết quả trước khi nhập — AI có thể đọc sai giá.

> Lưu ý quyền riêng tư: chỉ đưa cho AI bên ngoài những tài liệu công khai (bảng giá, dịch vụ, FAQ). Không đưa danh sách khách hàng, số điện thoại, hồ sơ bệnh.

````text
Bạn là trợ lý nhập liệu. Hãy chuyển tài liệu tôi gửi kèm thành JSON đúng định dạng dưới đây, KHÔNG thêm lời giải thích, chỉ trả về JSON.

Định dạng:
{
  "format": "zaloai-knowledge",
  "version": 1,
  "info":     [ { "code", "title", "content" } ],
  "services": [ { "code", "group", "name", "durationMinutes", "price", "priceUnit", "priceNote", "description", "suitableFor", "notSuitableFor", "branches", "note" } ],
  "packages": [ { "code", "group", "name", "sessions", "minutesPerSession", "price", "priceNote", "description", "suitableFor", "notSuitableFor", "note" } ],
  "faqs":     [ { "code", "question", "answer" } ],
  "policies": [ { "code", "topic", "content" } ]
}

Quy tắc:
1. "code": chữ in hoa không dấu, số, dấu gạch ngang, 2–50 ký tự, không trùng nhau. Tiền tố: TT- (thông tin chung), DV- (dịch vụ), GOI- (gói nhiều buổi), FAQ- (câu hỏi), CS- (chính sách). Đặt mã dễ hiểu từ tên, ví dụ "Massage Đông y 60 phút" → "DV-MASSAGE-DONG-Y-60".
2. "price": số nguyên VNĐ (450.000đ → 450000). Không rõ giá → null và ghi lý do vào "priceNote". KHÔNG tự đoán giá.
3. Mỗi mức thời lượng/giá khác nhau của cùng dịch vụ là một mục riêng.
4. Gói nhiều buổi đưa vào "packages", ghi "sessions" và "minutesPerSession".
5. Địa chỉ, chi nhánh, hotline, giờ mở cửa, cách đặt lịch, thanh toán → "info" (mỗi thông tin một mục).
6. Đặt lịch, hủy/đổi lịch, hoàn tiền, bảo hành, ưu đãi → "policies".
7. Giữ nguyên nội dung gốc, không thêm thông tin không có trong tài liệu. Trường không có thông tin → null.
8. Nội dung tiếng Việt có dấu.
````

## 7. Công cụ kiểm tra / chuyển định dạng (cho chủ dự án)

Không cần database, chạy trong thư mục repo BE:

```
dotnet run --project src/ZaloAi.Api -- knowledge validate <file.xlsx|file.json>
dotnet run --project src/ZaloAi.Api -- knowledge convert <vào.json|vào.xlsx> <ra.xlsx|ra.json>
dotnet run --project src/ZaloAi.Api -- knowledge diff <cũ.xlsx|cũ.json> <mới.xlsx|mới.json>
```

- `validate`: in lỗi theo sheet/dòng/cột, hoặc "Hợp lệ. N mục (...)". Mã thoát 0 = hợp lệ, 1 = có lỗi.
- `convert`: JSON (AI tạo) → Excel (người sửa) và ngược lại. File vào còn lỗi thì không chuyển.
- `diff`: so sánh hai file đúng như khi nhập file mới lên hệ thống (file cũ đóng vai dữ liệu đang có): thêm mới, thay đổi từng trường, có thể trùng, không còn trong file mới.

Trên giao diện (M2 bước 7): nút **Tải file mẫu** (`GET /knowledge/template`) và **Xuất dữ liệu hiện tại** (`GET /knowledge/export?format=xlsx|json`).
