@echo off
rem Tạo lại doanh nghiệp DEMO "Spa Thảo Mộc An Nhiên" đầy dữ liệu ảo (chỉ máy dev) để trang quản trị có số liệu đẹp.
rem Chạy lại bao nhiêu lần cũng được: xóa sạch doanh nghiệp demo rồi tạo lại, số liệu tính theo ngày hôm nay.
rem KHÔNG đụng doanh nghiệp khác (Khoa Học Huyệt Đạo, An Phát...).
rem Đăng nhập sau khi chạy: demo@remi.local / Dev@123456 (chủ) · lan@remi.local / Dev@123456 (nhân viên).
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

rem Ẩn câu lệnh SQL (rất nhiều dòng) — chỉ in phần tóm tắt.
set "Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore=Warning"
dotnet run --project src\ZaloAi.Api -- seed-demo
if errorlevel 1 (
  echo [!] Seed lỗi, xem thông báo phía trên.
  exit /b 1
)
echo.
echo Xong. Bật lại API bằng "dev" rồi mở http://localhost:3000 và đăng nhập demo@remi.local / Dev@123456
echo Xóa hẳn dữ liệu demo khi không cần nữa: scripts\clear-demo.cmd
