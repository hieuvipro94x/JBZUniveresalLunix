@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

title JBZUniveresalLunix V12.9.5 - Verify and Build
set "LOG=verify_build_V12.9.5.log"

echo ============================================================ > "%LOG%"
echo JBZUniveresalLunix V12.9.5 VERIFY BUILD >> "%LOG%"
echo Date: %DATE% %TIME% >> "%LOG%"
echo ============================================================ >> "%LOG%"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Khong tim thay .NET SDK 8. >> "%LOG%"
    echo [ERROR] Khong tim thay .NET SDK 8.
    echo Cai .NET 8 SDK tren may build, sau do chay lai file nay.
    pause
    exit /b 1
)

echo [1/4] dotnet --version
dotnet --version >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

echo [2/4] Restore
dotnet restore "JBZUniveresalLunix.csproj" -r win-x86 --nologo >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

echo [3/4] Build Release win-x86
dotnet build "JBZUniveresalLunix.csproj" -c Release -r win-x86 --no-restore --nologo >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

echo [4/4] Publish one-file
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "Scripts\Publish-OneFile.ps1" -Runtime "win-x86" -Configuration "Release" -OutputFolder "PublishSingle" >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

echo.
echo ============================================================
echo VERIFY + BUILD + PUBLISH V12.9.5 SUCCESS
echo Log: %CD%\%LOG%
echo ============================================================
pause
exit /b 0

:fail
echo.
echo ============================================================
echo BUILD FAILED - xem log: %CD%\%LOG%
echo ============================================================
pause
exit /b 1
