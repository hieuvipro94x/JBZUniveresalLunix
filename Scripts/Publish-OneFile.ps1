param(
    [ValidateSet("win-x86", "win-x64")]
    [string]$Runtime = "win-x86",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputFolder = "PublishSingle",

    [switch]$NoOpenOutput
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)

    Write-Host ""
    Write-Host ("==> " + $Message) -ForegroundColor Cyan
}

function Find-Project {
    param([string]$Root)

    $projects = @(
        Get-ChildItem -Path $Root -Filter "*.csproj" -File -Recurse |
        Where-Object {
            $_.FullName -notmatch "\\(bin|obj|PublishSingle|PublishSmall|publish)\\"
        }
    )

    if ($projects.Count -eq 0) {
        throw ("Cannot find a .csproj file under: " + $Root)
    }

    $preferred = @(
        $projects | Where-Object {
            $_.Name -ieq "JBZUniveresalLunix.csproj"
        }
    )

    if ($preferred.Count -eq 1) {
        return $preferred[0]
    }

    if ($projects.Count -eq 1) {
        return $projects[0]
    }

    $projectList = ($projects | ForEach-Object {
        " - " + $_.FullName
    }) -join [Environment]::NewLine

    throw ("More than one .csproj file was found:" +
           [Environment]::NewLine +
           $projectList)
}

try {
    $root = Split-Path -Parent $PSScriptRoot
    $project = Find-Project -Root $root

    $projectDir = $project.Directory.FullName

    # V12.9: Version.props la nguon version duy nhat cho build/publish.
    $versionPropsPath = Join-Path $projectDir "Version.props"
    if (-not (Test-Path -LiteralPath $versionPropsPath)) {
        throw ("Version.props was not found: " + $versionPropsPath)
    }

    [xml]$versionXml = Get-Content -LiteralPath $versionPropsPath -Raw
    $versionGroup = $versionXml.Project.PropertyGroup | Select-Object -First 1
    $productVersion = [string]$versionGroup.Version
    $fileVersion = [string]$versionGroup.FileVersion
    $assemblyVersion = [string]$versionGroup.AssemblyVersion
    $versionFileTag = [string]$versionGroup.VersionFileTag

    if ([string]::IsNullOrWhiteSpace($productVersion) -or
        [string]::IsNullOrWhiteSpace($versionFileTag)) {
        throw "Version.props does not contain Version/VersionFileTag."
    }

    # Runtime V16 uses one stable executable name. Version remains in file
    # metadata and in the versioned publish directory.
    $appName = "JBZUniveresalLunix"
    $publishRoot = Join-Path $projectDir $OutputFolder
    $publishDir = Join-Path $publishRoot ("V" + $productVersion)
    $logPath = Join-Path $projectDir ("publish_V" + $productVersion + ".log")

    Write-Host ("Project          : " + $project.FullName)
    Write-Host ("Version          : " + $productVersion)
    Write-Host ("AssemblyVersion  : " + $assemblyVersion)
    Write-Host ("FileVersion      : " + $fileVersion)
    Write-Host ("EXE              : " + $appName + ".exe")
    Write-Host ("Runtime          : " + $Runtime)
    Write-Host ("Mode             : " + $Configuration)
    Write-Host ("Output           : " + $publishDir)

    Write-Step "Checking .NET SDK"
    & dotnet --version

    if ($LASTEXITCODE -ne 0) {
        throw "The .NET SDK was not found."
    }

    Write-Step "Stopping the running application"
    Get-Process -Name $appName -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Step "Removing the current publish output"

    # Khong xoa bin/obj: Visual Studio XAML Designer dang dung output Debug trong
    # cac thu muc nay. Xoa chung khi Designer dang mo co the lam shadow-copy bi loi.
    # dotnet publish tu cap nhat output Release; chi can lam sach thu muc publish hien tai.
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    if (Test-Path -LiteralPath $logPath) {
        Remove-Item -LiteralPath $logPath -Force
    }

    Write-Step "Restoring NuGet packages"

    & dotnet restore $project.FullName `
        -r $Runtime `
        --nologo 2>&1 |
        Tee-Object -FilePath $logPath -Append

    if ($LASTEXITCODE -ne 0) {
        throw ("Restore failed. See: " + $logPath)
    }

    Write-Step "Publishing one framework-dependent ReadyToRun EXE"

    $publishArguments = @(
        "publish"
        $project.FullName
        "-c"
        $Configuration
        "-r"
        $Runtime
        "--self-contained"
        "false"
        "--nologo"
        "-p:PublishSingleFile=true"
        "-p:SelfContained=false"
        "-p:PublishSelfContained=false"
        "-p:UseAppHost=true"
        "-p:EnableCompressionInSingleFile=false"
        "-p:IncludeNativeLibrariesForSelfExtract=true"
        "-p:PublishReadyToRun=true"
        "-p:PublishTrimmed=false"
        "-p:DebugType=None"
        "-p:DebugSymbols=false"
        "-p:GenerateDocumentationFile=false"
        "-o"
        $publishDir
    )

    & dotnet @publishArguments 2>&1 |
        Tee-Object -FilePath $logPath -Append

    if ($LASTEXITCODE -ne 0) {
        throw ("Publish failed. See: " + $logPath)
    }

    $exePath = Join-Path $publishDir ($appName + ".exe")

    if (-not (Test-Path -LiteralPath $exePath)) {
        throw ("Publish completed, but the EXE was not found: " + $exePath)
    }

    $allPublishedFiles = @(
        Get-ChildItem -LiteralPath $publishDir -File
    )

    $extraFiles = @(
        $allPublishedFiles | Where-Object {
            $_.FullName -ne $exePath
        }
    )

    if ($extraFiles.Count -gt 0) {
        Write-Host ""
        Write-Host "WARNING: The publish folder contains extra files:" -ForegroundColor Yellow

        foreach ($file in $extraFiles) {
            Write-Host (" - " + $file.Name) -ForegroundColor Yellow
        }

        Write-Host ""
        Write-Host "Check CopyToOutputDirectory and CopyToPublishDirectory." -ForegroundColor Yellow
        Write-Host "Runtime configuration JSON should be created by the app." -ForegroundColor Yellow
    }

    $exeInfo = Get-Item -LiteralPath $exePath
    $sizeMb = [Math]::Round($exeInfo.Length / 1MB, 2)

    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host "PUBLISH SUCCESS" -ForegroundColor Green
    Write-Host ("File : " + $exePath) -ForegroundColor Green
    Write-Host ("Size : " + $sizeMb + " MB") -ForegroundColor Green
    Write-Host ("Log  : " + $logPath) -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Green

    if (-not $NoOpenOutput) {
        Start-Process -FilePath "explorer.exe" -ArgumentList $publishDir
    }

    exit 0
}
catch {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host "PUBLISH FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "============================================================" -ForegroundColor Red

    exit 1
}
