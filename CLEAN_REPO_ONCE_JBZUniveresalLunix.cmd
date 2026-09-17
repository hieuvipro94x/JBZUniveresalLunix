@echo off
setlocal EnableExtensions
chcp 65001 >nul
title JBZUniveresalLunix - Dọn repository

set "ROOT=%~dp0"
pushd "%ROOT%" >nul 2>&1
if errorlevel 1 goto :FAIL

echo ============================================================
echo JBZUniveresalLunix - DỌN REPOSITORY MỘT LẦN
echo ============================================================
echo.

if not exist "%ROOT%JBZUniveresalLunix.csproj" (
    echo [LỖI AN TOÀN] Không tìm thấy JBZUniveresalLunix.csproj.
    echo Script này chỉ được chạy trong project JBZUniveresalLunix.
    goto :FAIL
)

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [LỖI GIT] Thư mục hiện tại không phải repository Git.
    goto :FAIL
)

git config core.longpaths true >nul 2>&1

echo [1/3] Bỏ staging cũ để tổng hợp lại repository sạch...
git reset >nul
if errorlevel 1 goto :FAIL

echo [2/3] Xóa file tạm, tài liệu chuyển đổi và source D2XX cũ...

del /Q "%ROOT%BUILD_ONE_FILE_JBZUniveresalLunix_V*.cmd" >nul 2>&1
del /Q "%ROOT%BUILD_ONE_FILE_JBZUniversalTester_V*.cmd" >nul 2>&1
del /Q "%ROOT%BUILD_ONE_FILE_TWO_PROJECTS*.zip" >nul 2>&1

if exist "%ROOT%docs" rmdir /S /Q "%ROOT%docs"
if exist "%ROOT%JBZ_Windows" rmdir /S /Q "%ROOT%JBZ_Windows"
del /Q "%ROOT%JBZ_Windows*.zip" >nul 2>&1
del /Q "%ROOT%Trace phan mem FirmwareDowloader*.gz" >nul 2>&1
del /Q "%ROOT%AUDIT_REPORT.md" >nul 2>&1
del /Q "%ROOT%AUTO_START_KEYSIGHT_NOTES.txt" >nul 2>&1
del /Q "%ROOT%CODEX_HANDOFF_*.md" >nul 2>&1
del /Q "%ROOT%LOGIC_FIX_NOTES.txt" >nul 2>&1
del /Q "%ROOT%PROTOCOL_FIX_NOTES.txt" >nul 2>&1
del /Q "%ROOT%V15_CHANGELOG.txt" >nul 2>&1
del /Q "%ROOT%V15_1_CHANGELOG.txt" >nul 2>&1
del /Q "%ROOT%V16_CHANGELOG.txt" >nul 2>&1
del /Q "%ROOT%SAVE_CODEX_CHECKPOINT*.ps1" >nul 2>&1
del /Q "%ROOT%TestViewModel_LeakRealtime_SAFE.diff" >nul 2>&1
del /Q "%ROOT%sample.d2xx.jbzproduct.json" >nul 2>&1

for %%F in (
    "Models\ProductBundle.cs"
    "Services\BoardAddressMapper.cs"
    "Services\BoardIoDecoder.cs"
    "Services\D2xxBoardTransport.cs"
    "Services\D2xxResistanceRouting.cs"
    "Services\IoMappingFramePresenter.cs"
    "Services\ManualProbeSession.cs"
    "Services\ProbeContactClassifier.cs"
    "Services\ProbeStateTracker.cs"
    "Services\ThtModelParser.cs"
    "Services\UnifiedBoardTransport.cs"
) do (
    if exist "%ROOT%%%~F" del /Q "%ROOT%%%~F" >nul 2>&1
)

del /Q "%ROOT%JBZUniversalTester.csproj" >nul 2>&1
del /Q "%ROOT%JBZUniversalTester.slnx" >nul 2>&1
del /Q "%ROOT%HardwareVerification\JBZUniversalTester.HardwareVerification.csproj" >nul 2>&1
del /Q "%ROOT%Tests\JBZUniversalTester.SelfTests.csproj" >nul 2>&1

echo [3/3] Stage lại toàn bộ source hiện hành...
git -c core.longpaths=true add -A
if errorlevel 1 goto :FAIL

echo.
echo ============================================================
echo REPOSITORY ĐÃ ĐƯỢC TỔNG HỢP LẠI
echo ============================================================
echo.
echo Các file chuẩn bị commit:
echo ------------------------------------------------------------
git status --short
echo ------------------------------------------------------------
echo.
echo LƯU Ý:
echo - Các dòng D của source D2XX/docs cũ cần được commit MỘT LẦN
echo   để chúng thật sự biến mất khỏi repository GitHub.
echo - Source UART hiện hành Services\Jbz*.cs KHÔNG bị xóa.
echo - Script này KHÔNG tự commit và KHÔNG tự push.
echo.
pause
popd >nul
exit /b 0

:FAIL
echo.
echo [LỖI] Dọn repository thất bại. Không tự commit/push.
echo.
pause
popd >nul
exit /b 1
