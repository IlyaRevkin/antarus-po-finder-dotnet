<#
.SYNOPSIS
    Инвентаризация «дофайндеровского» сетевого диска: что там лежит и как это ляжет в структуру ПО.

.DESCRIPTION
    Скрипт НИЧЕГО НЕ МЕНЯЕТ. Он обходит указанную папку, находит файлы прошивок и проекты панелей,
    пытается вычитать из пути и имени тип шкафа, подтип, контроллер, номер версии, номер заявки и
    заводской SN — и складывает всё это в отчёт (CSV + сводка на экран).

    Зачем именно так, а не «сразу разложить»: правила раскладки у Финдера жёсткие
    (ПО\<тип>\<подтип>\<контроллер>\<версия>\Прошивка\<версия>.psl), а старый диск собирался людьми
    и годами. Пока не видно, ЧТО там лежит и сколько из этого распознаётся автоматически, любой
    «переезд одной кнопкой» — это лотерея с чужими прошивками. Поэтому: сначала отчёт, потом глазами
    по нему решение, и только потом раскладка (для неё есть -Apply, см. ниже, но включать её стоит
    после того, как отчёт признан правильным).

    Что распознаётся:
      • тип шкафа       — НГР / ПЖ / ТГР (и их расшифровки) в любом сегменте пути или в имени файла;
      • контроллер      — SMH4 / SMH5 / KINCO / PIXEL2 / PIXEL;
      • номер версии    — 4–5 чисел через точку с необязательным штампом даты (2.1.0042.0007.20260422_1348);
      • заявка / SN     — «_(01312)», «_SN00042», «заявка 1312», «зав. № 42»;
      • дата сборки     — из имени файла, иначе время изменения файла.

.PARAMETER Source
    Папка со старым хламом (UNC или буква диска). Обходится рекурсивно.

.PARAMETER Report
    Куда положить CSV-отчёт. По умолчанию — рядом со скриптом, с датой в имени.

.PARAMETER Target
    Корень диска Финдера (тот, что в Настройки → Общие → «Сетевой диск»). Нужен только чтобы
    посчитать, КУДА лёг бы каждый файл. Без -Apply ничего туда не пишется.

.PARAMETER Apply
    Разложить распознанные файлы по структуре Финдера. КОПИРУЕТ (не перемещает), пропускает всё, что
    распозналось не полностью, и никогда не перезаписывает существующий файл. Требует -Target.

.EXAMPLE
    .\legacy-disk-inventory.ps1 -Source '\\server\share\Прошивки старые'

.EXAMPLE
    .\legacy-disk-inventory.ps1 -Source 'Z:\Старое' -Target 'Z:\Software\Antarus Finder' -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Report,
    [string]$Target,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Source)) { throw "Папка не найдена: $Source" }
if ($Apply -and -not $Target) { throw "-Apply без -Target: некуда раскладывать." }
if (-not $Report) {
    $stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
    $Report = Join-Path $PSScriptRoot "legacy-inventory_$stamp.csv"
}

# Расширения файлов прошивок. Проекты Кинко/Овен — это ПАПКИ, они ловятся отдельно ниже.
$FirmwareExt = @('.psl', '.lfs', '.pls')
$HmiExt      = @('.fsprj', '.pm3', '.hmi')

# Тип шкафа: ключ — что ищем в тексте, значение — как называется папка у Финдера.
$GroupWords = [ordered]@{
    'НГР' = 'НГР'; 'НАСОС' = 'НГР'
    'ПЖ'  = 'ПЖ';  'ПОЖАР' = 'ПЖ'
    'ТГР' = 'ТГР'; 'ТЕПЛ'  = 'ТГР'
}
$ControllerWords = [ordered]@{
    'SMH5' = 'SMH5'; 'SMH 5' = 'SMH5'; 'СМН5' = 'SMH5'
    'SMH4' = 'SMH4'; 'SMH 4' = 'SMH4'; 'СМН4' = 'SMH4'
    'KINCO' = 'KINCO'; 'КИНКО' = 'KINCO'
    'PIXEL2' = 'PIXEL2'; 'PIXEL 2' = 'PIXEL2'
    'PIXEL' = 'PIXEL'; 'ПИКСЕЛ' = 'PIXEL'
}

