using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Разбор снимка машины — вся смысловая часть проверки «что тут не так». Тесты написаны
/// по живым жалобам, а не по строчкам кода:
/// <list type="bullet">
/// <item>«диск в проводнике виден, а программа его не находит» — две разные причины, и их надо
/// различать: чужая буква в базе и запуск от имени администратора;</item>
/// <item>«обновления не устанавливаются сами» — общий путь с чужой буквой и отдельно выключенная
/// автоустановка;</item>
/// <item>и самое важное — недоступный диск у наладчика вне офиса НЕ должен выглядеть поломкой,
/// иначе на красное перестанут смотреть.</item>
/// </list></summary>
public class SelfCheckAnalyzerTests
{
    /// <summary>Здоровая машина: диск на месте, обновления из общей папки, автоустановка включена.
    /// От неё все остальные случаи получаются одним `with`.</summary>
    private static SelfCheckFacts Healthy() => new()
    {
        AppVersion = "1.74.0.2",
        MachineName = "PC-15",
        WindowsUser = "naladka3",
        AppUser = "revkin.i",
        RoleLabel = "Наладчик",
        Elevated = false,
        RootPath = @"Z:\Software\Antarus Finder",
        RootKind = DiskAttachKind.DriveLetter,
        RootExists = true,
        RootReadable = true,
        RootUnc = @"\\ant_srv\Software\Antarus Finder",
        OfficeNetworkReachable = true,
        AuthConfigured = true,
        AuthReachable = true,
        AuthTarget = "antarus.local:389",
        AuthDetails = "доступен",
        StoredPathsChecked = true,
        StoredPaths = new StoredPathAuditResult(120, 0, 0, 0, Array.Empty<ForeignRootUse>(), ""),
        UpdatePathShared = "Обновления",
        UpdatePathEffective = @"Z:\Software\Antarus Finder\Обновления",
        UpdateFolderReachable = true,
        GitHubReachable = true,
        UpdateAutoInstall = true,
        SyncTransport = "fileshare",
        SyncTarget = @"Z:\Software\Antarus Finder\Конфиг",
        SyncReachable = true,
    };

    private static SelfCheckFinding Find(IReadOnlyList<SelfCheckFinding> findings, string title) =>
        findings.First(x => x.Title == title);

    [Fact]
    public void HealthyMachine_HasNoProblems()
    {
        var findings = SelfCheckAnalyzer.Analyze(Healthy());

        Assert.False(SelfCheckAnalyzer.HasProblems(findings));
        Assert.Equal(SelfCheckSeverity.Ok, Find(findings, "Рабочий диск").Severity);
        Assert.Equal(SelfCheckSeverity.Ok, Find(findings, "Откуда приходят обновления").Severity);
    }

    // ── «В проводнике диск виден, а программа его не находит» ────────────────

    /// <summary>Вторая живая версия жалобы: буква подключена в обычном сеансе Windows, а программу
    /// запустили от имени администратора — Windows специально не показывает такие диски процессам с
    /// повышенными правами. Проверка обязана назвать причину словами, а не выдать «путь не найден».</summary>
    [Fact]
    public void ElevatedWithDriveMappedInSession_BlamesElevation_NotTheDrive()
    {
        var facts = Healthy() with
        {
            Elevated = true,
            RootExists = false,
            RootReadable = false,
            RootUnc = "",
            RootMappedInSession = new MappedDrive("Z:", @"\\ant_srv\Software"),
        };

        var disk = Find(SelfCheckAnalyzer.Analyze(facts), "Рабочий диск");

        Assert.Equal(SelfCheckSeverity.Problem, disk.Severity);
        Assert.Contains("от имени администратора", disk.Reason);
        Assert.Contains(@"\\ant_srv\Software", disk.Reason);
        Assert.Contains("без «Запуск от имени администратора»", disk.Fix);
    }

