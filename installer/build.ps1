<#
.SYNOPSIS
    Publishes AntarusPoFinder.App as a self-contained single-file exe and builds the MSI installer
    around it. Run from anywhere; paths are resolved relative to this script.

.EXAMPLE
    powershell -File installer/build.ps1 -Version 1.8.0
    powershell -File installer/build.ps1   # reads <Version> from AntarusPoFinder.App.csproj
#>
param(
    [string]$Version,
    # БЕТА СТАВИТСЯ ОТДЕЛЬНЫМ ПРИЛОЖЕНИЕМ, рядом со стабильным: свой UpgradeCode (иначе MSI снёс бы
    # рабочую программу), своя папка, свой ярлык и свой профиль в %LocalAppData%. Прямая просьба:
    # «чтобы если там что-то не то, то работники смогли бы пользоваться предыдущей версией без
    # необходимости переустановки». Конфигурация сборки Beta задаёт константу BETA — по ней
    # приложение берёт своё имя, свой профиль и СВОЮ ленту релизов (теги beta-v…).
    [switch]$Beta
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $root "installer"
$publishDir = Join-Path $installerDir "publish"
$appProject = Join-Path $root "AntarusPoFinder.App\AntarusPoFinder.App.csproj"
$isolatedSmLogixPayloadDir = Join-Path $installerDir "loader\SMLogix isolated payload"

# -Version used to be mandatory and entirely unrelated to <Version> in AntarusPoFinder.App.csproj —
# easy to rebuild with a version that doesn't match what the exe itself reports (About/update-check
# both read the csproj's AssemblyVersion), which is the suspected cause of one reported "installer
# doesn't see the old version" case. Falls back to reading the csproj directly so the two can't drift
# apart just because nobody remembered to pass a matching -Version by hand; still overridable for a
# deliberate one-off (e.g. building an installer for a version not yet committed to the csproj).
$configuration = if ($Beta) { "Beta" } else { "Release" }
$productName   = if ($Beta) { "Antarus ПО Finder (бета)" } else { "Antarus ПО Finder" }
# Второй UpgradeCode — ровно то, что делает бету отдельным продуктом для Windows Installer.
$upgradeGuid   = if ($Beta) { "b17a4c20-9f3e-4a51-8c6d-2e5f7a1b4d90" } else { "6e74f83e-15ca-46d2-bf18-e927ba2635ad" }
$appDirName    = if ($Beta) { "AntarusPoFinderBeta" } else { "AntarusPoFinder" }
$appDataName   = if ($Beta) { "AntarusPOFinderBeta" } else { "AntarusPOFinder" }
$regKeyName    = if ($Beta) { "AntarusPoFinderBeta" } else { "AntarusPoFinder" }
$namePrefix    = if ($Beta) { "AntarusPoFinderBeta" } else { "AntarusPoFinder" }

if (-not $Version) {
    $csprojContent = Get-Content $appProject -Raw
    # У беты свой <Version> внутри PropertyGroup с Condition Beta — берём ПОСЛЕДНЕЕ совпадение,
    # иначе бета собралась бы под номером стабильной и автообновление беты никогда бы не сработало.
    $versions = [regex]::Matches($csprojContent, '<Version>([^<]+)</Version>')
    if ($versions.Count -eq 0) {
        throw "Could not find <Version> in $appProject - pass -Version explicitly."
    }
    $Version = if ($Beta) { $versions[$versions.Count - 1].Groups[1].Value } else { $versions[0].Groups[1].Value }
    Write-Host "No -Version passed - using $Version from AntarusPoFinder.App.csproj."
}

Write-Host "Publishing AntarusPoFinder.App $Version (self-contained win-x64, single file)..."
dotnet publish $appProject -c $configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Copy the portable exe out to installer/ under its release-facing name BEFORE the MSI step's
# `finally` wipes $publishDir — previously nothing did this at all, so a build.ps1 run only ever
# produced the MSI; the portable exe silently never made it out of the temporary publish folder,
# and both release artifacts were only present in past releases because someone copied the exe out
# by hand after the fact. AppUpdateService/the update banner specifically expect the exe under this
# exact "AntarusPoFinder-{version}.exe" name, not the plain "AntarusPoFinder.App.exe" publish uses.
$exeName = "$namePrefix-$Version.exe"
$exePath = Join-Path $installerDir $exeName
$publishedExe = if ($Beta) { "AntarusPoFinder.Beta.exe" } else { "AntarusPoFinder.App.exe" }
Copy-Item (Join-Path $publishDir $publishedExe) $exePath -Force

# Фикс подлинности обновлений (см. AntarusPoFinder.App/AppUpdateService.cs): файл с голым hex-хешем
# SHA256, рядом с exe, под именем "<exe>.sha256" — попадает в тот же релиз ассетом (gh release
# create просто подхватит его вместе с exe/msi, если передать явно), AppUpdateService при следующем
# обновлении скачивает его и сверяет с хешем реально скачанного файла ПЕРЕД установкой. Голый хеш
# (без имени файла после него) — самый простой формат, который однозначно разбирает и sha256sum-
# совместимый инструмент, и наш собственный парсер (ParseSha256Text берёт первый токен).
$sha256Hash = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash.ToLower()
$sha256Path = "$exePath.sha256"
Set-Content -Path $sha256Path -Value $sha256Hash -NoNewline -Encoding ascii

$msiName = "$namePrefix-$Version-setup.msi"
$msiPath = Join-Path $installerDir $msiName

Write-Host "Building MSI installer $msiName..."
$createdIsolatedPayloadPlaceholder = $false
if (-not (Test-Path -LiteralPath $isolatedSmLogixPayloadDir)) {
    # WiX 5 resolves the excluded directory before harvesting and expects it to exist.
    New-Item -ItemType Directory -Path $isolatedSmLogixPayloadDir | Out-Null
    $createdIsolatedPayloadPlaceholder = $true
}

Push-Location $installerDir
try {
    wix build Package.wxs -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
        -d ProductVersion=$Version -d ProductName="$productName" -d UpgradeGuid=$upgradeGuid `
        -d AppDirName=$appDirName -d AppDataDirName=$appDataName -d RegistryKeyName=$regKeyName `
        -d PublishedExeName=$publishedExe -culture ru-RU -o $msiName
    if ($LASTEXITCODE -ne 0) { throw "wix build failed." }
} finally {
    Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $installerDir "$msiName".Replace(".msi", ".wixpdb")) -ErrorAction SilentlyContinue
    if ($createdIsolatedPayloadPlaceholder) {
        Remove-Item -LiteralPath $isolatedSmLogixPayloadDir -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
}

Write-Host "Done: $exePath"
Write-Host "Done: $sha256Path"
Write-Host "Done: $msiPath"
