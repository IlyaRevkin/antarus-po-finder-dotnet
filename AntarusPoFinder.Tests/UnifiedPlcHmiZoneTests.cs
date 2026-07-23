using System.Linq;
using System.Reflection;
using AntarusPoFinder.App.Views;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Бета-опция "единая drag&amp;drop-зона ПЛК+HMI" (UploadView, включается галочкой в
/// Настройки → Общие → ЗАГРУЗКА, см. ConfigService.UnifiedPlcHmiZoneEnabled). UploadView сам —
/// WPF UserControl, инстанцировать его в юнит-тестах (как и остальные *View в этом проекте) не
/// принято — вместо этого здесь два независимых уровня проверки:
///  1) настройка в ConfigService — обычный getter/setter/default, без UI;
///  2) инвариант, на который полагается классификация "ПЛК или HMI по расширению файла"
///     (UploadView.ClassifyAndAssignOne) — расширения ПЛК и HMI не должны пересекаться, иначе
///     единая зона начнёт молча путать типы файлов вместо того, чтобы спросить оператора.</summary>
public class UnifiedPlcHmiZoneTests
{
    [Fact]
    public void UnifiedPlcHmiZoneEnabled_DefaultsToFalse()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        // Раздельные зоны — поведение по умолчанию для всех существующих и новых установок, пока
        // программист/администратор явно не включит бету себе в Настройках.
        Assert.False(cfg.UnifiedPlcHmiZoneEnabled());
    }

    [Fact]
    public void SetUnifiedPlcHmiZoneEnabled_RoundTrips()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetUnifiedPlcHmiZoneEnabled(true);
        Assert.True(cfg.UnifiedPlcHmiZoneEnabled());

        cfg.SetUnifiedPlcHmiZoneEnabled(false);
        Assert.False(cfg.UnifiedPlcHmiZoneEnabled());
    }

    [Fact]
    public void UnifiedPlcHmiZoneEnabled_IsPersistedOnANewConfigServiceInstance()
    {
        // Как и остальные настройки вкладки "Общие" — значение должно переживать пересоздание
        // ConfigService поверх той же БД (напр. перезапуск приложения), не только жить в памяти.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg1 = new ConfigService(db);
        cfg1.SetUnifiedPlcHmiZoneEnabled(true);

        var cfg2 = new ConfigService(db);
        Assert.True(cfg2.UnifiedPlcHmiZoneEnabled());
    }

    /// <summary>Читает приватные static readonly string[] MainExecutableExts/HmiExecutableExts из
    /// UploadView через рефлексию — по тому же приёму, что и
    /// ConfigExportSkipSettingsKeysTests.ReadSkipSettingsKeys (чтение чужого приватного поля вместо
    /// его копирования сюда: если поле переименуют/удалят, тест явно упадёт, а не станет тихим
    /// no-op). Не создаёт ни одного WPF-объекта — обращение к статическому полю не требует
    /// Dispatcher/STA, только загрузки типа.</summary>
    private static string[] ReadExtField(string name)
    {
        var field = typeof(UploadView).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (string[])field!.GetValue(null)!;
    }

    [Fact]
    public void PlcAndHmiExecutableExtensions_DoNotOverlap()
    {
        // Единая зона (UploadView.ClassifyAndAssignOne) решает "ПЛК или HMI" по расширению файла:
        // ровно одна из групп расширений матчит — файл однозначно того типа, без вопроса оператору.
        // Если бы одно и то же расширение встречалось в обоих списках, эта логика начала бы либо
        // всегда попадать в первую проверенную ветку (тихо и неверно для второго типа), либо вообще
        // перестала бы быть однозначной — расширения обязаны быть непересекающимися множествами.
        var plcExts = ReadExtField("MainExecutableExts");
        var hmiExts = ReadExtField("HmiExecutableExts");

        Assert.NotEmpty(plcExts);
        Assert.NotEmpty(hmiExts);

        var overlap = plcExts.Intersect(hmiExts, System.StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(overlap.Count == 0, $"ПЛК и HMI расширения пересекаются: {string.Join(", ", overlap)}");
    }

    [Fact]
    public void HmiExecutableExtensions_ContainFsprj()
    {
        // .fsprj — расширение, явно упомянутое и в галочке "Добавить HMI-проект" (классический режим),
        // и в подсказке единой зоны; если его вдруг уберут из HmiExecutableExts, единая зона перестанет
        // молча узнавать HMI-файлы и начнёт переспрашивать оператора на каждом из них — не поломка,
        // но заметная регрессия удобства, которую стоит ловить явно.
        var hmiExts = ReadExtField("HmiExecutableExts");
        Assert.Contains(".fsprj", hmiExts, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainExecutableExtensions_ContainKnownPlcProjectExtensions()
    {
        var plcExts = ReadExtField("MainExecutableExts");
        foreach (var ext in new[] { ".psl", ".lfs", ".kpr", ".kpj", ".dpj" })
            Assert.Contains(ext, plcExts, System.StringComparer.OrdinalIgnoreCase);
    }
}