    /// <summary>Та же машина, но человек вне офиса: сети нет вовсе, и утверждать, что дело именно в
    /// правах запуска, нельзя — диск не нашёлся бы и без них. Тревога снимается до предупреждения,
    /// но про права всё равно сказано, иначе в офисе человек упрётся в то же самое заново.</summary>
    [Fact]
    public void ElevatedButNoNetworkAtAll_IsWarningNotProblem()
    {
        var facts = Healthy() with
        {
            Elevated = true,
            RootExists = false,
            RootReadable = false,
            RootUnc = "",
            RootMappedInSession = new MappedDrive("Z:", @"\\ant_srv\Software"),
            OfficeNetworkReachable = false,
            AuthReachable = false,
            SyncReachable = false,
            UpdateFolderReachable = false,
        };

        var disk = Find(SelfCheckAnalyzer.Analyze(facts), "Рабочий диск");

        Assert.Equal(SelfCheckSeverity.Warning, disk.Severity);
        Assert.Contains("от имени администратора", disk.Reason);
        Assert.Contains("вне офиса", disk.Reason);
    }

    /// <summary>Повышенные права и путь буквой, но записи о подключении в сеансе нет (непостоянное
    /// подключение). Точную причину назвать нельзя, но подозрение то же самое и озвучить его надо.</summary>
    [Fact]
    public void ElevatedWithDriveLetterPath_StillPointsAtElevation()
    {
        var facts = Healthy() with { Elevated = true, RootExists = false, RootReadable = false, RootUnc = "" };

        var disk = Find(SelfCheckAnalyzer.Analyze(facts), "Рабочий диск");

        Assert.Equal(SelfCheckSeverity.Problem, disk.Severity);
        Assert.Contains("от имени администратора", disk.Reason);
    }

    /// <summary>Наладчик вне офиса: диска нет, сети нет — это норма, а не поломка, и тикет заводить
    /// не о чем. Ровно то место, где ложная тревога приучила бы смотреть мимо красного.</summary>
    [Fact]
    public void DiskUnreachableWithNoNetwork_IsExpected_NotAProblem()
    {
        var facts = Healthy() with
        {
            RootExists = false,
            RootReadable = false,
            RootUnc = "",
            OfficeNetworkReachable = false,
            AuthReachable = false,
            SyncReachable = false,
            UpdateFolderReachable = false,
        };

        var findings = SelfCheckAnalyzer.Analyze(facts);

        Assert.Equal(SelfCheckSeverity.Warning, Find(findings, "Рабочий диск").Severity);
        Assert.False(SelfCheckAnalyzer.HasProblems(findings));
    }

    /// <summary>А вот когда сеть работает, а диска нет — это уже разговор.</summary>
    [Fact]
    public void DiskUnreachableWhileNetworkWorks_IsAProblem()
    {
        var facts = Healthy() with { RootExists = false, RootReadable = false, RootUnc = "" };

        Assert.Equal(SelfCheckSeverity.Problem, Find(SelfCheckAnalyzer.Analyze(facts), "Рабочий диск").Severity);
    }

    [Fact]
    public void FolderFoundButUnreadable_BlamesPermissions()
    {
        var facts = Healthy() with { RootReadable = false, RootError = "Отказано в доступе" };

        var disk = Find(SelfCheckAnalyzer.Analyze(facts), "Рабочий диск");

        Assert.Equal(SelfCheckSeverity.Problem, disk.Severity);
        Assert.Contains("прав", disk.Reason);
        Assert.Contains("naladka3", disk.Fix);
    }

    // ── Пути в базе ─────────────────────────────────────────────────────────

    /// <summary>Случай коллеги: в базе пути с чужой буквой, но все приводятся к своему корню. Это
    /// штатное устройство хранения, и красить его в красный нельзя.</summary>
    [Fact]
    public void ForeignPathsThatLocalize_AreReportedAsNormal()
    {
        var facts = Healthy() with
        {
            StoredPaths = new StoredPathAuditResult(1240, 1240, 1240, 0,
                new[] { new ForeignRootUse("Y:", 1240) }, ""),
        };

        var paths = Find(SelfCheckAnalyzer.Analyze(facts), "Пути к файлам в базе");

        Assert.Equal(SelfCheckSeverity.Ok, paths.Severity);
        Assert.Contains("штатно", paths.Reason);
        Assert.Equal("", paths.Fix);
    }

