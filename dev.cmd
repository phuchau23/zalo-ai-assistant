@echo off
rem Bật môi trường dev: Postgres + Redis (Docker), API, Worker, FE, sidecar Zalo cá nhân và tunnel Zalo (nếu đã cài),
rem mỗi cái một cửa sổ riêng.
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

rem Sidecar Zalo cá nhân (connectors\zalo-personal, Node + zca-js): chỉ khi đã có file .env (khóa chung với API).
rem Lần đầu tự cài thư viện. Cửa sổ tên "ZaloAi Personal" nên stop.cmd tắt luôn.
if exist "connectors\zalo-personal\.env" (
  if not exist "connectors\zalo-personal\node_modules" (
    echo Đang cài thư viện cho sidecar Zalo cá nhân...
    call npm ci --prefix connectors\zalo-personal --no-audit --no-fund
  )
  start "ZaloAi Personal :4100" /d "connectors\zalo-personal" cmd /k npm run dev
)

rem Tunnel cho Zalo thật (README "Kết nối Zalo OA khi chạy local"): chỉ khi đã cài cloudflared và đăng nhập (có cert.pem).
rem Chỉ mở các đường Zalo cần (scripts\tunnel-dev.yml). Cửa sổ tên "ZaloAi Tunnel" nên stop.cmd tắt luôn.
where cloudflared >nul 2>&1
if not errorlevel 1 if exist "%USERPROFILE%\.cloudflared\cert.pem" (
  start "ZaloAi Tunnel" cmd /k cloudflared tunnel --no-autoupdate --config scripts\tunnel-dev.yml run zaloai-dev
  echo Tunnel Zalo: https://dev-api.haulp.io.vn
)

echo.
echo Đã mở các cửa sổ. Trang quản trị: http://localhost:3000
exit /b 0

:build_failed
echo [!] Build lỗi. Nếu báo "file đang bị dùng" thì còn API/Worker cũ đang chạy: đóng các cửa sổ đó rồi chạy lại.
exit /b 1
