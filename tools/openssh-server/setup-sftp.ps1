# Настройка OpenSSH на сервере конторы: доступ снаружи к ОДНОЙ папке по SFTP.
#
# Что делает:
#   1. Ставит и включает службу OpenSSH Server (если ещё не стоит).
#   2. Заводит локальную группу, членам которой разрешён только SFTP и только в заданную папку.
#   3. Готовит саму папку и права на неё так, как этого требует sshd для chroot.
#   4. Дописывает блок Match в sshd_config — с резервной копией и без дублей при повторном запуске.
#   5. Открывает порт в брандмауэре и проверяет конфиг перед перезапуском службы.
#
# Скрипт идемпотентный: повторный запуск ничего не ломает и не задваивает.
# Запускать от администратора.
#
# Пользователей заводит НЕ этот скрипт, а add-sftp-user.ps1 рядом — чтобы добавление человека
# не требовало трогать конфигурацию службы.

[CmdletBinding()]
param(
    # Папка, которую увидят подключившиеся. Она же корень chroot.
    [string] $ShareRoot = 'C:\AntarusShare',

    # Локальная группа, членство в которой и включает ограничение.
    [string] $Group = 'antarus-sftp',

    # Порт SSH. 22 — стандартный; менять есть смысл, только если 22 уже занят или ИТ так решили.
    [int] $Port = 22,

    # Разрешить ли членам группы проброс портов. По умолчанию НЕТ: проброс — это доступ к любым
    # внутренним адресам, видимым серверу, а мы выдаём доступ к папке.
    [switch] $AllowPortForwarding
)

$ErrorActionPreference = 'Stop'

function Require-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Нужны права администратора: запустите PowerShell «от имени администратора».'
    }
}

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

Require-Admin

# ─── 1. Служба OpenSSH ────────────────────────────────────────────────────────
Step 'Проверяю OpenSSH Server'
$cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
if ($cap.State -ne 'Installed') {
    Write-Host '    ставлю компонент OpenSSH.Server…'
    Add-WindowsCapability -Online -Name $cap.Name | Out-Null
} else {
    Write-Host '    уже установлен'
}

Set-Service -Name sshd -StartupType Automatic
if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }
Write-Host "    служба sshd: $((Get-Service sshd).Status), автозапуск включён"

# ─── 2. Группа ────────────────────────────────────────────────────────────────
Step "Группа $Group"
if (-not (Get-LocalGroup -Name $Group -ErrorAction SilentlyContinue)) {
    New-LocalGroup -Name $Group -Description 'Доступ по SFTP только к папке Antarus' | Out-Null
    Write-Host '    создана'
} else {
    Write-Host '    уже есть'
}

# ─── 3. Папка и права ─────────────────────────────────────────────────────────
#
# Требование sshd к chroot жёсткое и неочевидное: сам корень chroot и все папки выше него должны
# принадлежать администратору/SYSTEM и НЕ быть доступны на запись обычным пользователям. Если это
# нарушить, подключение молча обрывается с «bad ownership or modes» в журнале — самая частая
# причина «SFTP не работает, а конфиг вроде правильный».
#
# Поэтому раскладка такая: корень — только чтение, а писать можно во вложенную папку.
Step "Папка $ShareRoot"
$inbox = Join-Path $ShareRoot 'Обмен'
foreach ($dir in @($ShareRoot, $inbox)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}

# Корень: наследование выключаем, оставляем SYSTEM и администраторов на полный доступ,
# группе — только чтение.
$acl = Get-Acl $ShareRoot
$acl.SetAccessRuleProtection($true, $false)
$acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }
$rules = @(
    New-Object Security.AccessControl.FileSystemAccessRule(
        'SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'),
    New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')).Translate([Security.Principal.NTAccount]),
        'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'),
    New-Object Security.AccessControl.FileSystemAccessRule(
        $Group, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
)
foreach ($r in $rules) { $acl.AddAccessRule($r) }
Set-Acl -Path $ShareRoot -AclObject $acl
Write-Host '    корень: группе только чтение (требование chroot)'