function Find-Word {
    param([string]$Text, [System.Collections.Specialized.OrderedDictionary]$Words)
    $upper = $Text.ToUpperInvariant()
    foreach ($key in $Words.Keys) {
        if ($upper.Contains($key)) { return $Words[$key] }
    }
    return ''
}

function Get-VersionRaw {
    <# Номер версии Финдера: 4–5 чисел через точку, необязательный штамп «.ГГГГММДД_ЧЧММ». Берём
       самое длинное совпадение — иначе «2.1» из «версия 2.1.0042.0007» ушло бы вместо полного. #>
    param([string]$Text)
    $m = [regex]::Matches($Text, '\d+\.\d+\.\d+\.\d+(\.\d{8}_\d{4})?')
    if ($m.Count -eq 0) { return '' }
    return ($m | Sort-Object { $_.Value.Length } -Descending | Select-Object -First 1).Value
}

function Get-Marker {
    <# Заявка и заводской SN. Форматы, которые реально встречаются в именах: «_(01312)», «_SN00042»,
       «заявка 1312», «зав №42». Возвращаем как есть, без дополнения нулями: дополнит уже программа. #>
    param([string]$Text)
    $request = ''
    $sn = ''
    if ($Text -match '_\((\d{1,6})\)') { $request = $Matches[1] }
    elseif ($Text -match '(?i)заявк\w*\s*№?\s*(\d{1,6})') { $request = $Matches[1] }
    if ($Text -match '(?i)_SN\s*(\d{1,6})') { $sn = $Matches[1] }
    elseif ($Text -match '(?i)зав\w*\.?\s*№?\s*(\d{1,6})') { $sn = $Matches[1] }
    return @{ Request = $request; Sn = $sn }
}

Write-Host "Обходим: $Source" -ForegroundColor Cyan
$all = Get-ChildItem -LiteralPath $Source -Recurse -File -ErrorAction SilentlyContinue
Write-Host ("Всего файлов: {0}" -f $all.Count)

