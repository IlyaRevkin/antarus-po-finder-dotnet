<#
.SYNOPSIS
    Publishes AntarusPoFinder.App as a self-contained single-file exe and builds the MSI installer
    around it. Run from anywhere; paths are resolved relative to this script.

.EXAMPLE
    powershell -File installer/build.ps1 -Version 1.8.0
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $root "installer"
$publishDir = Join-Path $installerDir "publish"
$appProject = Join-Path $root "AntarusPoFinder.App\AntarusPoFinder.App.csproj"

Write-Host "Publishing AntarusPoFinder.App $Version (self-contained win-x64, single file)..."
dotnet publish $appProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

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

Write-Host "Done: $msiPath"
