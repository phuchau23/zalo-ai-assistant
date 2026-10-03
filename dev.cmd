@echo off
rem Bật môi trường dev: Postgres + Redis (Docker), API, Worker và FE, mỗi cái một cửa sổ riêng.
rem Tắt: đóng từng cửa sổ (hoặc Ctrl+C). Postgres/Redis vẫn chạy nền, tắt bằng: docker compose down
chcp 65001 >nul
setlocal
cd /d "%~dp0"

docker info >nul 2>&1
if errorlevel 1 (
  echo [!] Docker Desktop chưa chạy. Mở Docker Desktop, đợi "Engine running" rồi chạy lại.
  exit /b 1
)
docker compose up -d --wait
if errorlevel 1 exit /b 1

rem Build trước một lần rồi chạy --no-build: tránh API và Worker cùng build một lúc, giành nhau file DLL.
echo Đang build API và Worker...
dotnet build src\ZaloAi.Api -v q -nologo
if errorlevel 1 goto build_failed
dotnet build src\ZaloAi.Worker -v q -nologo
if errorlevel 1 goto build_failed

start "ZaloAi API :4000" cmd /k dotnet run --project src\ZaloAi.Api --no-build
start "ZaloAi Worker" cmd /k dotnet run --project src\ZaloAi.Worker --no-build
if exist "..\zalo-ai-portal\package.json" start "ZaloAi Portal :3000" /d "..\zalo-ai-portal" cmd /k pnpm dev

echo.
echo Đã mở các cửa sổ. Trang quản trị: http://localhost:3000
exit /b 0

:build_failed
echo [!] Build lỗi. Nếu báo "file đang bị dùng" thì còn API/Worker cũ đang chạy: đóng các cửa sổ đó rồi chạy lại.
exit /b 1
