<#
.SYNOPSIS
    Builds the distributable RecallOS app and zips it.

.DESCRIPTION
    Produces a self-contained win-x64 build: the user does not need the .NET runtime
    installed, they unzip the folder and run RecallOS.exe.

    Single-file publishing is deliberately NOT used. Tesseract's interop loader resolves its
    native libraries relative to Assembly.Location, which is empty inside a single-file
    bundle; it then throws before any fallback and OCR never initialises. A folder layout
    keeps the native x64 libraries where that loader can find them.

.PARAMETER Version
    Version stamped into the assembly and the archive name.

.PARAMETER Runtime
    Target runtime identifier. win-x64 is what the released archive targets.
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0',
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts "RecallOS-$Version-$Runtime"
$archive = Join-Path $artifacts "RecallOS-$Version-$Runtime.zip"

Write-Host "RecallOS $Version  ($Runtime, $Configuration)" -ForegroundColor Cyan

# A stale staging folder would leave files from a previous build inside the archive.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $archive) { Remove-Item $archive -Force }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

Write-Host "`n[1/4] Running tests" -ForegroundColor Yellow
dotnet test (Join-Path $root 'tests\RecallOS.Core.Tests\RecallOS.Core.Tests.csproj') `
    -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed; not publishing." }

Write-Host "`n[2/4] Publishing" -ForegroundColor Yellow
dotnet publish (Join-Path $root 'src\RecallOS.App\RecallOS.App.csproj') `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en `
    -o $stage `
    --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Write-Host "`n[3/4] Verifying payload" -ForegroundColor Yellow

$exe = Join-Path $stage 'RecallOS.exe'
if (-not (Test-Path $exe)) { throw "RecallOS.exe is missing from the published output." }

# The native Tesseract and Leptonica libraries are what make OCR work at all, and they are
# the part most likely to go missing in a packaging change. Fail loudly rather than ship an
# app whose text search silently never works.
$native = Join-Path $stage "x64"
if (-not (Test-Path $native)) {
    throw "Native Tesseract libraries are missing ($native). OCR would not initialise."
}

Write-Host "  RecallOS.exe          $('{0:N1} MB' -f ((Get-Item $exe).Length / 1MB))"
Write-Host "  native libraries      $((Get-ChildItem $native -File).Count) files"
Write-Host "  total                 $('{0:N0} MB' -f ((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))"

Write-Host "`n[4/4] Creating archive" -ForegroundColor Yellow
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal

Write-Host "`nDone." -ForegroundColor Green
Write-Host "  $archive  ($('{0:N0} MB' -f ((Get-Item $archive).Length / 1MB)))"
