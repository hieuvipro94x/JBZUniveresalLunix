param(
    [string]$Version = ""
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root "Version.props"
$assemblyInfoPath = Join-Path $root "Properties\AssemblyInfo.cs"

if (-not (Test-Path -LiteralPath $propsPath)) {
    throw "Version.props not found: $propsPath"
}

[xml]$xml = Get-Content -LiteralPath $propsPath -Raw
$group = $xml.Project.PropertyGroup | Select-Object -First 1
$currentText = [string]$group.Version
$current = [Version]::Parse($currentText)

if ([string]::IsNullOrWhiteSpace($Version)) {
    # Mac dinh: moi lan sua/phat hanh tang 1 minor release.
    $next = New-Object Version($current.Major, ($current.Minor + 1), 0)
}
else {
    $next = [Version]::Parse($Version)
    if ($next.Build -lt 0) {
        $next = New-Object Version($next.Major, $next.Minor, 0)
    }
}

$release = "{0}.{1}.{2}" -f $next.Major, $next.Minor, $next.Build
$fileTag = $release.Replace('.', '_')
$assemblyVersion = $release + ".0"

if ($release -eq $currentText) {
    throw "New version must be different from current version $currentText."
}

$group.VersionPrefix = $release
$group.Version = $release
$group.AssemblyVersion = $assemblyVersion
$group.FileVersion = $assemblyVersion
$group.InformationalVersion = $release
$group.VersionFileTag = $fileTag
$group.AssemblyTitle = "JBZUniveresalLunix V" + $release

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.IndentChars = "  "
$settings.NewLineChars = "`r`n"
$settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$writer = [System.Xml.XmlWriter]::Create($propsPath, $settings)
$xml.Save($writer)
$writer.Close()

if (Test-Path -LiteralPath $assemblyInfoPath) {
    $family = "V{0}.{1}" -f $next.Major, $next.Minor
    $content = Get-Content -LiteralPath $assemblyInfoPath -Raw
    $content = [regex]::Replace(
        $content,
        'AssemblyMetadata\("ReleaseFamily",\s*"[^"]+"\)',
        ('AssemblyMetadata("ReleaseFamily", "' + $family + '")'))
    [System.IO.File]::WriteAllText(
        $assemblyInfoPath,
        $content,
        (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host "============================================================" -ForegroundColor Green
Write-Host ("VERSION UPDATED: V" + $currentText + " -> V" + $release) -ForegroundColor Green
Write-Host ("AssemblyVersion : " + $assemblyVersion) -ForegroundColor Green
Write-Host ("FileVersion     : " + $assemblyVersion) -ForegroundColor Green
Write-Host ("EXE name        : JBZUniveresalLunix_V" + $fileTag + ".exe") -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
