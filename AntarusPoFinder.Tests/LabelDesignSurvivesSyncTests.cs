using System;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Дословная жалоба: «не сохраняются настройки дизайна QR-инструкции».
///
/// Сохранялись они исправно — их затирал ПРИЁМ общего конфига. Оформление этикетки синхронизируется
/// сознательно (это одно лицо предприятия на всех наклейках, см. LabelSettingsSyncTests), но
/// применялось оно вслепую: ConfigSyncService.ApplyToDatabase писал пришедшее значение поверх
/// местного, ничего не сравнивая. При этом подтяжка идёт САМА, у всех ролей, раз в sync_interval_min
/// (по умолчанию 5 минут), а ОТПРАВЛЯЕТ конфиг только администратор и по умолчанию не отправляет
/// вовсе. Итог для наладчика: подобрал оформление — через пять минут оно вернулось к чужому, и уехать
/// к коллегам не могло в принципе.
///
/// Здесь проверяется правило, которым это лечится: местная правка НОВЕЕ приехавшего снимка —
/// побеждает; старше — уступает, как и раньше. Плюс сопутствующее: отметки выполненных разовых
/// миграций больше не уезжают в общий конфиг (уехав, они отменяли миграцию на машине, где та ещё не
/// выполнялась).</summary>
public class LabelDesignSurvivesSyncTests
{
    /// <summary>Ровно тот случай, на который жаловались: у коллеги в общем конфиге лежит прежнее
    /// оформление, здесь человек только что сохранил своё — приём его не трогает.</summary>
    [Fact]
    public void LocallySavedDesign_SurvivesAnIncomingSharedConfig()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        // На машине A — прежнее оформление, и она выкладывает общий конфиг.
        (LabelLayout.Default with
        {
            HeadlinePlace = HeadlinePlacement.Auto,
            QrPlace = QrPlacement.Left,
            HoleText = "РЭ",
            ShowFrame = true,
        }).SaveTo(m.CfgA);
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");

