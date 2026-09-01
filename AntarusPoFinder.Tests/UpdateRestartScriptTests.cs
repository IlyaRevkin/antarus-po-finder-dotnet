using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Скрипт самоподмены exe при автообновлении раньше был .ps1 и запускался через
/// <c>powershell -File</c> без <c>-ExecutionPolicy Bypass</c>. В корпоративном домене GPO часто
/// ставит ExecutionPolicy = Restricted/AllSigned, и тогда powershell молча отказывался исполнять
/// скрипт: приложение уже закрылось (Application.Current.Shutdown), а подмена/перезапуск не
/// отрабатывали — «скачалось, закрылось, обратно не открылось, exe остался старым» ровно у части
/// сотрудников. Механизм переведён на .cmd (<see cref="UpdateRestartScript"/>): cmd.exe
/// ExecutionPolicy не подчиняется. Тесты фиксируют: ожидание PID перед подменой, экранирование
/// путей с пробелами/кириллицей/процентом, перезапуск даже в ветке ошибки переноса, отсутствие
/// любой зависимости от PowerShell — и один живой прогон, доказывающий, что сгенерированный .cmd
/// реально исполняется одним лишь cmd.exe и делает перенос+перезапуск.</summary>
public class UpdateRestartScriptTests
{
    private const int SomePid = 4321;
    private const string Staged = @"C:\Users\Иван Петров\AppData\Local\Programs\AntarusPoFinder\AntarusPoFinder.App.exe.update";
    private const string Current = @"C:\Users\Иван Петров\AppData\Local\Programs\AntarusPoFinder\AntarusPoFinder.App.exe";
    private const string Log = @"C:\Users\Иван Петров\AppData\Local\Temp\antarus_update_error.log";

    private static string Build() => UpdateRestartScript.BuildCmd(SomePid, Staged, Current, Log);

