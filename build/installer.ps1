<#
.SYNOPSIS
    Builds the one-click RecallOS installer.

.DESCRIPTION
    Produces RecallOS-win-Setup.exe: a per-user installer that needs no administrator
    rights, creates Start Menu and Desktop shortcuts, registers an uninstaller in Add or
    Remove Programs, and launches the app when it finishes.

    IMPORTANT — the pack id is deliberately NOT "RecallOS".

    Velopack installs to %LOCALAPPDATA%\<packId>, and RecallOS keeps its database, frames
    and language packs in %LOCALAPPDATA%\RecallOS. Using the same id makes the installer
    treat the user's recordings as its own install directory and delete them. The id below
    keeps the two apart; it is never shown to the user, who only ever sees the title.

.PARAMETER Version
    Version stamped into the installer and the update feed.
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0',
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$stage     = Join-Path $artifacts 'stage'
$output    = Join-Path $artifacts 'installer'

# Must differ from the data folder name. See the note above.
$packId = 'RecallOSApp'

Write-Host "RecallOS $Version installer  ($Runtime)" -ForegroundColor Cyan

if (Test-Path $stage)  { Remove-Item $stage -Recurse -Force }
if (Test-Path $output) { Remove-Item $output -Recurse -Force }

Write-Host "`n[1/4] Running tests" -ForegroundColor Yellow
dotnet test (Join-Path $root 'tests\RecallOS.Core.Tests\RecallOS.Core.Tests.csproj') `
    -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed; not packaging." }

Write-Host "`n[2/4] Publishing" -ForegroundColor Yellow
dotnet publish (Join-Path $root 'src\RecallOS.App\RecallOS.App.csproj') `
    -c $Configuration -r $Runtime --self-contained true `
    -p:Version=$Version -p:DebugType=none -p:SatelliteResourceLanguages=en `
    -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# Native Tesseract libraries are the part most likely to vanish in a packaging change, and
# their absence would ship an app whose text search silently never works.
if (-not (Test-Path (Join-Path $stage 'x64'))) {
    throw "Native Tesseract libraries are missing from the publish output."
}

Write-Host "`n[3/4] Packaging installer" -ForegroundColor Yellow
$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
if (-not (Test-Path $vpk)) { $vpk = 'vpk' }

& $vpk pack `
    --packId $packId `
    --packVersion $Version `
    --packDir $stage `
    --mainExe 'RecallOS.exe' `
    --packTitle 'RecallOS' `
    --packAuthors 'Ansh Patel' `
    --outputDir $output
if ($LASTEXITCODE -ne 0) { throw "Packaging failed." }

Write-Host "`n[4/4] Verifying and renaming" -ForegroundColor Yellow

# Velopack names its output after the pack id. The pack id is an internal identifier, so
# the files people actually download are renamed to the product name.
$built = Join-Path $output "$packId-win-Setup.exe"
if (-not (Test-Path $built)) { throw "Setup executable was not produced." }

$setup = Join-Path $output 'RecallOS-Setup.exe'
Move-Item $built $setup -Force

$builtPortable = Join-Path $output "$packId-win-Portable.zip"
if (Test-Path $builtPortable) {
    Move-Item $builtPortable (Join-Path $output 'RecallOS-Portable.zip') -Force
}

Write-Host "`nDone." -ForegroundColor Green
Get-ChildItem $output | ForEach-Object { Write-Host ("  {0,-38} {1,8:N1} MB" -f $_.Name, ($_.Length/1MB)) }
