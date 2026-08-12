<#
.SYNOPSIS
    Кладёт актуальную версию Antarus ПО Finder в общую папку обновлений на сетевом диске.

.DESCRIPTION
    Приложение умеет брать обновление из общей папки, а если её нет — с GitHub
    (UpdateFolderResolver + AppUpdateService). Но КЛАСТЬ туда свежий релиз до сих пор приходилось
    руками после каждого выпуска, и ровно это регулярно забывалось: на GitHub лежит новая версия, в
    общей папке — прошлая, и коллеги без доступа наружу сидят на старой.

    Скрипт закрывает эту дыру и рассчитан на запуск при входе в систему: сетевой диск в этот момент
    может быть ещё не подключён, поэтому недоступная папка — ШТАТНЫЙ исход, а не ошибка.

    Что делает:
      1. молча выходит, если общая папка недоступна (диск не подключён, нет прав, нет сети);
      2. смотрит, какая версия уже лежит в папке;
      3. смотрит, какая версия последняя на GitHub;
      4. если на GitHub новее — скачивает exe/MSI/контрольную сумму, СВЕРЯЕТ хеш и кладёт в папку;
      5. если рядом лежит свежесобранный релиз (installer\), а он новее — берёт его и не ходит в сеть.

    Файл появляется в папке одним движением: сначала пишется под временным именем, потом
    переименовывается. Иначе коллега, поймавший момент копирования 180-мегабайтного exe, скачал бы
    половину файла и получил бы «повреждённый установщик».

.PARAMETER Share
    Папка обновлений на общем диске. По умолчанию — путь предприятия.

.PARAMETER Force
    Положить заново, даже если версия совпадает.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish-to-share.ps1
    powershell -ExecutionPolicy Bypass -File tools\publish-to-share.ps1 -Share "\\server\share\Antarus" -Force
