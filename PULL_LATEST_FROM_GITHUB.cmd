@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title JBZUniveresalLunix - Lay code moi nhat tu GitHub

set "ROOT=%~dp0"
set "TARGET_BRANCH=main"
set "REMOTE=origin"
set "STASH_CREATED=0"

pushd "%ROOT%" >nul 2>&1
if errorlevel 1 (
    echo [LOI] Khong the mo thu muc project:
    echo %ROOT%
    goto :FAIL
)

echo ============================================================
echo JBZUniveresalLunix - LAY CODE MOI NHAT TU GITHUB
echo ============================================================
echo Thu muc:
echo   %ROOT%
echo.

where git >nul 2>&1
if errorlevel 1 (
    echo [LOI] Chua cai Git hoac Git chua co trong PATH.
    goto :FAIL
)

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [LOI] Thu muc nay khong phai repository Git.
    goto :FAIL
)

git remote get-url %REMOTE% >nul 2>&1
if errorlevel 1 (
    echo [LOI] Khong tim thay remote "%REMOTE%".
    goto :FAIL
)

for /f "delims=" %%U in ('git remote get-url %REMOTE%') do set "REMOTE_URL=%%U"
echo GitHub:
echo   !REMOTE_URL!
echo.

set "HAS_CHANGES=0"
for /f %%A in ('git status --porcelain ^| find /c /v ""') do (
    if not "%%A"=="0" set "HAS_CHANGES=1"
)

if "!HAS_CHANGES!"=="1" (
    echo ============================================================
    echo PHAT HIEN CODE LOCAL CHUA COMMIT
    echo ============================================================
    git status --short
    echo.
    echo De tranh mat code, script KHONG tu xoa thay doi local.
    echo.
    choice /C YN /N /M "Tam cat thay doi local bang git stash, lay code moi, roi khoi phuc lai? [Y/N]: "
    if errorlevel 2 (
        echo.
        echo Da huy. Hay commit/push hoac xu ly code local truoc.
        goto :CANCEL
    )

    echo.
    echo [GIT] Dang tam cat thay doi local...
    git stash push -u -m "AUTO_STASH_BEFORE_PULL"
    if errorlevel 1 (
        echo [LOI] Khong the stash thay doi local.
        goto :FAIL
    )
    set "STASH_CREATED=1"
)

for /f "delims=" %%B in ('git branch --show-current') do set "CURRENT_BRANCH=%%B"

if /I not "!CURRENT_BRANCH!"=="%TARGET_BRANCH%" (
    echo [GIT] Dang chuyen tu nhanh "!CURRENT_BRANCH!" sang "%TARGET_BRANCH%"...
    git switch %TARGET_BRANCH%
    if errorlevel 1 (
        echo [LOI] Khong the chuyen sang nhanh %TARGET_BRANCH%.
        goto :RESTORE_AND_FAIL
    )
)

echo.
echo ============================================================
echo DANG KIEM TRA CODE MOI NHAT
echo ============================================================

echo [GIT] Fetch %REMOTE%...
git fetch %REMOTE% --prune
if errorlevel 1 (
    echo [LOI] git fetch that bai.
    echo Kiem tra Internet hoac dang nhap GitHub.
    goto :RESTORE_AND_FAIL
)

echo.
echo Local truoc khi cap nhat:
git log -1 --oneline HEAD

echo GitHub moi nhat:
git log -1 --oneline %REMOTE%/%TARGET_BRANCH%
echo.

git merge-base --is-ancestor HEAD %REMOTE%/%TARGET_BRANCH% >nul 2>&1
if errorlevel 1 (
    git merge-base --is-ancestor %REMOTE%/%TARGET_BRANCH% HEAD >nul 2>&1
    if not errorlevel 1 (
        echo Local dang co commit moi hon GitHub.
        echo Khong tu dong ghi de local.
        goto :RESTORE_AND_FAIL
    )

    echo [LOI] Local main va origin/main da re nhanh.
    echo Khong the fast-forward an toan.
    goto :RESTORE_AND_FAIL
)

echo [GIT] Dang cap nhat main bang fast-forward...
git pull --ff-only %REMOTE% %TARGET_BRANCH%
if errorlevel 1 (
    echo [LOI] git pull --ff-only that bai.
    goto :RESTORE_AND_FAIL
)

if "!STASH_CREATED!"=="1" (
    echo.
    echo [GIT] Dang khoi phuc thay doi local da tam cat...
    git stash pop
    if errorlevel 1 (
        echo.
        echo [CANH BAO] Code moi da duoc lay ve, nhung stash pop co xung dot.
        echo Khong tiep tuc tu dong sua xung dot.
        echo Hay chay: git status
        goto :DONE
    )
)

echo.
echo ============================================================
echo HOAN TAT - DA LAY CODE MOI NHAT
echo ============================================================
git status -sb
echo.
echo Commit hien tai:
git log -1 --oneline HEAD
echo.
echo Commit origin/main:
git log -1 --oneline %REMOTE%/%TARGET_BRANCH%
echo.
goto :DONE

:RESTORE_AND_FAIL
if "!STASH_CREATED!"=="1" (
    echo.
    echo [GIT] Dang khoi phuc thay doi local da tam cat...
    git stash pop >nul 2>&1
)
goto :FAIL

:CANCEL
echo.
echo Da huy theo yeu cau.
goto :DONE

:FAIL
echo.
echo ============================================================
echo DA DUNG DO CO LOI
echo Cua so se khong tu dong dong.
echo ============================================================
goto :DONE

:DONE
echo.
pause
popd >nul
exit /b 0