    [Fact]
    public void PathsThatCannotBeLocalized_AreAProblemWithASample()
    {
        var facts = Healthy() with
        {
            StoredPaths = new StoredPathAuditResult(1240, 1240, 1237, 3,
                new[] { new ForeignRootUse("Y:", 1240) }, @"Y:\Старое\ручная папка"),
        };

        var paths = Find(SelfCheckAnalyzer.Analyze(facts), "Пути к файлам в базе");

        Assert.Equal(SelfCheckSeverity.Problem, paths.Severity);
        Assert.Contains("Y: — 1240", paths.Reason);
        Assert.Contains(@"Y:\Старое\ручная папка", paths.Reason);
    }

    [Fact]
    public void PathsNotChecked_IsInformationalOnly()
    {
        var facts = Healthy() with { StoredPathsChecked = false, StoredPaths = StoredPathAuditResult.Empty };

        Assert.Equal(SelfCheckSeverity.Info, Find(SelfCheckAnalyzer.Analyze(facts), "Пути к файлам в базе").Severity);
    }

    // ── «Обновления не устанавливаются сами» ────────────────────────────────

    /// <summary>Гипотеза по второй половине жалобы коллеги, и она же самая коварная: администратор
    /// задал общую папку обновлений АБСОЛЮТНЫМ путём со своей буквой. Настройка синхронизируется
    /// дословно, и на всех остальных машинах папка просто не находится — молча, потому что запасным
    /// источником выступает GitHub.</summary>
    [Fact]
    public void SharedUpdateFolderWithForeignDriveLetter_IsAProblemEvenWhenGitHubWorks()
    {
        var facts = Healthy() with
        {
            RootPath = @"\\ant_srv\Software\Antarus Finder",
            RootKind = DiskAttachKind.Unc,
            RootUnc = "",
            UpdatePathShared = @"Z:\Software\Antarus Finder\Обновления",
            UpdatePathEffective = @"Z:\Software\Antarus Finder\Обновления",
            UpdateFolderReachable = false,
            UpdateFolderProblem = "папка недоступна",
            GitHubReachable = true,
        };

        var update = Find(SelfCheckAnalyzer.Analyze(facts), "Откуда приходят обновления");

        Assert.Equal(SelfCheckSeverity.Problem, update.Severity);
        Assert.Contains("Z:", update.Reason);
        Assert.Contains(@"\\ant_srv\Software", update.Reason);
        Assert.Contains("ОТНОСИТЕЛЬНО корня диска", update.Fix);
    }

    /// <summary>Тот же общий путь, но записанный относительным — он разворачивается от корня этой
    /// машины и работает. Тревоги быть не должно.</summary>
    [Fact]
    public void SharedUpdateFolderWrittenRelative_IsFine()
    {
        var update = Find(SelfCheckAnalyzer.Analyze(Healthy()), "Откуда приходят обновления");

        Assert.Equal(SelfCheckSeverity.Ok, update.Severity);
        Assert.Contains("развёрнутая от корня", update.Reason);
    }

    /// <summary>Локальный перебив сильнее общей настройки — и если он указывает в никуда, машина
    /// остаётся без обновлений при полностью исправной общей папке.</summary>
    [Fact]
    public void LocalUpdateOverridePointingNowhere_IsAProblem()
    {
        var facts = Healthy() with
        {
            UpdatePathLocal = @"D:\Обновления",
            UpdatePathEffective = @"D:\Обновления",
            UpdateFolderReachable = false,
            UpdateFolderProblem = "папка недоступна",
        };

        var update = Find(SelfCheckAnalyzer.Analyze(facts), "Откуда приходят обновления");

        Assert.Equal(SelfCheckSeverity.Problem, update.Severity);
        Assert.Contains("перебивает общую", update.Reason);
        Assert.Contains("Очистить поле", update.Fix);
    }