# Вложенная папка обмена — туда уже можно писать.
$aclIn = Get-Acl $inbox
$aclIn.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
    $Group, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl -Path $inbox -AclObject $aclIn
Write-Host "    $inbox — запись разрешена"

# ─── 4. sshd_config ───────────────────────────────────────────────────────────
Step 'Правлю sshd_config'
$cfgPath = 'C:\ProgramData\ssh\sshd_config'
$marker  = '# --- antarus-sftp (добавлено setup-sftp.ps1) ---'
$endMark = '# --- конец antarus-sftp ---'

$forwarding = if ($AllowPortForwarding) { 'yes' } else { 'no' }
$block = @"
$marker
# Блок Match ОБЯЗАН идти последним в файле: всё, что написано после Match, относится к нему,
# а не к общим настройкам. Дописывать что-либо ниже нельзя.
Match Group $Group
    ChrootDirectory $ShareRoot
    ForceCommand internal-sftp
    AllowTcpForwarding $forwarding
    PermitTunnel no
    X11Forwarding no
    AllowAgentForwarding no
    PermitTTY no
$endMark
"@

$content = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw } else { '' }

# Резервная копия — один раз в сутки, чтобы повторные запуски не плодили файлы.
$backup = "$cfgPath.backup-$(Get-Date -Format 'yyyy-MM-dd')"
if ((Test-Path $cfgPath) -and -not (Test-Path $backup)) {
    Copy-Item $cfgPath $backup
    Write-Host "    резервная копия: $backup"
}

if ($content -match [regex]::Escape($marker)) {
    # Заменяем прежний блок целиком — так повторный запуск с другими параметрами реально
    # применяет их, а не дописывает второй Match, который никогда не сработает.
    $pattern = [regex]::Escape($marker) + '.*?' + [regex]::Escape($endMark)
    $content = [regex]::Replace($content, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $block.TrimEnd() }, 'Singleline')
    Write-Host '    прежний блок заменён'
} else {
    $content = $content.TrimEnd() + "`r`n`r`n" + $block
    Write-Host '    блок добавлен в конец файла'
}

if ($Port -ne 22) {
    if ($content -match '(?m)^\s*#?\s*Port\s+\d+') {
        $content = [regex]::Replace($content, '(?m)^\s*#?\s*Port\s+\d+', "Port $Port")
    } else {
        $content = "Port $Port`r`n" + $content
    }
    Write-Host "    порт: $Port"
}

Set-Content -Path $cfgPath -Value $content -Encoding utf8

# ─── 5. Проверка и перезапуск ─────────────────────────────────────────────────
Step 'Проверяю конфиг перед перезапуском'
$sshd = 'C:\Windows\System32\OpenSSH\sshd.exe'
& $sshd -t -f $cfgPath
if ($LASTEXITCODE -ne 0) {
    throw "sshd не принял конфиг. Файл не тронут дальше, восстановите из $backup и разберите ошибку выше."
}
Write-Host '    конфиг корректен'

Restart-Service sshd
Write-Host "    служба перезапущена: $((Get-Service sshd).Status)"

Step 'Брандмауэр'
$ruleName = "OpenSSH Antarus $Port"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP `
        -LocalPort $Port -Action Allow | Out-Null
    Write-Host "    правило добавлено (TCP $Port)"
} else {
    Write-Host '    правило уже есть'
}

Write-Host ''
Write-Host '═══ Готово ═══' -ForegroundColor Green
Write-Host "Папка снаружи:  $ShareRoot  (внутри неё «Обмен» — на запись)"
Write-Host "Группа:         $Group"
Write-Host "Порт:           $Port"
Write-Host "Проброс портов: $(if ($AllowPortForwarding) { 'разрешён' } else { 'запрещён' })"
Write-Host ''
Write-Host 'Добавить человека:  .\add-sftp-user.ps1 -User naladchik1 -PublicKeyFile C:\Temp\naladchik1.pub'
