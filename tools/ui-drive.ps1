<#
.SYNOPSIS
    Живой прогон интерфейса: запустить программу на отдельном профиле, походить по ней и снять экран.

.DESCRIPTION
    ⚠️ ЗАПУСКАТЬ ТОЛЬКО ПО ЯВНОЙ ПРОСЬБЕ ИЛЬИ. Прогон занимает экран и тратит лимиты, поэтому он не
    часть обычной работы (правило записано в CLAUDE.md).

    ПОЧЕМУ ИМЕННО UI AUTOMATION, А НЕ МЫШЬ. Проверено 05.11.2026 на живой машине: синтетический ввод
    отсюда до окна не доходит — SetCursorPos курсор не двигает вовсе (GetCursorPos возвращает одну и
    ту же точку за краем экрана), а SendInput с юникодом не попадает в поля WPF. Ковыряться в причинах
    (DPI-виртуализация процесса, чужое окно поверх, целостность процессов) незачем: UI Automation
    обходит это целиком — она ставит фокус и нажимает кнопки через дерево элементов, без курсора и
    без пересчёта координат под 125 %.

    КАК ВОЙТИ. На свежем профиле аварийный пароль администратора — «12345»
    (Database.SeedDefaultAdminPasswordHash). Настоящие доменные учётки для прогона не нужны и
    спрашивать их не надо.

.EXAMPLE
    powershell -File tools/ui-drive.ps1 -Beta -Shot "C:\Temp\main.png"
#>
param(
    # Какую сборку гонять. Без ключа — рабочая.
    [switch]$Beta,
    # Куда положить снимок экрана. Пусто — не снимать.
    [string]$Shot = "",
    # Профиль: отдельная папка, чтобы не трогать рабочую базу пользователя на этом ПК.
    [string]$Profile = "$env:LOCALAPPDATA\AntarusUiDrive",
    # Не закрывать программу в конце — когда нужно продолжить вручную.
    [switch]$KeepOpen
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = if ($Beta) {
    Join-Path $root "AntarusPoFinder.App\bin\Beta\net8.0-windows\AntarusPoFinder.App.exe"
} else {
    Join-Path $root "AntarusPoFinder.App\bin\Debug\net8.0-windows\AntarusPoFinder.App.exe"
}
if (-not (Test-Path $exe)) { throw "Сначала соберите: dotnet build AntarusPoFinder.App/AntarusPoFinder.App.csproj" + $(if ($Beta) { " -c Beta" }) }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

# ── Запуск на изолированном профиле ───────────────────────────────────────────
$env:ANTARUS_TEST_APPDATA = $Profile
New-Item -ItemType Directory -Force $Profile | Out-Null
$app = Start-Process -PassThru $exe
Start-Sleep -Seconds 12

function Get-Window([string]$title) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $title)
    [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
}

function Get-ById($parent, [string]$id) {
    $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}

function Get-ByName($parent, [string]$name) {
    $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}

function Set-Text($element, [string]$text) {
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}

function Invoke-Button($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# ── Вход: аварийный пароль свежего профиля ────────────────────────────────────
$login = Get-Window "Вход в Antarus ПО Finder"
if ($login) {
    Set-Text (Get-ById $login 'AdminEscapePasswordInput') "12345"
    Invoke-Button (Get-ByName $login 'Войти как администратор')
    Start-Sleep -Seconds 6
}

$main = Get-Window "Antarus ПО Finder"
if (-not $main) { throw "Главное окно не открылось — смотрите снимок экрана" }

# Обучалка перекрывает собой всё, ради чего прогон и затевался.
$skip = Get-ByName $main 'Пропустить всё'
if ($skip) { Invoke-Button $skip; Start-Sleep -Seconds 2 }

# ── Снимок ────────────────────────────────────────────────────────────────────
if ($Shot) {
    # Поверх всех окон — иначе чужое окно закроет наше и снимок окажется ни о чём.
    Add-Type @"
using System; using System.Runtime.InteropServices;
public class UiDriveTop {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x,int y,int cx,int cy,uint f);
  public static void Topmost(IntPtr h, bool on){ SetWindowPos(h, on ? new IntPtr(-1) : new IntPtr(-2), 0,0,0,0, 0x0003|0x0040); }
}
"@
    $app.Refresh()
    [UiDriveTop]::Topmost($app.MainWindowHandle, $true)
    Start-Sleep -Seconds 2
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    ([System.Drawing.Graphics]::FromImage($bmp)).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($Shot)
    [UiDriveTop]::Topmost($app.MainWindowHandle, $false)
    Write-Output "снимок: $Shot"
}

if (-not $KeepOpen) {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Remove-Item -Recurse -Force $Profile -ErrorAction SilentlyContinue
    Write-Output "программа закрыта, временный профиль удалён"
} else {
    Write-Output "оставлено открытым, pid $($app.Id), профиль $Profile"
}