    /// <summary>Относительный общий путь без заданного корня диска разворачивать не от чего —
    /// UpdateFolderResolver честно возвращает пусто, и обновления молча идут мимо папки.</summary>
    [Fact]
    public void RelativeSharedPathWithoutRoot_IsAProblem()
    {
        var facts = Healthy() with
        {
            RootPath = "",
            RootKind = DiskAttachKind.NotConfigured,
            RootUnc = "",
            UpdatePathShared = "Обновления",
            UpdatePathEffective = "",
        };

        var update = Find(SelfCheckAnalyzer.Analyze(facts), "Откуда приходят обновления");

        Assert.Equal(SelfCheckSeverity.Problem, update.Severity);
        Assert.Contains("развернуть его не от чего", update.Reason);
    }

    /// <summary>Прямой ответ на «у него не устанавливаются обновления сами»: галочка выключена.
    /// Это не поломка (человек мог выключить осознанно), но сказать об этом надо в лоб.</summary>
    [Fact]
    public void AutoInstallOff_IsCalledOutInPlainWords()
    {
        var auto = Find(SelfCheckAnalyzer.Analyze(Healthy() with { UpdateAutoInstall = false }), "Автоустановка обновлений");

        Assert.Equal(SelfCheckSeverity.Warning, auto.Severity);
        Assert.Contains("не устанавливаются сами", auto.Reason);
        Assert.Contains("Настройки", auto.Fix);
    }

    /// <summary>«Почему не сработало в прошлый раз» — журнал проверок обновлений, который до сих пор
    /// мог прочитать только тот, кто знает про этот файл.</summary>
    [Fact]
    public void PastUpdateFailure_IsShownAsHistory_NotAsANewProblem()
    {
        var findings = SelfCheckAnalyzer.Analyze(Healthy() with { LastUpdateFailure = "[2026-08-20] Папка обновлений недоступна" });

        var history = Find(findings, "Прошлая проверка обновлений");
        Assert.Equal(SelfCheckSeverity.Info, history.Severity);
        Assert.Contains("Папка обновлений недоступна", history.Reason);
    }

    [Fact]
    public void WithoutPastFailures_NoHistoryRowAtAll() =>
        Assert.DoesNotContain(SelfCheckAnalyzer.Analyze(Healthy()), x => x.Title == "Прошлая проверка обновлений");

    [Theory]
    [InlineData("", "", "GitHub — папка обновлений не настроена")]
    [InlineData(@"D:\Обновления", "", "папка обновлений этой машины (перебивает общую)")]
    public void UpdateRuleText_NamesTheRuleThatFired(string local, string shared, string expected) =>
        Assert.Equal(expected, SelfCheckAnalyzer.UpdateRuleText(
            Healthy() with { UpdatePathLocal = local, UpdatePathShared = shared, UpdatePathEffective = local }));

    // ── Канал обмена и хранилище ────────────────────────────────────────────

    /// <summary>Диск доступен, а общей папки обмена на нём нет — данные с других машин не приходят.
    /// Не «проблема»: на свежем диске папка появляется с первой отправки.</summary>
    [Fact]
    public void SyncFolderMissingOnReachableDisk_IsAWarningWithAFix()
    {
        var sync = Find(SelfCheckAnalyzer.Analyze(Healthy() with { SyncReachable = false }), "Синхронизация настроек и тикетов");

        Assert.Equal(SelfCheckSeverity.Warning, sync.Severity);
        Assert.Contains("Отправить сейчас", sync.Fix);
    }

    [Fact]
    public void SyncServerNotAnswering_CarriesTheProbeMessage()
    {
        var facts = Healthy() with
        {
            SyncTransport = "server",
            SyncTarget = "https://sync.local:8443",
            SyncReachable = false,
            SyncDetails = "Служба не ответила за 10 секунд.",
        };

        var sync = Find(SelfCheckAnalyzer.Analyze(facts), "Синхронизация настроек и тикетов");

        Assert.Equal(SelfCheckSeverity.Problem, sync.Severity);
        Assert.Contains("Служба не ответила", sync.Reason);
    }