$rows = New-Object System.Collections.Generic.List[object]
foreach ($file in $all) {
    $ext = $file.Extension.ToLowerInvariant()
    $kind = if ($FirmwareExt -contains $ext) { 'прошивка' }
            elseif ($HmiExt -contains $ext) { 'панель' }
            elseif ($ext -in @('.pdf', '.doc', '.docx')) { 'документ' }
            elseif ($ext -in @('.xls', '.xlsx')) { 'карта/таблица' }
            else { continue }

    # Ищем по ВСЕМУ пути, а не только по имени: тип и контроллер чаще записаны папкой, а версия и
    # номер заявки — в имени файла.
    $relative = $file.FullName.Substring($Source.TrimEnd('\').Length).TrimStart('\')
    $group = Find-Word -Text $relative -Words $GroupWords
    $controller = Find-Word -Text $relative -Words $ControllerWords
    $version = Get-VersionRaw -Text $file.Name
    if (-not $version) { $version = Get-VersionRaw -Text $relative }
    $marker = Get-Marker -Text $relative

    $complete = $kind -eq 'прошивка' -and $group -and $controller -and $version
    $rows.Add([pscustomobject]@{
        Тип         = $kind
        Файл        = $file.Name
        Папка       = Split-Path $relative -Parent
        Размер_КБ   = [math]::Round($file.Length / 1KB, 1)
        Изменён     = $file.LastWriteTime.ToString('yyyy-MM-dd')
        ТипШкафа    = $group
        Контроллер  = $controller
        Версия      = $version
        Заявка      = $marker.Request
        SN          = $marker.Sn
        Распознано  = if ($complete) { 'да' } else { 'нет' }
        Полныйпуть  = $file.FullName
    })
}

# Проекты панелей и ПЛК бывают ПАПКАМИ (Кинко, Овен) — их отмечаем отдельной строкой, иначе такой
# проект выглядел бы в отчёте россыпью из десятков безымянных файлов.
$projectDirs = Get-ChildItem -LiteralPath $Source -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '(?i)(проект|project|_hmi|kinco|овен)' }
foreach ($dir in $projectDirs) {
    $relative = $dir.FullName.Substring($Source.TrimEnd('\').Length).TrimStart('\')
    $rows.Add([pscustomobject]@{
        Тип         = 'проект-папка'
        Файл        = $dir.Name
        Папка       = Split-Path $relative -Parent
        Размер_КБ   = ''
        Изменён     = $dir.LastWriteTime.ToString('yyyy-MM-dd')
        ТипШкафа    = Find-Word -Text $relative -Words $GroupWords
        Контроллер  = Find-Word -Text $relative -Words $ControllerWords
        Версия      = Get-VersionRaw -Text $relative
        Заявка      = (Get-Marker -Text $relative).Request
        SN          = (Get-Marker -Text $relative).Sn
        Распознано  = 'нет'
        Полныйпуть  = $dir.FullName
    })
}

$rows | Sort-Object Тип, Папка, Файл | Export-Csv -LiteralPath $Report -NoTypeInformation -Encoding UTF8
Write-Host "`nОтчёт: $Report" -ForegroundColor Green

Write-Host "`nСводка:" -ForegroundColor Cyan
$rows | Group-Object Тип | Sort-Object Count -Descending |
    ForEach-Object { Write-Host ("  {0,-14} {1}" -f $_.Name, $_.Count) }
# @() вокруг выборок обязательно: одна строка (или ноль) не имеет .Count в Windows PowerShell 5.1, и
# сводка молча печатала бы пустоту вместо числа.
$fw = @($rows | Where-Object Тип -eq 'прошивка')
$ok = @($fw | Where-Object Распознано -eq 'да').Count
Write-Host ("`n  прошивок всего: {0}, распознано полностью: {1}, требует разбора руками: {2}" -f `
    $fw.Count, $ok, ($fw.Count - $ok))
Write-Host "`nБез типа шкафа / контроллера / версии — смотреть в отчёте столбец «Распознано = нет»."

if (-not $Apply) {
    Write-Host "`nЭто был СУХОЙ прогон: на диске ничего не менялось." -ForegroundColor Yellow
    Write-Host "Проверьте отчёт, и если раскладка верна — повторите с -Target <корень диска> -Apply."
    return
}

# ── Раскладка ────────────────────────────────────────────────────────────────
# Копируем, а не переносим: исходный хлам остаётся нетронутым, пока человек не убедится, что всё на
# месте. Подтип не угадываем НИКОГДА — в старых путях он записан как попало, а ошибиться подтипом
# значит положить прошивку не в тот шкаф; вместо этого кладём в «—» (у Финдера это законный
# «подтипа нет»), а разложить по подтипам можно потом в самой программе.
Write-Host "`nРаскладываем в: $Target" -ForegroundColor Cyan
$copied = 0
$skipped = 0
foreach ($row in ($rows | Where-Object { $_.Распознано -eq 'да' })) {
    $versionFolder = if ($row.Заявка -or $row.SN) {
        $name = @()
        if ($row.Заявка) { $name += ('{0:d5}' -f [int]$row.Заявка) }
        if ($row.SN) { $name += ('SN{0:d5}' -f [int]$row.SN) }
        Join-Path (Join-Path (Join-Path $Target 'ПО') (Join-Path $row.ТипШкафа (Join-Path '—' $row.Контроллер))) `
            (Join-Path 'ОПЦ' ($name -join '_'))
    } else {
        Join-Path (Join-Path (Join-Path $Target 'ПО') (Join-Path $row.ТипШкафа (Join-Path '—' $row.Контроллер))) $row.Версия
    }

    $fwFolder = Join-Path $versionFolder 'Прошивка'
    $dst = Join-Path $fwFolder (Split-Path $row.Полныйпуть -Leaf)
    if (Test-Path -LiteralPath $dst) {
        Write-Host "  уже есть, пропуск: $dst"
        $skipped++
        continue
    }
    New-Item -ItemType Directory -Force -Path $fwFolder | Out-Null
    foreach ($slot in @('Инструкция', 'Карта Modbus', 'Карта ВВ', 'HMI')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $versionFolder $slot) | Out-Null
    }
    # CHANGELOG.md с номером версии — без него досмотр диска не опознает ОПЦ-папку, названную заявкой.
    $changelog = Join-Path $versionFolder 'CHANGELOG.md'
    if (-not (Test-Path -LiteralPath $changelog)) {
        Set-Content -LiteralPath $changelog -Value "# $($row.Версия)" -Encoding UTF8
    }
    Copy-Item -LiteralPath $row.Полныйпуть -Destination $dst
    $copied++
}
Write-Host ("`nСкопировано: {0}, пропущено (уже было): {1}" -f $copied, $skipped) -ForegroundColor Green
Write-Host "Дальше — в программе: Настройки → Иерархия → «Досмотреть диск», прошивки появятся в модерации."
