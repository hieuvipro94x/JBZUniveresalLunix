@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title JBZUniveresalLunix - Push Source GitHub

set "ROOT=%~dp0"
set "TARGET_BRANCH=main"

pushd "%ROOT%" >nul 2>&1
if errorlevel 1 (
    echo [LỖI] Không thể mo thu muc project:
    echo %ROOT%
    pause
    exit /b 1
)

echo ============================================================
echo JBZUniveresalLunix - TỔNG HỢP VÀ ĐẨY SOURCE LÊN GITHUB
echo ============================================================
echo.

if not exist "%ROOT%.gitignore" (
    echo [LỖI] Không tìm thấy .gitignore:
    echo %ROOT%.gitignore
    goto :FAIL
)

where git >nul 2>&1
if errorlevel 1 (
    echo [LỖI] Chưa cài Git hoac Git chua co trong PATH.
    goto :FAIL
)

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [LỖI] Thư mục nay khong phai repository Git.
    goto :FAIL
)

for /f "delims=" %%B in ('git branch --show-current') do set "CURRENT_BRANCH=%%B"
if /I not "%CURRENT_BRANCH%"=="%TARGET_BRANCH%" (
    echo [LỖI] Nhánh hiện tại: %CURRENT_BRANCH%
    echo File này chỉ cho phép push nhánh main.
    echo Hãy chạy: git switch main
    goto :FAIL
)

git remote get-url origin >nul 2>&1
if errorlevel 1 (
    echo [LỖI] Chưa cấu hình remote origin.
    goto :FAIL
)

echo Nhánh hiện tại : %CURRENT_BRANCH%
for /f "delims=" %%U in ('git remote get-url origin') do echo GitHub origin  : %%U
echo.
echo Mọi file sẽ được Git tự động lọc theo .gitignore.
echo.

echo ============================================================
echo CÁC THAY ĐỔI HIỆN TẠI
echo ============================================================
git status --short
echo.

set "HAS_CHANGES=0"
for /f %%A in ('git status --porcelain ^| find /c /v ""') do (
    if not "%%A"=="0" set "HAS_CHANGES=1"
)

set "AHEAD_COUNT=0"
for /f %%A in ('git rev-list --count origin/%TARGET_BRANCH%..HEAD 2^>nul') do set "AHEAD_COUNT=%%A"

if "%HAS_CHANGES%"=="0" if "%AHEAD_COUNT%"=="0" (
    echo Không có gi can day. GitHub da moi nhat.
    goto :DONE
)

choice /C YN /N /M "Tổng hợp TOÀN BỘ source hiện tại theo .gitignore? [Y/N]: "
if errorlevel 2 goto :CANCEL

echo.
echo Đang stage bằng: git add -A
git add -A
if errorlevel 1 (
    echo [LỖI] git add -A that bai.
    goto :FAIL
)

echo.
echo Các file/thay đổi SẼ được commit:
echo ------------------------------------------------------------
git status --short
echo ------------------------------------------------------------
echo.

git diff --cached --quiet
if errorlevel 1 (
    set "DEFAULT_MSG=Cập nhật source JBZUniveresalLunix"
    set "COMMIT_MSG="
    set /p "COMMIT_MSG=Nhập nội dung commit, ENTER để dùng mặc định: "
    if "!COMMIT_MSG!"=="" set "COMMIT_MSG=!DEFAULT_MSG!"

    echo.
    echo Nội dung commit: !COMMIT_MSG!
    choice /C YN /N /M "Tạo commit này? [Y/N]: "
    if errorlevel 2 goto :CANCEL

    git commit -m "!COMMIT_MSG!"
    if errorlevel 1 (
        echo [LỖI] git commit that bai.
        goto :FAIL
    )
) else (
    echo Không có thay doi staged moi.
)

echo.
echo Đang fetch origin...
git fetch origin
if errorlevel 1 (
    echo [LỖI] git fetch that bai. Kiểm tra Internet/GitHub.
    goto :FAIL
)

echo Đang rebase với origin/%TARGET_BRANCH%...
git rebase --autostash origin/%TARGET_BRANCH%
if errorlevel 1 (
    echo [LỖI] REBASE THẤT BẠI. Có thể có xung đột code.
    echo Xử lý xung đột rồi chạy:
    echo git rebase --continue
    echo git push origin %TARGET_BRANCH%
    goto :FAIL
)

echo.
echo Các commit đang chờ push:
echo ------------------------------------------------------------
git log --oneline origin/%TARGET_BRANCH%..HEAD
echo ------------------------------------------------------------
echo.

choice /C YN /N /M "Xác nhận PUSH lên GitHub origin/main? [Y/N]: "
if errorlevel 2 goto :CANCEL

git push origin %TARGET_BRANCH%
if errorlevel 1 (
    echo [LỖI] git push that bai.
    goto :FAIL
)

echo.
echo ============================================================
echo THÀNH CÔNG - GITHUB MAIN ĐÃ ĐƯỢC CẬP NHẬT
echo ============================================================
git status -sb
echo.
echo Commit local mới nhất:
git log -1 --oneline HEAD
echo.
echo Commit origin/main mới nhất:
git log -1 --oneline origin/%TARGET_BRANCH%
goto :DONE

:CANCEL
echo.
echo Đã hủy theo yeu cau. Không có source nao bi xoa.
goto :DONE

:FAIL
echo.
echo ============================================================
echo ĐÃ DỪNG DO CÓ LỖI
echo Cửa sổ sẽ KHÔNG tự động đóng.
echo ============================================================
goto :DONE

:DONE
echo.
pause
popd >nul
exit /b 0