    [Fact]
    public void NoDependencyOnPowerShellOrExecutionPolicy()
    {
        var script = Build();
        Assert.DoesNotContain("powershell", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExecutionPolicy", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".ps1", script, StringComparison.OrdinalIgnoreCase);
        // Кодовую страницу пиннит сам скрипт — иначе кириллица в логе ошибки зависела бы от того,
        // какая CP стоит на машине по умолчанию.
        Assert.Contains("chcp 866", script);
    }

    [Fact]
    public void WaitsForProcessDeathBeforeReplacing()
    {
        var script = Build();
        // Ждёт именно наш PID и крутится в цикле, пока процесс жив (иначе move упрётся в залоченный exe).
        Assert.Contains($"PID eq {SomePid}", script);
        Assert.Contains($"find \"{SomePid}\"", script);
        Assert.Contains("goto AntarusWait", script);
        // Ожидание стоит ДО переноса.
        Assert.True(script.IndexOf("AntarusWait", StringComparison.Ordinal)
                    < script.IndexOf("move /y", StringComparison.Ordinal));
    }

    [Fact]
    public void QuotesPathsWithSpacesAndCyrillic()
    {
        var script = Build();
        // Пути с пробелами и кириллицей идут в кавычках дословно.
        Assert.Contains($"\"{Staged}\"", script);
        Assert.Contains($"\"{Current}\"", script);
        Assert.Contains($"\"{Log}\"", script);
    }

    [Fact]
    public void EscapesPercentSignInPaths()
    {
        // В имени пользователя Windows процент допустим, значит может попасть и в путь установки.
        // В .cmd-файле литеральный процент — это %%; без экранирования cmd попыталась бы раскрыть
        // %USER...% как переменную и путь бы сломался.
        const string weird = @"C:\Users\ma%user%name\App\AntarusPoFinder.App.exe";
        var script = UpdateRestartScript.BuildCmd(SomePid, weird + ".update", weird, Log);
        Assert.Contains(@"C:\Users\ma%%user%%name\App\AntarusPoFinder.App.exe", script);
        Assert.DoesNotContain("ma%user%name", script);
    }

    [Fact]
    public void QuoteCmdPath_DoublesPercent_AndQuotes()
    {
        Assert.Equal("\"C:\\a b\\x.exe\"", UpdateRestartScript.QuoteCmdPath(@"C:\a b\x.exe"));
        Assert.Equal("\"%%TEMP%%\\x\"", UpdateRestartScript.QuoteCmdPath(@"%TEMP%\x"));
    }

    [Fact]
    public void ErrorBranchStillRestartsAndLogsReason()
    {
        var script = Build();
        // Ветка ошибки: сначала причина в лог, потом type собственного текста ошибки move, и ВСЁ
        // РАВНО start current — приложение не должно остаться закрытым, даже если подмена не удалась.
        var echoIdx = script.IndexOf("echo Автообновление не установилось", StringComparison.Ordinal);
        var startIdx = script.IndexOf("start \"\"", StringComparison.Ordinal);
        Assert.True(echoIdx >= 0, "нет записи причины в лог");
        Assert.True(startIdx > echoIdx, "перезапуск должен идти после записи причины (и выполняться в любой ветке)");
        // Лог пишется перезаписью (>), а не дозаписью, чтобы старая причина не накапливалась.
        Assert.Contains($">\"{Log}\" echo", script);
        // При успехе move запись лога перескакивается — иначе на КАЖДОМ обновлении оставался бы
        // ложный «провал», который TakeLastUpdateError показал бы пользователю.
        Assert.Contains("&& goto AntarusLaunch", script);
    }

    /// <summary>Живой прогон: пишем сгенерированный .cmd на диск (в cp866, как это делает
    /// AppUpdateService.InstallAndRestart) и запускаем ОДНИМ cmd.exe /c — без PowerShell вообще,
    /// значит и без какой-либо ExecutionPolicy. PID берём заведомо мёртвый, поэтому цикл ожидания
    /// проходит сразу. Проверяем, что скрипт реально: перенёс staged→current, перезапустил current
    /// (новый current — батник, который оставляет маркер), не создал лог ошибки и самоудалился.</summary>
    [Fact]
    public void GeneratedCmd_ActuallyExecutes_MovesAndRestarts_WithoutPowerShell()
    {
        using var root = new TempRoot();
        // Каталог с пробелом и кириллицей — тот самый случай, из-за которого важно экранирование.
        var dir = Path.Combine(root.Path, "Программы AntarusPoFinder");
        Directory.CreateDirectory(dir);

        var current = Path.Combine(dir, "app.cmd");
        var staged = current + ".update";
        var log = Path.Combine(dir, "err.log");
        var launchedMarker = Path.Combine(dir, "launched.txt");

        // "Старая" версия — просто должна существовать, запускать её не будем.
        File.WriteAllText(current, "@echo off\r\n", TextFileEncoding.Cp866);
        // "Новая" версия (staged) — батник, который при запуске оставит маркер и выйдет. Именно его
        // move поставит на место current, и именно его start и запустит.
        File.WriteAllText(staged,
            "@echo off\r\n> \"" + launchedMarker + "\" echo LAUNCHED\r\n",
            TextFileEncoding.Cp866);

        var deadPid = SpawnAndKill();

        var script = UpdateRestartScript.BuildCmd(deadPid, staged, current, log);
        var scriptPath = Path.Combine(dir, "run.cmd");
        File.WriteAllText(scriptPath, script, TextFileEncoding.Cp866);

        var proc = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        proc.WaitForExit(15000);

        // Перенос состоялся: staged исчез, а current теперь содержит батник новой версии.
        Assert.False(File.Exists(staged), "staged должен был переехать в current");
        Assert.Contains("LAUNCHED", File.ReadAllText(current, TextFileEncoding.Cp866));
        // Перезапуск состоялся: запущенный новый current оставил маркер (ждём, start асинхронный).
        Assert.True(WaitForFile(launchedMarker, TimeSpan.FromSeconds(10)),
            "перезапущенный current должен был оставить маркер — значит start отработал");
        // Успех: лог ошибки не создан, скрипт самоудалился.
        Assert.False(File.Exists(log), "при успешном переносе лог ошибки создаваться не должен");
        Assert.False(File.Exists(scriptPath), "скрипт должен был удалить сам себя");
    }

    private static int SpawnAndKill()
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.WaitForExit();
        var pid = p.Id;
        p.Dispose();
        return pid; // PID уже завершившегося процесса — цикл ожидания в скрипте пройдёт сразу.
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(path)) return true;
            Thread.Sleep(100);
        }
        return File.Exists(path);
    }
}