#>
param(
    [string]$Share = "Z:\Software\Antarus Finder\Конфиг",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repo = "IlyaRevkin/antarus-po-finder-dotnet"
$root = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $root "installer"
$logPath = Join-Path $env:LOCALAPPDATA "AntarusPoFinder\publish-to-share.log"

function Write-Log([string]$message) {
    $line = "{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $message
    Write-Host $line
    try {
        $dir = Split-Path -Parent $logPath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -Path $logPath -Value $line -Encoding utf8
    } catch { }
}

# Версия из имени файла «AntarusPoFinder-1.68.0.exe». Возвращает $null, если имя не наше.
function Get-VersionFromName([string]$name) {
    if ($name -match 'AntarusPoFinder-(\d+\.\d+\.\d+)(-setup)?\.(exe|msi)$') { return [version]$Matches[1] }
    return $null
}

function Get-NewestVersionIn([string]$folder) {
    $best = $null
    Get-ChildItem -Path $folder -Filter "AntarusPoFinder-*.exe" -File -ErrorAction SilentlyContinue | ForEach-Object {
        $v = Get-VersionFromName $_.Name
        if ($v -and (-not $best -or $v -gt $best)) { $best = $v }
    }
    return $best
}

# Копирование через временное имя: см. описание сверху про «половину файла».
function Copy-Atomic([string]$source, [string]$targetDir) {
    $name = Split-Path -Leaf $source
    $final = Join-Path $targetDir $name
    $temp = "$final.part"
    Copy-Item -LiteralPath $source -Destination $temp -Force
    if (Test-Path -LiteralPath $final) { Remove-Item -LiteralPath $final -Force }
    Rename-Item -LiteralPath $temp -NewName $name
    Write-Log "положено: $name"
}

# ── 1. Папка доступна? ────────────────────────────────────────────────────────
# Именно так, а не Test-Path: на отключённом сетевом диске Test-Path висит до таймаута SMB.
$shareReady = $false
try {
    $shareReady = [System.IO.Directory]::Exists($Share)
} catch { $shareReady = $false }

if (-not $shareReady) {
    Write-Log "папка обновлений недоступна ($Share) — выходим, это нормально при запуске до подключения диска"
    exit 0
}

$onShare = Get-NewestVersionIn $Share
Write-Log ("в папке сейчас: " + $(if ($onShare) { $onShare } else { "ничего нашего нет" }))

# ── 2. Что есть локально после сборки ────────────────────────────────────────
$local = Get-NewestVersionIn $installerDir
if ($local) { Write-Log "рядом собран: $local" }

# ── 3. Что на GitHub ─────────────────────────────────────────────────────────
$remote = $null
$ghAvailable = $null -ne (Get-Command gh -ErrorAction SilentlyContinue)
if ($ghAvailable) {
    try {
        $tag = (gh release view --repo $repo --json tagName --jq .tagName 2>$null)
        if ($tag -match 'v?(\d+\.\d+\.\d+)') { $remote = [version]$Matches[1] }
    } catch { }
}
if ($remote) { Write-Log "на GitHub: $remote" }
elseif (-not $ghAvailable) { Write-Log "gh не установлен — GitHub не спрашиваем, работаем тем, что собрано рядом" }
else { Write-Log "GitHub не ответил — работаем тем, что собрано рядом" }

# ── 4. Кто новее ─────────────────────────────────────────────────────────────
$candidates = @($local, $remote) | Where-Object { $_ }
if (-not $candidates) {
    Write-Log "брать нечего: ни собранного релиза рядом, ни ответа GitHub"
    exit 0
}
$newest = ($candidates | Sort-Object -Descending)[0]

if ($onShare -and $newest -le $onShare -and -not $Force) {
    Write-Log "в папке уже $onShare — новее ничего нет, ничего не делаем"
    exit 0
}

Write-Log "кладём $newest"

# Локальная сборка предпочтительнее скачивания: файлы уже здесь и это ровно та же версия.
$useLocal = $local -and $local -eq $newest
$sourceDir = $installerDir

if (-not $useLocal) {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("antarus-release-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    Write-Log "скачиваем с GitHub во временную папку"
    gh release download "v$newest" --repo $repo --dir $tempDir --pattern "AntarusPoFinder-*" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Log "скачать не удалось"; exit 1 }
    $sourceDir = $tempDir
}

# ── 5. Сверяем хеш ДО того, как класть на общий диск ─────────────────────────
# Битый файл в общей папке хуже отсутствующего: приложение возьмёт его как обновление.
$exe = Join-Path $sourceDir "AntarusPoFinder-$newest.exe"
$shaFile = "$exe.sha256"
if ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $shaFile)) {
    $expected = (Get-Content -LiteralPath $shaFile -Raw).Trim().ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) {
        Write-Log "контрольная сумма НЕ СОШЛАСЬ — на общий диск ничего не кладём"
        exit 1
    }
    Write-Log "контрольная сумма сошлась"
} elseif (-not (Test-Path -LiteralPath $exe)) {
    Write-Log "файла $exe нет — класть нечего"
    exit 1
} else {
    Write-Log "файла с контрольной суммой нет — кладём как есть"
}

# ── 6. Кладём ────────────────────────────────────────────────────────────────
try {
    foreach ($pattern in @("AntarusPoFinder-$newest.exe", "AntarusPoFinder-$newest.exe.sha256", "AntarusPoFinder-$newest-setup.msi")) {
        $file = Join-Path $sourceDir $pattern
        if (Test-Path -LiteralPath $file) { Copy-Atomic $file $Share }
        else { Write-Log "нет файла $pattern — пропускаем" }
    }
    Write-Log "готово: в папке обновлений теперь $newest"
} catch {
    Write-Log "не удалось положить: $($_.Exception.Message)"
    exit 1
} finally {
    if (-not $useLocal -and (Test-Path -LiteralPath $sourceDir)) {
        Remove-Item -LiteralPath $sourceDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
