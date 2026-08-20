# Завести человека, которому можно ходить по SFTP в папку Antarus.
#
# Отдельно от setup-sftp.ps1 намеренно: добавление человека не должно требовать правки конфигурации
# службы и её перезапуска — иначе каждый новый наладчик означал бы короткий перерыв у всех
# остальных.
#
# Вход только по ключу. Пароль не заводится вовсе: пароль от локальной учётки сервера, гуляющий
# по переписке, — ровно то, чего мы избегаем, выдавая доступ наружу.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $User,

    # Открытый ключ: файл .pub либо строка целиком.
    [string] $PublicKeyFile,
    [string] $PublicKey,

    [string] $Group = 'antarus-sftp',
    [string] $ShareRoot = 'C:\AntarusShare',

    # Убрать доступ вместо выдачи.
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Нужны права администратора.'
}

if ($Remove) {
    if (Get-LocalUser -Name $User -ErrorAction SilentlyContinue) {
        Remove-LocalGroupMember -Group $Group -Member $User -ErrorAction SilentlyContinue
        Disable-LocalUser -Name $User
        Write-Host "Доступ у $User отобран: выведен из группы $Group, учётка отключена." -ForegroundColor Yellow
        Write-Host 'Саму учётку намеренно не удаляем — чтобы осталась видна история, кому выдавали.'
    } else {
        Write-Host "Пользователя $User нет."
    }
    return
}

if (-not $PublicKey -and -not $PublicKeyFile) {
    throw 'Нужен открытый ключ: -PublicKeyFile путь\к\ключу.pub или -PublicKey "ssh-ed25519 AAAA…"'
}
if ($PublicKeyFile) {
    if (-not (Test-Path $PublicKeyFile)) { throw "Файл ключа не найден: $PublicKeyFile" }
    $PublicKey = (Get-Content $PublicKeyFile -Raw).Trim()
}
if ($PublicKey -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-\S+)\s+\S+') {
    throw 'Это не похоже на открытый ключ. Ожидается строка вида «ssh-ed25519 AAAAC3Nz… комментарий».'
}

# ─── Учётная запись ───────────────────────────────────────────────────────────
if (-not (Get-LocalUser -Name $User -ErrorAction SilentlyContinue)) {
    # Пароль обязателен для создания учётки в Windows, но входить по нему нельзя: вход по паролю
    # закрыт в sshd (см. README), а сама учётка нужна только как «кому принадлежит ключ».
    $random = [Convert]::ToBase64String((1..24 | ForEach-Object { Get-Random -Maximum 256 }))
    New-LocalUser -Name $User `
        -Password (ConvertTo-SecureString $random -AsPlainText -Force) `
        -PasswordNeverExpires `
        -Description 'SFTP-доступ к папке Antarus, вход только по ключу' | Out-Null
    Write-Host "Учётка $User создана."
} else {
    Enable-LocalUser -Name $User
    Write-Host "Учётка $User уже была, включена."
}

if (-not (Get-LocalGroupMember -Group $Group -Member $User -ErrorAction SilentlyContinue)) {
    Add-LocalGroupMember -Group $Group -Member $User
    Write-Host "Добавлен в группу $Group."
}

# ─── Ключ ─────────────────────────────────────────────────────────────────────
#
# Ключи лежат в профиле пользователя. Важная особенность Windows-сборки OpenSSH: для членов группы
# «Администраторы» файл authorized_keys в профиле ИГНОРИРУЕТСЯ, вместо него читается общий
# C:\ProgramData\ssh\administrators_authorized_keys. Наши SFTP-пользователи администраторами быть
# не должны — иначе ключ окажется не там, где его ищут, и вход молча не сработает.
$profileDir = "C:\Users\$User"
if (-not (Test-Path $profileDir)) { New-Item -ItemType Directory -Path $profileDir | Out-Null }
$sshDir = Join-Path $profileDir '.ssh'
if (-not (Test-Path $sshDir)) { New-Item -ItemType Directory -Path $sshDir | Out-Null }

$authKeys = Join-Path $sshDir 'authorized_keys'
$existing = if (Test-Path $authKeys) { Get-Content $authKeys } else { @() }
if ($existing -contains $PublicKey) {
    Write-Host 'Такой ключ уже прописан.'
} else {
    Add-Content -Path $authKeys -Value $PublicKey -Encoding utf8
    Write-Host 'Ключ добавлен.'
}

# Права: sshd откажется читать authorized_keys, если файл доступен кому-то ещё.
icacls $sshDir /inheritance:r /grant:r "${User}:(F)" "SYSTEM:(F)" "Администраторы:(F)" 2>$null | Out-Null
icacls $sshDir /inheritance:r /grant:r "${User}:(F)" "SYSTEM:(F)" "Administrators:(F)" 2>$null | Out-Null
icacls $authKeys /inheritance:r /grant:r "${User}:(R)" "SYSTEM:(F)" "Администраторы:(F)" 2>$null | Out-Null
icacls $authKeys /inheritance:r /grant:r "${User}:(R)" "SYSTEM:(F)" "Administrators:(F)" 2>$null | Out-Null

# Личная папка внутри общей — чтобы файлы разных людей не перемешивались.
$personal = Join-Path (Join-Path $ShareRoot 'Обмен') $User
if (-not (Test-Path $personal)) {
    New-Item -ItemType Directory -Path $personal | Out-Null
    $acl = Get-Acl $personal
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $User, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -Path $personal -AclObject $acl
    Write-Host "Личная папка: $personal"
}

Write-Host ''
Write-Host '═══ Готово ═══' -ForegroundColor Green
Write-Host "Проверка с машины пользователя:"
Write-Host "  sftp $User@<адрес сервера>"
Write-Host "После входа он увидит корень как /, внутри — папку «Обмен»."
