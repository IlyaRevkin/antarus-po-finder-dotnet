<#
.SYNOPSIS
    Publishes AntarusPoFinder.App as a self-contained single-file exe and builds the MSI installer
    around it. Run from anywhere; paths are resolved relative to this script.

.EXAMPLE
    powershell -File installer/build.ps1 -Version 1.8.0
    powershell -File installer/build.ps1   # reads <Version> from AntarusPoFinder.App.csproj
#>
param(
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $root "installer"
$publishDir = Join-Path $installerDir "publish"
$appProject = Join-Path $root "AntarusPoFinder.App\AntarusPoFinder.App.csproj"

# -Version used to be mandatory and entirely unrelated to <Version> in AntarusPoFinder.App.csproj —
# easy to rebuild with a version that doesn't match what the exe itself reports (About/update-check
# both read the csproj's AssemblyVersion), which is the suspected cause of one reported "installer
# doesn't see the old version" case. Falls back to reading the csproj directly so the two can't drift
# apart just because nobody remembered to pass a matching -Version by hand; still overridable for a
# deliberate one-off (e.g. building an installer for a version not yet committed to the csproj).
if (-not $Version) {
    $csprojContent = Get-Content $appProject -Raw
    if ($csprojContent -notmatch '<Version>([^<]+)</Version>') {
        throw "Could not find <Version> in $appProject - pass -Version explicitly."
    }
    $Version = $Matches[1]
    Write-Host "No -Version passed - using $Version from AntarusPoFinder.App.csproj."
}

Write-Host "Publishing AntarusPoFinder.App $Version (self-contained win-x64, single file)..."
dotnet publish $appProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Copy the portable exe out to installer/ under its release-facing name BEFORE the MSI step's
# `finally` wipes $publishDir — previously nothing did this at all, so a build.ps1 run only ever
# produced the MSI; the portable exe silently never made it out of the temporary publish folder,
# and both release artifacts were only present in past releases because someone copied the exe out
# by hand after the fact. AppUpdateService/the update banner specifically expect the exe under this
# exact "AntarusPoFinder-{version}.exe" name, not the plain "AntarusPoFinder.App.exe" publish uses.
$exeName = "AntarusPoFinder-$Version.exe"
$exePath = Join-Path $installerDir $exeName
Copy-Item (Join-Path $publishDir "AntarusPoFinder.App.exe") $exePath -Force

$msiName = "AntarusPoFinder-$Version-setup.msi"
$msiPath = Join-Path $installerDir $msiName

Write-Host "Building MSI installer $msiName..."
Push-Location $installerDir
try {
    wix build Package.wxs -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
        -d ProductVersion=$Version -culture ru-RU -o $msiName
    if ($LASTEXITCODE -ne 0) { throw "wix build failed." }
} finally {
    Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $installerDir "$msiName".Replace(".msi", ".wixpdb")) -ErrorAction SilentlyContinue
    Pop-Location
}

Write-Host "Done: $exePath"
Write-Host "Done: $msiPath"
