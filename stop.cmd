@echo off
rem Tắt môi trường dev do dev.cmd mở: API, Worker, FE (đóng luôn các cửa sổ).
rem   stop       tắt API, Worker, FE. Postgres/Redis vẫn chạy (lần sau bật nhanh hơn).
rem   stop all   tắt thêm Postgres/Redis (dữ liệu vẫn giữ, không bị xóa).
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem Cửa sổ do dev.cmd mở có tiêu đề bắt đầu bằng "ZaloAi". /T tắt cả chương trình con (dotnet, node).
taskkill /FI "WINDOWTITLE eq ZaloAi*" /T /F >nul 2>&1

rem Phòng trường hợp chạy bằng tay ở terminal khác: tắt theo tên chương trình và theo cổng của FE.
taskkill /IM ZaloAi.Api.exe /F >nul 2>&1
taskkill /IM ZaloAi.Worker.exe /F >nul 2>&1
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /r /c:":3000 .*LISTENING"') do taskkill /PID %%p /T /F >nul 2>&1

echo Đã tắt API, Worker, FE.

if /i "%~1"=="all" (
  docker compose stop
  echo Đã tắt Postgres và Redis. Dữ liệu vẫn còn, bật lại bằng dev.cmd.
)
exit /b 0
