@echo off
rem Backup database dev (Postgres trong Docker) ra thư mục backups\, giữ 14 bản mới nhất.
rem File backup chứa dữ liệu khách: không gửi cho ai, không đưa lên git (backups\ đã có trong .gitignore).
rem Lưu ý: dữ liệu mã hóa trong backup chỉ đọc được khi còn đúng khóa Security:EncryptionKey. Cất khóa riêng, cẩn thận như backup.
chcp 65001 >nul
setlocal
cd /d "%~dp0.."

docker compose ps --status running postgres | findstr /i postgres >nul
if errorlevel 1 (
  echo [!] Postgres chưa chạy. Chạy dev.cmd hoặc "docker compose up -d" trước.
  exit /b 1
)

for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set STAMP=%%t
if not exist backups mkdir backups
set FILE=backups\zaloai-%STAMP%.dump

rem -Fc: định dạng nén của Postgres, khôi phục bằng pg_restore (scripts\restore-db.cmd).
docker compose exec -T postgres pg_dump -U app -d zaloai -Fc -f /tmp/backup.dump
if errorlevel 1 goto failed
docker compose cp postgres:/tmp/backup.dump "%FILE%"
if errorlevel 1 goto failed
docker compose exec -T postgres rm -f /tmp/backup.dump

rem Giữ 14 bản mới nhất (tên file có ngày giờ nên sắp theo tên là sắp theo thời gian).
for /f "skip=14 delims=" %%f in ('dir /b /o-n backups\zaloai-*.dump') do del "backups\%%f"

echo Đã backup: %FILE%
exit /b 0

:failed
echo [!] Backup lỗi, xem thông báo phía trên.
exit /b 1
