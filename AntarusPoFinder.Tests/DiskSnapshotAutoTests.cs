using System;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>АВТОМАТИЧЕСКИЙ СЛЕПОК: когда снимать и когда НЕ отправлять.
///
/// Раньше слепок снимался только руками, и это было не случайностью: опись диска с машины каждого
/// коллеги ежедневно — мусорный поток, в котором полезное тонет. Автоматику поэтому держат два
/// ограничения, и проверяются здесь именно они:
///
/// • не чаще раза в сутки на машину, и только если диск реально доступен;
/// • не отправлять, если содержимое описи не изменилось — причём сравнение идёт по СОДЕРЖИМОМУ,
///   а не по машине и времени. От этого зависит главное обещание: шесть машин, смотрящих в один
///   диск, не должны прислать шесть одинаковых описей в сутки.</summary>
public class DiskSnapshotAutoTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private static DiskSnapshot Snapshot(string machine, DateTime takenAt, string root,
        string appVersion = "2.2.3.0") =>
        DiskSnapshot.Build(machine, takenAt, root,
            // Пути файлов в слепке ОТНОСИТЕЛЬНЫЕ — такими их отдаёт обход
            // (DiskSnapshotService.Collect), и именно поэтому слепки двух машин сравнимы вообще.
            new[]
            {
                (@"ПО\ПЖ\2.0\SMH5\1.0\Прошивка\ф.psl", 10L, new DateTime(2026, 11, 1)),
                (@"ПО\ПЖ\2.0\SMH5\1.0\CHANGELOG.md", 20L, new DateTime(2026, 11, 1)),
            },
            new[]
            {
                new FwVersionRecord
                {
                    SyncId = "s1", GroupName = "ПЖ", SubtypeName = "2.0", CtrlName = "SMH5",
                    VersionRaw = "1.0", DiskPath = $@"{root}\ПО\ПЖ\2.0\SMH5\1.0", Status = "active",
                },
            },
            DiskSnapshot.MaxFiles,
            new[] { @"ПО\ПЖ\2.0\SMH5\1.0", @"ПО\ПЖ\2.0\SMH5\1.0\HMI" },
            appVersion);

    // ── Пора снимать или ещё рано ────────────────────────────────────────────────────────────

    [Fact]
    public void Снимаем_когда_прошли_сутки_и_диск_на_месте()
    {
        var decision = DiskSnapshotSchedule.Decide(
            enabled: true, diskAvailable: true, Now,
            lastDoneUtc: Now.AddHours(-25), lastTryUtc: Now.AddHours(-25));

        Assert.Equal(DiskSnapshotDecision.Take, decision);
    }

    /// <summary>Сутки считаются от последней УДАЧИ, а не по часам: «каждый день в три ночи» на
    /// рабочем компьютере означает «никогда», его в это время выключают.</summary>
    [Fact]
    public void До_суток_со_съёмки_второй_раз_не_снимаем()
    {
        var decision = DiskSnapshotSchedule.Decide(
            enabled: true, diskAvailable: true, Now,
            lastDoneUtc: Now.AddHours(-23), lastTryUtc: Now.AddHours(-23));

        Assert.Equal(DiskSnapshotDecision.TooSoon, decision);
    }

    /// <summary>Первый запуск на машине: отметок нет вовсе — снимаем.</summary>
    [Fact]
    public void Без_отметок_снимаем_сразу()
    {
        var decision = DiskSnapshotSchedule.Decide(
            enabled: true, diskAvailable: true, Now, DateTime.MinValue, DateTime.MinValue);

        Assert.Equal(DiskSnapshotDecision.Take, decision);
    }

    /// <summary>Диск недоступен — отдельный ответ, а не «ещё рано»: «слепков с этой машины нет» и
    /// «на этой машине нет диска» разные диагнозы, и различать их потом будут по журналу.</summary>
    [Fact]
    public void Без_рабочего_диска_пропускаем_и_говорим_почему()
    {
        var decision = DiskSnapshotSchedule.Decide(
            enabled: true, diskAvailable: false, Now, DateTime.MinValue, DateTime.MinValue);

        Assert.Equal(DiskSnapshotDecision.NoDisk, decision);
    }

    /// <summary>После неудачи ждём час, а не сутки: шара, отвалившаяся на минуту, успеет вернуться
    /// за смену. Но и не каждый тик синхронизации — обход сетевого дерева идёт минутами.</summary>
    [Fact]
    public void После_неудачи_повторяем_не_раньше_чем_через_час()
    {
        Assert.Equal(DiskSnapshotDecision.TooSoon, DiskSnapshotSchedule.Decide(
            enabled: true, diskAvailable: true, Now,
            lastDoneUtc: DateTime.MinValue, lastTryUtc: Now.AddMinutes(-10)));

        Assert.Equal(DiskSnapshotDecision.Take, DiskSnapshotSchedule.Decide(
            enabled: true, diskAvailable: true, Now,
            lastDoneUtc: DateTime.MinValue, lastTryUtc: Now.AddMinutes(-70)));
    }

    [Fact]
    public void Выключенная_автоматика_не_снимает_ничего()
    {
        var decision = DiskSnapshotSchedule.Decide(
            enabled: false, diskAvailable: true, Now, DateTime.MinValue, DateTime.MinValue);

        Assert.Equal(DiskSnapshotDecision.Disabled, decision);
    }

    // ── Дедупликация по отпечатку содержимого ────────────────────────────────────────────────

    /// <summary>ГЛАВНОЕ СВОЙСТВО. Два слепка ОДНОГО диска, снятые в разное время, разными версиями
    /// программы, на РАЗНЫХ машинах и с разными буквами шары, обязаны дать один отпечаток. Иначе
    /// обещание «шесть одинаковых описей не приедут» не выполняется: каждая машина шлёт свою.</summary>
    [Fact]
    public void Отпечаток_не_зависит_от_машины_времени_версии_и_буквы_диска()
    {
        var mine = Snapshot("ILIA-PC", new DateTime(2026, 11, 11, 3, 0, 0), @"Z:\Software", "2.2.3.0");
        var colleagues = Snapshot("CEH-02", new DateTime(2026, 11, 11, 18, 44, 0),
            @"\\ANT-SRV\WebDisk\Software", "2.1.0.0");

        Assert.Equal(DiskSnapshot.ContentFingerprint(mine.ToJson()),
            DiskSnapshot.ContentFingerprint(colleagues.ToJson()));
    }

    /// <summary>И обратное: изменился диск — изменился отпечаток. Иначе дедупликация прятала бы
    /// ровно то, ради чего слепок и снимают.</summary>
    [Fact]
    public void Отпечаток_меняется_когда_на_диске_появился_файл()
    {
        var before = Snapshot("ILIA-PC", Now, @"Z:\Software");
        var after = DiskSnapshot.Build("ILIA-PC", Now, @"Z:\Software",
            before.Files.Select(f => (f.Path, f.Size, DateTime.Parse(f.Modified)))
                .Append((@"ПО/ПЖ/2.0/SMH5/1.0/HMI/панель.wlr", 5L, new DateTime(2026, 11, 2))),
            Array.Empty<FwVersionRecord>());

        Assert.NotEqual(DiskSnapshot.ContentFingerprint(before.ToJson()),
            DiskSnapshot.ContentFingerprint(after.ToJson()));
    }

    /// <summary>Мнение БАЗЫ в отпечаток входит намеренно: разъехавшиеся между машинами базы — одна
    /// из тех бед, ради которых слепок заведён, и прятать её дедупликацией было бы наоборот.</summary>
    [Fact]
    public void Расхождение_баз_между_машинами_отпечаток_разводит()
    {
        var withRow = Snapshot("ILIA-PC", Now, @"Z:\Software");
        var withoutRow = DiskSnapshot.Build("CEH-02", Now, @"Z:\Software",
            withRow.Files.Select(f => (f.Path, f.Size, DateTime.Parse(f.Modified))),
            Array.Empty<FwVersionRecord>(), DiskSnapshot.MaxFiles, withRow.Folders);

        Assert.NotEqual(DiskSnapshot.ContentFingerprint(withRow.ToJson()),
            DiskSnapshot.ContentFingerprint(withoutRow.ToJson()));
    }

    [Fact]
    public void Совпавший_отпечаток_отправку_отменяет()
    {
        var hash = DiskSnapshot.ContentFingerprint(Snapshot("ILIA-PC", Now, @"Z:\Software").ToJson());

        Assert.False(DiskSnapshotSchedule.ShouldSend(hash, hash));
        Assert.True(DiskSnapshotSchedule.ShouldSend("", hash), "первый слепок с машины уезжает всегда");
        Assert.True(DiskSnapshotSchedule.ShouldSend("другой", hash));
    }

    /// <summary>Неразбираемый слепок отпечатка не имеет — и тогда лучше отправить лишний раз, чем
    /// молча совпасть с прошлым и не уехать никогда.</summary>
    [Fact]
    public void Слепок_без_отпечатка_отправляется_на_всякий_случай()
    {
        Assert.Equal("", DiskSnapshot.ContentFingerprint("это не json"));
        Assert.True(DiskSnapshotSchedule.ShouldSend("", ""));
    }
}
