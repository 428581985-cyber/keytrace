<#
.SYNOPSIS
    Builds and packages a self-contained single-file Windows release zip for KeyTrace.

.DESCRIPTION
    Publishes src/KeyTrace/KeyTrace.csproj as a self-contained single-file executable
    (data/zh-hanzi.tsv and assets/*.ico are embedded resources inside keytrace.exe),
    stages keytrace.exe together with scripts/install-task.bat, README.md, LICENSE,
    and NOTICE, creates dist/KeyTrace-<Version>-<Runtime>.zip, and writes dist/SHA256SUMS.txt.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/package-release.ps1 -Version v0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = 'dev',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Version = $Version.Trim()
$Runtime = $Runtime.Trim()

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Version must not be empty.'
}
if ([string]::IsNullOrWhiteSpace($Runtime)) {
    throw 'Runtime must not be empty.'
}
if ($Version -match '[\\/:*?"<>|\s]') {
    throw "Version contains invalid characters: $Version"
}
if ($Runtime -match '[\\/:*?"<>|\s]') {
    throw "Runtime contains invalid characters: $Runtime"
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src\KeyTrace\KeyTrace.csproj'
$DistDir = if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    Join-Path $RepoRoot 'dist'
} else {
    $OutputDir
}

if (-not (Test-Path $ProjectPath)) {
    throw "Project file not found: $ProjectPath"
}

New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

$ZipName = "KeyTrace-$Version-$Runtime.zip"
$ZipPath = Join-Path $DistDir $ZipName
$WorkRoot = Join-Path $DistDir ("_work-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$PublishDir = Join-Path $WorkRoot 'publish'
$StageDir = Join-Path $WorkRoot 'stage'

New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

try {
    $publishArgs = @(
        'publish', $ProjectPath,
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-o', $PublishDir
    )

    if ($Version -match '^v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$') {
        $publishArgs += "-p:Version=$($Matches[1])"
    } else {
        $publishArgs += "-p:InformationalVersion=0.1.0-$Version"
    }

    Write-Host "[1/3] Publishing KeyTrace ($Version / $Runtime)..."
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    $publishedExe = Join-Path $PublishDir 'keytrace.exe'
    if (-not (Test-Path $publishedExe)) {
        throw "Expected executable not found after publish: $publishedExe"
    }

    Write-Host '[2/3] Staging release files...'
    Get-ChildItem -Path $PublishDir -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination (Join-Path $StageDir $_.Name) -Force
    }

    $stageScriptsDir = Join-Path $StageDir 'scripts'
    New-Item -ItemType Directory -Path $stageScriptsDir -Force | Out-Null

    $bundleFiles = @(
        @{ Src = 'scripts\install-task.bat'; Dst = 'scripts\install-task.bat' },
        @{ Src = 'README.md';                Dst = 'README.md' },
        @{ Src = 'LICENSE';                  Dst = 'LICENSE' },
        @{ Src = 'NOTICE';                   Dst = 'NOTICE' }
    )

    foreach ($item in $bundleFiles) {
        $srcPath = Join-Path $RepoRoot $item.Src
        if (-not (Test-Path $srcPath)) {
            throw "Required release file not found: $srcPath"
        }
        Copy-Item -Path $srcPath -Destination (Join-Path $StageDir $item.Dst) -Force
    }

    Write-Host "[3/3] Creating $ZipName and SHA256SUMS.txt..."
    if (Test-Path $ZipPath) {
        Remove-Item -Path $ZipPath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $StageDir,
        $ZipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    $zipFiles = Get-ChildItem -Path $DistDir -Filter 'KeyTrace-*.zip' -File | Sort-Object Name
    $sumLines = foreach ($file in $zipFiles) {
        $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($file.Name)"
    }

    $sumsPath = Join-Path $DistDir 'SHA256SUMS.txt'
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($sumsPath, (($sumLines -join "`n") + "`n"), $utf8NoBom)

    $zipSizeMB = [math]::Round((Get-Item $ZipPath).Length / 1MB, 2)
    Write-Host ''
    Write-Host "Packaged: $ZipPath ($zipSizeMB MB)"
    Write-Host "Checksums: $sumsPath"
    foreach ($line in $sumLines) {
        Write-Host "  $line"
    }
}
finally {
    if (Test-Path $WorkRoot) {
        Remove-Item -Path $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
