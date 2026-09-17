@echo off
setlocal EnableExtensions
chcp 65001 >nul
title JBZUniveresalLunix - Tiep tuc phien lam viec

set "ROOT=%~dp0"
pushd "%ROOT%" >nul 2>&1
if errorlevel 1 (
    echo Khong the mo thu muc project: %ROOT%
    pause
    exit /b 1
)

cls
echo ============================================================
echo JBZUniveresalLunix - BAN GIAO PHIEN LAM VIEC 2026-08-28
echo ============================================================
echo.
echo PHIEN BAN BAN GIAO: V16.0.117
echo NHANH GIT          : main
echo REMOTE             : origin/main
echo.

echo [1] CAC NOI DUNG DA HOAN THANH
echo ------------------------------------------------------------
echo - History giu dung 14 cot; giao dien tieng Viet, CSV tieng Han.
echo - Cot Ho so kiem tra ghi cong doan continuity, dien tro, leak,
echo   loi day va thoi gian lap/test/thao san pham.
echo - Barcode chi luu khi san pham PASS va may in xac nhan Printed.
echo - Tat Tu dong in khi PASS: khong tao barcode, khong goi may in,
echo   khong ghi loi may in vao history.
echo - Moi cycle snapshot dung file THT dang test. Sau khi doi A.tht
echo   sang B.tht, cycle moi xuat B.tht; cycle cu van giu A.tht.
echo - CSV/XLSX xuat toan bo du lieu theo bo loc, khong bi gioi han
echo   boi 20.000 dong DataGrid hoac gioi han 50.000 dong cu.
echo - Thu tu xuat: Ma hang tang dan, Started tang dan, Id tang dan.
echo - Cot Chuong trinh snapshot ten va version dang chay theo dang:
echo   JBZUniveresalLunix Vx.x.x.
echo - BUILD_ONE_FILE.cmd da doi sang co che version thong minh:
echo   source doi va chua tang version thi tu tang mot lan;
echo   version da tang thi giu nguyen; build lai thi khong tang.
echo.

echo [2] KIEM TRA DA THUC HIEN
echo ------------------------------------------------------------
echo - Build Release: 0 loi, 0 canh bao.
echo - Self-test: 32/32 PASS.
echo - TEM_BE_QR da dung thu tu V00..V10 cua 60-15 va QR
echo   PartNumber,yyMMddLOT4; da co golden test theo trace may in goc.
echo - Bang Test da gop mau vao mot cot Mau, bo #1..#4 va dung mau
echo   dong ho/thieu ket noi theo giao dien Htdrv goc.
echo - Bang day dung chu 20 dam ro; cot Mau da thu hep theo Htdrv goc.
echo - Mau kep chia 50/50, giu dung thu tu trong THT va chu luon de doc.
echo - Xac nhan FAIL chi pulse relay mo JIG da thu/chon: Relay 1 hoac 2.
echo - Mot tuy chon kieu dau: R2 MARK-R1 JIG hoac R1 MARK-R2 JIG.
echo - Chua kiem tra bang may in va JIG vat ly.
echo.

echo [3] FILE QUAN TRONG
echo ------------------------------------------------------------
echo - BUILD_ONE_FILE.cmd
echo - Scripts\Resolve-BuildVersion.ps1
echo - Views\HistoryPage.xaml
echo - Views\HistoryPage.xaml.cs
echo - Models\HistoryModels.cs
echo - Services\TestHistoryStore.cs
echo - Services\HistoryExportService.cs
echo - Services\ProgramIdentityService.cs
echo - ViewModels\TestViewModel.cs
echo - Tests\Program.cs
echo - docs\tasks\LICH SU VA XUAT CSV DUNG FORMAT ALL.csv GOC.txt
echo.

echo [4] LENH TIEP TUC TAI MAY NHA
echo ------------------------------------------------------------
echo git switch main
echo git pull origin main
echo TIEP_TUC_PHIEN_LAM_VIEC.cmd
echo.
echo Sau khi sua code, build bang:
echo BUILD_ONE_FILE.cmd
echo.

echo [5] TRANG THAI GIT HIEN TAI
echo ------------------------------------------------------------
git status -sb
echo.
git log -5 --oneline --decorate
echo.
echo ============================================================
echo Mo file CMD nay bang Notepad neu can doc lai toan bo ban giao.
echo ============================================================
pause

popd >nul
exit /b 0
