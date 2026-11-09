@echo off
rem Xóa hẳn dữ liệu ẢO do scripts\seed-demo.cmd tạo: doanh nghiệp demo "Spa Thảo Mộc An Nhiên" + 2 tài khoản demo@remi.local, lan@remi.local.
rem KHÔNG đụng doanh nghiệp khác (Khoa Học Huyệt Đạo, An Phát...) hay dữ liệu bạn tự nhập. Chỉ chạy ở máy dev.
rem Bỏ qua câu hỏi xác nhận: scripts\clear-demo.cmd /y
chcp 65001 >nul
setlocal
cd /d "%~dp0.."

docker compose ps --status running postgres | findstr /i postgres >nul
if errorlevel 1 (
  echo [!] Postgres chưa chạy. Chạy dev.cmd hoặc "docker compose up -d" trước.
  exit /b 1
)

tasklist /fi "imagename eq ZaloAi.Api.exe" | findstr /i ZaloAi.Api >nul
if not errorlevel 1 (
  echo [!] API đang chạy nên không build được ^(file bị khóa^). Tắt cửa sổ API ^(hoặc chạy "stop"^) rồi chạy lại script này.
  exit /b 1
)

if /i not "%~1"=="/y" (
  choice /c YN /m "Xóa toàn bộ dữ liệu demo (Spa Thảo Mộc An Nhiên + 2 tài khoản demo)?"
  if errorlevel 2 (
    echo Đã hủy, không xóa gì.
    exit /b 0
  )
)

rem Ẩn câu lệnh SQL — chỉ in kết quả.
set "Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore=Warning"
dotnet run --project src\ZaloAi.Api -- clear-demo
if errorlevel 1 (
  echo [!] Xóa lỗi, xem thông báo phía trên.
  exit /b 1
)
echo.
echo Xong. Muốn có lại dữ liệu demo: scripts\seed-demo.cmd
