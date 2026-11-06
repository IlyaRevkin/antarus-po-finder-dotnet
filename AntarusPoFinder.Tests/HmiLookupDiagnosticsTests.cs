using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>След на отрицательный ответ «у версии нет HMI» (жалоба Киселёва 05.11.2026,
/// HmiLookupDiagnostics). Проверяется ровно то, ради чего он заведён: запись должна объяснять ответ
/// — какую папку смотрели, что в ней лежало, в каком режиме читалась версия, — и не должна тащить
/// наружу ни содержимого файлов, ни имени учётной записи.</summary>
public class HmiLookupDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hmi-diag-" + Guid.NewGuid().ToString("N"));

    public HmiLookupDiagnosticsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ПишетИменаТогоЧтоЛежалоВПапке()
    {
        var version = Dir("версия");
        File.WriteAllText(Path.Combine(version, "прошивка.lfs"), "секретное содержимое");
        Dir("версия", "Инструкция");

        var probe = HmiLookupDiagnostics.Inspect("папка версии", version, withLayout: true);

        Assert.True(probe.Exists);
        Assert.Contains("прошивка.lfs", probe.Entries);
        Assert.Contains("Инструкция\\", probe.Entries);
        Assert.Equal(2, probe.EntriesTotal);
        // Содержимого файлов в записи нет и быть не должно.
        Assert.DoesNotContain("секретное", string.Join(" ", probe.Entries));
    }

    [Fact]
    public void РежимВерсииВиденПоПапкеПрошивка()
    {
        var legacy = Dir("старая");
        Assert.Equal(HmiLookupDiagnostics.LayoutLegacy,
            HmiLookupDiagnostics.Inspect("версия", legacy, withLayout: true).Layout);

        var rebuilt = Dir("новая");
        Directory.CreateDirectory(VersionLayout.FirmwareFolder(rebuilt));
        Assert.Equal(HmiLookupDiagnostics.LayoutRebuilt,
            HmiLookupDiagnostics.Inspect("версия", rebuilt, withLayout: true).Layout);

        Assert.Equal(HmiLookupDiagnostics.LayoutUnknown,
            HmiLookupDiagnostics.Inspect("версия", Path.Combine(_root, "нет такой"), withLayout: true).Layout);
    }

    /// <summary>Самая ценная улика: полный обход находит проект панели там, где быстрый ответ сказал
    /// «нет». Правило в записи обязано назвать это ошибкой программы, а не отсутствием панели.</summary>
    [Fact]
    public void НайденныйПолнымОбходомПроектМеняетПравило()
    {
        var version = Dir("версия");
        File.WriteAllText(Path.Combine(Dir("версия", "HMI", "2.4_hmi"), "панель.fsprj"), "x");

        var probe = HmiLookupDiagnostics.Inspect("папка версии", version, withLayout: true);

        Assert.Single(probe.HmiFiles);
        Assert.Contains("панель.fsprj", probe.HmiFiles[0]);
        Assert.Equal(HmiLookupDiagnostics.Rules.MissedExistingFile,
            HmiLookupDiagnostics.RuleFor(new[] { probe }));
    }

    [Fact]
    public void ПустаяПапкаИОтсутствующаяРазличаются()
    {
        var empty = HmiLookupDiagnostics.Inspect("версия", Dir("пусто"), withLayout: true);
        Assert.Equal(HmiLookupDiagnostics.Rules.NothingAnywhere, HmiLookupDiagnostics.RuleFor(new[] { empty }));

        var missing = HmiLookupDiagnostics.Inspect("версия", Path.Combine(_root, "нет"), withLayout: true);
        Assert.Equal(HmiLookupDiagnostics.Rules.NoFoldersToLookIn, HmiLookupDiagnostics.RuleFor(new[] { missing }));
    }

    /// <summary>Локальный кэш прошивок лежит внутри профиля — имя учётной записи коллеги в записи не
    /// нужно никому, и уехать наружу оно не должно.</summary>
    [Fact]
    public void ПутьВнутриПрофиляОбезличивается()
    {
        var inside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AntarusPoFinder", "fw", "ПЖ", "2.4");
        var redacted = HmiLookupDiagnostics.Redact(inside);

        Assert.StartsWith("%ПРОФИЛЬ%", redacted);
        Assert.Contains("2.4", redacted);
        Assert.DoesNotContain(Environment.UserName, redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void СетевойПутьОстаётсяКакЕсть()
    {
        const string net = @"\\ant_srv\Software\ПО\НГР\XL\SMH5\2.4.1321.0005";
        Assert.Equal(net, HmiLookupDiagnostics.Redact(net));
    }

    // ── Отсечка: одна версия — одна запись в день с этой машины ──────────────────────────────

    [Fact]
    public void ПовторПоТойЖеВерсииВТотЖеДеньНеПишется()
    {
        var first = HmiLookupDiagnostics.MarkIfNew(null, "2026-11-06", "2.4.1321.0005");
        Assert.NotNull(first);
        Assert.Null(HmiLookupDiagnostics.MarkIfNew(first, "2026-11-06", "2.4.1321.0005"));
    }

    [Fact]
    public void ДругаяВерсияВТотЖеДеньПишется()
    {
        var first = HmiLookupDiagnostics.MarkIfNew(null, "2026-11-06", "2.4.1321.0005");
        var second = HmiLookupDiagnostics.MarkIfNew(first, "2026-11-06", "2.4.1321.0006");
        Assert.NotNull(second);
        Assert.Contains("2.4.1321.0005", second);
        Assert.Contains("2.4.1321.0006", second);
    }

    [Fact]
    public void НаутроТаЖеВерсияПишетсяСнова()
    {
        var first = HmiLookupDiagnostics.MarkIfNew(null, "2026-11-06", "2.4.1321.0005");
        var next = HmiLookupDiagnostics.MarkIfNew(first, "2026-11-07", "2.4.1321.0005");
        Assert.NotNull(next);
        Assert.StartsWith("2026-11-07", next);
        // Вчерашний список не тащим: отметка про сегодня, а не журнал за всё время.
        Assert.Equal("2026-11-07|2.4.1321.0005", next);
    }

    [Fact]
    public void ПотолокНаДеньОстанавливаетЗапись()
    {
        string? mark = null;
        for (var i = 0; i < HmiLookupDiagnostics.MaxVersionsPerDay; i++)
        {
            mark = HmiLookupDiagnostics.MarkIfNew(mark, "2026-11-06", "версия-" + i);
            Assert.NotNull(mark);
        }
        Assert.Null(HmiLookupDiagnostics.MarkIfNew(mark, "2026-11-06", "версия-лишняя"));
    }

    /// <summary>Отметка про ЭТУ машину. Уехав в общий конфиг, она заткнула бы след у всех остальных
    /// — а случай нужен как раз с той машины, где он виден (та же причина, что у снимка
    /// модерации рядом).</summary>
    [Fact]
    public void ОтметкаНеУезжаетВОбщийКонфиг()
    {
        Assert.Contains(App.Services.HmiLookupDiagnosticsReporter.SentKey, ConfigSyncSkipKeys.Read());
    }
}
