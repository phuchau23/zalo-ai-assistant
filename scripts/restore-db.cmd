@echo off
rem Khôi phục database dev từ file backup. GHI ĐÈ toàn bộ dữ liệu hiện tại.
rem Cách dùng: scripts\restore-db.cmd backups\zaloai-20261003-101500.dump
chcp 65001 >nul
setlocal
cd /d "%~dp0.."

if "%~1"=="" (
  echo Cách dùng: scripts\restore-db.cmd backups\zaloai-....dump
  echo Các bản backup hiện có:
  dir /b backups\*.dump 2>nul
  exit /b 1
)
if not exist "%~1" (
  echo [!] Không thấy file %~1
  exit /b 1
)

echo CẢNH BÁO: toàn bộ dữ liệu hiện tại của database dev sẽ bị thay bằng bản backup %~1
echo Hãy tắt API và Worker trước (chạy stop.cmd), nếu không lệnh khôi phục có thể bị treo.
set /p CONFIRM=Gõ YES để tiếp tục:
if not "%CONFIRM%"=="YES" (
  echo Đã hủy, không thay đổi gì.
  exit /b 1
)

docker compose cp "%~1" postgres:/tmp/restore.dump
if errorlevel 1 goto failed
rem --clean --if-exists: xóa bảng hiện có rồi tạo lại từ backup. --no-owner: không phụ thuộc user tạo ra bản backup.
docker compose exec -T postgres pg_restore -U app -d zaloai --clean --if-exists --no-owner /tmp/restore.dump
if errorlevel 1 goto failed
docker compose exec -T postgres rm -f /tmp/restore.dump
echo Đã khôi phục từ %~1
exit /b 0

:failed
echo [!] Khôi phục lỗi, xem thông báo phía trên.
exit /b 1
