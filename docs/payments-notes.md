# Ghi chú thanh toán (VietQR) — kiểm tra 2026-10-15

## VietQR (NAPAS 247, chuyển khoản tới số tài khoản)

Mã QR theo chuẩn **EMVCo Merchant-Presented QR**, hồ sơ NAPAS "QR Switching v1.5.2". Nguồn kiểm chứng: không có tài liệu
NAPAS công khai; đối chiếu nhiều thư viện mã nguồn mở triển khai cùng một cấu trúc (pub.dev `vietqr_gen`, packagist `chinh/vietqr`,
`liopay/vietqr`). **Trước khi dùng thật: chủ dự án quét thử mã bằng app ngân hàng của mình** (xem TEST-FLOW Buổi 9).

Cấu trúc payload (mỗi trường = `ID(2) + LEN(2) + VALUE`):

| ID | Giá trị | Ghi chú |
| --- | --- | --- |
| 00 | `01` | Payload format indicator |
| 01 | `12` khi có số tiền (động, một lần), `11` khi không có (tĩnh) | |
| 38 | Merchant account info (NAPAS) | lồng: `00` = `A000000727` (GUID NAPAS), `01` = lồng {`00` = BIN ngân hàng 6 số, `01` = số tài khoản}, `02` = `QRIBFTTA` (chuyển tới tài khoản) |
| 53 | `704` | VND |
| 54 | số tiền (số nguyên, không dấu) | chỉ khi có |
| 58 | `VN` | |
| 62 | Additional data | lồng: `08` = nội dung chuyển khoản (≤ 25 ký tự an toàn: chữ không dấu, số, khoảng trắng) |
| 63 | CRC16-CCITT (FALSE): poly 0x1021, init 0xFFFF, tính trên toàn chuỗi kể cả `6304`, 4 ký tự hex in hoa | luôn cuối |

Mã BIN thường gặp (NAPAS): Vietcombank 970436, Techcombank 970407, BIDV 970418, VietinBank 970415, MB 970422, ACB 970416,
Sacombank 970403, TPBank 970423, VPBank 970432, Agribank 970405, HDBank 970437, VIB 970441, SHB 970443, OCB 970448, MSB 970426,
Eximbank 970431, SeABank 970440, LPBank 970449, Nam A 970428, BacA 970409, VietABank 970427, PVcomBank 970412, CAKE 546034,
Timo 963388, Ubank 546035. (Chủ dự án kiểm lại BIN ngân hàng DN dùng khi cài đặt.)

Triển khai: `src/ZaloAi.Infrastructure/Billing/VietQr.cs` (`VietQr.Build(...)`, `VietQr.Crc16(...)`); ảnh PNG qua thư viện **QRCoder**
(MIT, thuần .NET, renderer `PngByteQRCode` không cần System.Drawing) — thư viện thêm 2026-10-15 cho C3, chủ dự án có thể thay.

## Chưa làm (chờ đối tác)
- Tự xác nhận đã nhận chuyển khoản (đọc biến động số dư): cần dịch vụ trung gian (Casso, SePay...) hoặc API ngân hàng — C3 ghi tay "đã nhận".
- Hóa đơn điện tử: nhà cung cấp được Tổng cục Thuế cấp phép — hỏi kế toán.
