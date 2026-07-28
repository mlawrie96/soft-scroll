<#
.SYNOPSIS
Builds, publishes, and atomically deploys Soft Scroll, then relaunches it.

.DESCRIPTION
Matches this repo's official CI publish profile (.github/workflows/auto-release.yml).

This machine's NuGet package sources are deliberately empty (an IT/corporate
policy control, not a bug) so restore/publish pass an explicit one-off
source instead of touching the machine's NuGet.Config.

The final install step is an atomic same-volume rename (Move-Item), not an
in-place overwrite. If this script is interrupted (crash, power loss) at any
point before the rename, the previously-installed exe is left fully intact
rather than possibly corrupted mid-write.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$distExe = Join-Path $repoRoot "dist\SoftScroll.exe"
$installDir = "$env:LOCALAPPDATA\SoftScroll"
$installExe = Join-Path $installDir "SoftScroll.exe"
$tempExe = Join-Path $installDir "SoftScroll.exe.new"
$nugetSource = "https://api.nuget.org/v3/index.json"

Push-Location $repoRoot
try {
    Write-Output "==> dotnet build"
    dotnet build --no-restore --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

    Write-Output "==> dotnet publish"
    dotnet publish -c Release -r win-x64 `
        -p:PublishSingleFile=true -p:SelfContained=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false `
        --source $nugetSource -o ./dist
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

if (-not (Test-Path $distExe)) {
    throw "Build output not found at $distExe"
}

New-Item -ItemType Directory -Force -Path $installDir | Out-Null

Write-Output "==> Staging new build (installed exe untouched so far)"
Copy-Item -Path $distExe -Destination $tempExe -Force

Write-Output "==> Stopping running instance"
Stop-Process -Name SoftScroll -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Output "==> Installing atomically (same-volume rename, not an in-place overwrite)"
Move-Item -Path $tempExe -Destination $installExe -Force

Write-Output "==> Relaunching"
Start-Process -FilePath $installExe
Start-Sleep -Seconds 2

$proc = Get-Process -Name SoftScroll -ErrorAction SilentlyContinue
if ($proc) {
    Write-Output "Deployed and running: PID $($proc.Id), started $($proc.StartTime)"
} else {
    Write-Warning "SoftScroll does not appear to be running after deploy -- check logs at $env:APPDATA\SoftScroll\logs\"
}
