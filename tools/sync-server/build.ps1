# Пересборка службы обмена antarus-sync.
#
# Зачем скрипт, если это одна команда go build: бинарник лежит В РЕПОЗИТОРИИ (исключение в
# .gitignore) — его ставит ИТ на сервере конторы, где Go нет и не будет. Значит после любой правки
# исходников службы надо прогнать это и закоммитить обновлённый exe ВМЕСТЕ с правкой, иначе в
# репозитории останется бинарник от старого кода, а расхождение никак не видно глазом.
#
# Рядом кладётся .sha256 — как у релизных файлов приложения. Он ловит порчу при копировании на
# сервер, а не подмену: лежит рядом и правится теми же правами.

[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $here
try {
    $go = (Get-Command go -ErrorAction SilentlyContinue)
    if (-not $go) {
        throw 'Go не найден в PATH. Нужен Go 1.21 или новее, взять на https://go.dev/dl/'
    }
    Write-Host (& $go.Source version)

    if (-not $SkipTests) {
        Write-Host 'Тесты службы...'
        & $go.Source test ./...
        if ($LASTEXITCODE -ne 0) {
            throw "go test вернул $LASTEXITCODE — бинарник НЕ пересобран, старый файл не тронут."
        }
    }

    $exe = Join-Path $here 'antarus-sync.exe'
    Write-Host 'Сборка...'
    & $go.Source build -o $exe .
    if ($LASTEXITCODE -ne 0) { throw "go build вернул $LASTEXITCODE." }

    $hash = (Get-FileHash -Algorithm SHA256 -Path $exe).Hash.ToLowerInvariant()
    Set-Content -Path "$exe.sha256" -Value "$hash *antarus-sync.exe" -Encoding ascii

    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ''
    Write-Host "Готово: antarus-sync.exe, $mb МБ"
    Write-Host "SHA256: $hash"
    Write-Host ''
    Write-Host 'Не забыть закоммитить exe и .sha256 вместе с правкой исходников,'
    Write-Host 'а при выпуске релиза приложить exe ассетом (см. README, раздел про релиз).'
}
finally {
    Pop-Location
}