        // На машине B человек подбирает своё — ПОСЛЕ того, как конфиг уже выложен.
        var mine = LabelLayout.Default with
        {
            HeadlinePlace = HeadlinePlacement.Bottom,
            QrPlace = QrPlacement.Above,
            HoleText = "МОЁ",
            ShowFrame = false,
            NoteText = "Договор 42",
        };
        mine.SaveTo(m.CfgB);

        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(update);
        ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, m.Root.Path);

        var after = LabelLayout.FromConfig(m.CfgB);
        Assert.Equal(mine.HeadlinePlace, after.HeadlinePlace);
        Assert.Equal(mine.QrPlace, after.QrPlace);
        Assert.Equal(mine.HoleText, after.HoleText);
        Assert.Equal(mine.ShowFrame, after.ShowFrame);
        Assert.Equal(mine.NoteText, after.NoteText);
    }

    /// <summary>Обратная сторона того же правила, и она не менее важна: администратор задаёт
    /// оформление ОДИН раз и рассылает его всем. Его экспорт свежее давней местной правки — значит,
    /// приезжает и применяется. Иначе «защита местного» превратилась бы в «оформление больше никогда
    /// не синхронизируется», а это ровно то, чего просили не делать.</summary>
    [Fact]
    public void ASharedConfigNewerThanTheLocalEdit_StillWins()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        // Сначала правка на B…
        (LabelLayout.Default with { HoleText = "СТАРОЕ", QrPlace = QrPlacement.Below }).SaveTo(m.CfgB);

        // …и только ПОТОМ администратор выкладывает общий конфиг. Отметка местной правки хранится с
        // точностью до секунды (тот же формат, что exported_at) — «позже» должно быть позже и в этом
        // масштабе, иначе тест проверял бы округление, а не правило.
        WaitForNextSecond();
        (LabelLayout.Default with { HoleText = "ОБЩЕЕ", QrPlace = QrPlacement.Right }).SaveTo(m.CfgA);
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");

        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(update);
        ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, m.Root.Path);

        var after = LabelLayout.FromConfig(m.CfgB);
        Assert.Equal("ОБЩЕЕ", after.HoleText);
        Assert.Equal(QrPlacement.Right, after.QrPlace);

        // Отметка снята: приезжий снимок её перекрыл, и держать её дальше незачем — иначе следующий
        // приём сравнивался бы с заведомо устаревшим временем.
        Assert.False(m.DbB.LocalSettingEdits().ContainsKey("label_hole_text"));
    }

    /// <summary>Плашка «что приедет» обязана считать ровно то, что применение и применит. Разойдись
    /// они — окно вечно обещало бы изменение, которое приём молча пропускает, и человек ходил бы
    /// нажимать «Обновить сейчас» без всякого следствия.</summary>
    [Fact]
    public void TheBanner_DoesNotPromiseChangesThatApplySkips()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        (LabelLayout.Default with { HoleText = "ЧУЖОЕ" }).SaveTo(m.CfgA);
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");

        (LabelLayout.Default with { HoleText = "МОЁ" }).SaveTo(m.CfgB);

        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(update);

        // Единственная разошедшаяся настройка — та, что защищена местной правкой. Значит, менять
        // приёму нечего, и обещать он ничего не должен.
        Assert.Equal(0, update!.SettingsChanged);

        ConfigSyncService.Apply(m.SvcB, update.ConfigPath, m.Root.Path);
        Assert.Equal("МОЁ", LabelLayout.FromConfig(m.CfgB).HoleText);
    }

    /// <summary>Список ключей, которые SaveTo помечает как «правил человек», обязан совпадать с тем,
    /// что реально уезжает в общий конфиг. Забытый здесь ключ — это настройка, которая снова
    /// возвращается к чужой через пять минут после сохранения, и заметить это можно только на уже
    /// напечатанной наклейке.</summary>
    [Fact]
    public void SyncedKeys_MatchExactlyWhatLeavesThisMachine()
    {
        using var dbFile = new TempDb();
        using var db = new Core.Data.Database(dbFile.Path);
        var cfg = new ConfigService(db);

        LabelLayout.Default.SaveTo(cfg);

        var skip = ConfigSyncSkipKeys.Read();
        var written = db.GetAllSettings().Keys.Where(k => k.StartsWith("label_", StringComparison.Ordinal));
        var travelling = written.Where(k => !skip.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(LabelLayout.SyncedKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(), travelling);
    }

    /// <summary>Отметка выполненной разовой миграции — местная. Уехав в общий конфиг, она приезжает
    /// на машину, где миграция ещё НЕ выполнялась, и отменяет её навсегда: флаг стоит, работа не
    /// сделана, повторить нечем. Проверка идёт по префиксу (см. ConfigSyncService.MigrationFlagPrefix),
    /// поэтому новая миграция защищена сама собой, без правки списков.</summary>
    [Fact]
    public void MigrationFlags_NeverTravelToTheSharedConfig()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.DbA.SetSetting(ConfigSyncService.MigrationFlagPrefix + "vydumannaya", "true");
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");

        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(update);
        ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, m.Root.Path);

        Assert.False(m.DbB.HasSetting(ConfigSyncService.MigrationFlagPrefix + "vydumannaya"));
    }

    /// <summary>Отметка местной правки хранится с точностью до секунды. Тест, которому нужно
    /// «строго позже», обязан эту секунду переждать — иначе он проверял бы не правило, а то, в какую
    /// сторону округлилось время, и падал бы примерно раз в N запусков.</summary>
    private static void WaitForNextSecond()
    {
        var started = DateTime.Now;
        while (DateTime.Now.Second == started.Second && DateTime.Now - started < TimeSpan.FromSeconds(2))
            System.Threading.Thread.Sleep(20);
    }
}