    /// <summary>Пустые ключи хранилища — штатное состояние, пока выкладка выключена.</summary>
    [Fact]
    public void StorageDisabled_IsNormal() =>
        Assert.Equal(SelfCheckSeverity.Info, Find(SelfCheckAnalyzer.Analyze(Healthy()), "Хранилище на хостинге").Severity);

    /// <summary>А включённая выкладка без ключей — уже противоречие: инструкции молча не уходят, и
    /// QR на наклейке с телефона не откроется.</summary>
    [Fact]
    public void StorageEnabledWithoutKeys_IsAProblem()
    {
        var facts = Healthy() with { StorageEnabled = true, StorageHasAddress = true, StorageTarget = "s3.twcstorage.ru/amperus" };

        var storage = Find(SelfCheckAnalyzer.Analyze(facts), "Хранилище на хостинге");

        Assert.Equal(SelfCheckSeverity.Problem, storage.Severity);
        Assert.Contains("ключи доступа не загружены", storage.Reason);
    }

    [Fact]
    public void SecondDiskNotConfigured_IsNormal() =>
        Assert.Equal(SelfCheckSeverity.Info, Find(SelfCheckAnalyzer.Analyze(Healthy()), "Второй диск (схемы)").Severity);

    // ── Отчёт и тикет ───────────────────────────────────────────────────────

    /// <summary>Смысл готового тикета в том, что коллеге не нужно ничего объяснять словами: версия,
    /// роль, как подключён диск, какое правило обновлений сработало и сколько записей ссылается на
    /// чужую букву — всё уже в тексте.</summary>
    [Fact]
    public void TicketText_CarriesTheFactsInsteadOfAskingTheColleague()
    {
        var facts = Healthy() with
        {
            Elevated = true,
            RootExists = false,
            RootReadable = false,
            RootUnc = "",
            RootMappedInSession = new MappedDrive("Z:", @"\\ant_srv\Software"),
            StoredPaths = new StoredPathAuditResult(1240, 1240, 1240, 0, new[] { new ForeignRootUse("Y:", 1240) }, ""),
            UpdateAutoInstall = false,
        };
        var findings = SelfCheckAnalyzer.Analyze(facts);

        var text = SelfCheckReport.BuildTicketText(facts, findings, new DateTime(2026, 8, 25, 12, 0, 0));

        Assert.StartsWith("Проверка компьютера нашла проблемы.", text);
        Assert.Contains("1.74.0.2", text);
        Assert.Contains("PC-15", text);
        Assert.Contains("Наладчик", text);
        Assert.Contains("Запуск от имени администратора: да", text);
        Assert.Contains(@"в сеансе Windows Z: подключён к \\ant_srv\Software", text);
        Assert.Contains("Y: — 1240", text);
        Assert.Contains("автоустановка: ВЫКЛЮЧЕНА", text);
        Assert.Contains("Что делать:", text);
    }

    [Fact]
    public void Report_StartsWithTheTimestampSoItCanBeForwardedAsIs()
    {
        var facts = Healthy();

        var text = SelfCheckReport.BuildReport(facts, SelfCheckAnalyzer.Analyze(facts), new DateTime(2026, 8, 25, 12, 34, 56));

        Assert.StartsWith("Проверка компьютера — 2026-08-25 12:34:56", text);
        Assert.Contains("[ ОК ] Рабочий диск", text);
    }

    /// <summary>Ничего не сломано — тикет предлагать не о чем.</summary>
    [Fact]
    public void HealthyMachine_TicketTextSaysSoInsteadOfInventingProblems()
    {
        var facts = Healthy();

        var text = SelfCheckReport.BuildTicketText(facts, SelfCheckAnalyzer.Analyze(facts), DateTime.Now);

        Assert.StartsWith("Проверка компьютера: явных проблем не найдено", text);
    }
}
