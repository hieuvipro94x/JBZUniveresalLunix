param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$failed = 0

function Check([string]$name, [bool]$ok) {
    if ($ok) { Write-Host "[PASS] $name" -ForegroundColor Green }
    else { Write-Host "[FAIL] $name" -ForegroundColor Red; $script:failed++ }
}

$version = Get-Content (Join-Path $ProjectRoot 'Version.props') -Raw -Encoding UTF8
$engine = Get-Content (Join-Path $ProjectRoot 'Services\TestEngine.cs') -Raw -Encoding UTF8
$testVm = Get-Content (Join-Path $ProjectRoot 'ViewModels\TestViewModel.cs') -Raw -Encoding UTF8
$history = Get-Content (Join-Path $ProjectRoot 'Services\HistoryExportService.cs') -Raw -Encoding UTF8
$settingsXaml = Get-Content (Join-Path $ProjectRoot 'Views\ProductionSettingsPage.xaml') -Raw -Encoding UTF8

Check 'Version 15.2.0' ($version -match '<Version>15\.2\.0</Version>')
Check 'Version tag 15_2_0' ($version -match '<VersionFileTag>15_2_0</VersionFileTag>')
Check 'Relay safe pulse finally' ($engine -match 'PulseRelaySafeAsync' -and $engine -match 'finally')
Check 'FAIL eject only calls R1 helper' ($engine -match 'EjectFaultProductAsync[\s\S]*?=> PulseJigRelayAsync')
Check 'PASS R2 before R1' ($engine.IndexOf('await PulseMarkingRelayAsync') -lt $engine.IndexOf('await PulseJigRelayAsync'))
Check 'Result side effects use interlocked gate' ($testVm -match 'Interlocked\.CompareExchange\(ref _resultRecordedThisCycle, 1, 0\)')
Check 'TestEngine does not subscribe board frames' ($engine -notmatch 'FrameReceived\s*\+=')
Check 'History uses one column definition' ($history -match 'HistoryColumn\[\] Columns' -and $history -notmatch 'string\[\] Headers')
Check 'XLSX native DateTime/number output' ($history -match 'ToOADate\(\)' -and $history -match 'HistoryCellType\.Number')
Check 'Unsupported settings hidden from UI' (
    $settingsXaml -notmatch 'Settings\.(WaterproofSerialPort|TemperatureTolerance|OversizeWaitSeconds|ShieldDelay)')
Check 'Self-test project exists' (Test-Path (Join-Path $ProjectRoot 'Tests\JBZUniveresalLunix.SelfTests.csproj'))

$xamlOk = $true
Get-ChildItem (Join-Path $ProjectRoot 'Views') -Filter '*.xaml' -File | ForEach-Object {
    try { [xml](Get-Content $_.FullName -Raw -Encoding UTF8) | Out-Null }
    catch { $xamlOk = $false; Write-Host "XAML parse error: $($_.FullName): $($_.Exception.Message)" }
}
Check 'All Views XAML parse as XML' $xamlOk

if ($failed -gt 0) { exit 1 }
Write-Host 'V15.2.0 static validation PASS.' -ForegroundColor Cyan
