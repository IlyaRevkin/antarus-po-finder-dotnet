using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Записать, ПОЧЕМУ у этой версии не нашлась панель, — сам, без участия человека.
///
/// <b>Зачем.</b> Жалоба Киселёва 05.11.2026: «при обновлении новой версии ПО, на примере XL, Finder
/// не обнаружил файл HMI». Воспроизвести нечем, а чинить по догадке мы уже пробовали и ошиблись
/// (см. ModerationDiagnostics). Поэтому две недели наблюдаем: следующий такой случай должен
/// приехать объяснённым — с папками, которые смотрели, с тем, что в них лежало, и с правилом,
/// которое дало «нет».
///
/// <b>Когда.</b> Только на ОТРИЦАТЕЛЬНОМ ответе фонового обхода карточки и только у версии, которая
/// на диске есть (у «на диске не найдена» объяснять нечего — там нет вообще ничего). Запись на
/// каждую удачную карточку утопила бы нужный случай: карточек в выдаче десятки, и обход идёт при
/// каждом поиске.
///
/// <b>Как часто.</b> Одна версия — одна запись в день с этой машины (см.
/// <see cref="HmiLookupDiagnostics.MarkIfNew"/>): человек ищет один и тот же шкаф по десятку раз за
/// день, и без отсечки одна беда дала бы сотню одинаковых писем.
///
/// <b>Куда.</b> В ту же очередь, из которой уходят журналы заливок и снимки модерации: отправкой
/// занимается общий фоновый проход (LoaderDiagnostics.FlushAsync), второго канала заводить
/// незачем.</summary>
public static class HmiLookupDiagnosticsReporter
{
    /// <summary>Отметка «про какие версии эта машина сегодня уже написала». Местная — см.
    /// ConfigSyncService.SkipSettingsKeys: уехав в общий конфиг, она заткнула бы запись у всех
    /// остальных, а случай нужен как раз с той машины, где он виден.</summary>
    public const string SentKey = "hmi_miss_diag_sent";

    /// <summary>Роли осматриваемых папок — словами, а не путями: путь у каждой машины свой, а роль
    /// одна и та же, и именно по ней запись потом и читают.</summary>
    private const string KindLocal = "локальная копия версии";
    private const string KindNetwork = "папка версии на диске";
    private const string KindShared = "папка HMI (своя у версии либо общая у контроллера)";
    private const string KindStored = "путь панели из записи версии";

    /// <summary>Положить запись в очередь. Ничего не бросает и ничего не показывает: диагностика не
    /// имеет права мешать выдаче — ради неё человек и искал.
    ///
    /// Раскладка по потокам ровно та же, что у всего остального в этой выдаче: к базе (отметка «уже
    /// писали») ходим на потоке интерфейса — соединение одно на программу, — а по диску (а он
    /// сетевой и отвечает через раз) гуляем в фоне.</summary>
    /// <param name="localFolder">Папка этой версии в локальном кэше.</param>
    /// <param name="networkFolder">Та папка версии на сетевом диске, которую обход реально смотрел
    /// (уже разрешённая, см. SearchView.ResolvedNetworkDir), а не disk_path из базы.</param>
    /// <param name="sharedHmiFolder">Чем вычислить папку «HMI», из которой читается панель этой
    /// записи (ответ VersionDocFolders). Функцией, а не значением: она сама ходит на диск, и звать
    /// её надо уже в фоне. Фоновый обход карточки на эту папку не смотрит вовсе — это одна из
    /// веток, по которым ответ оказывается «нет» молча.</param>
    public static async System.Threading.Tasks.Task ReportMissingAsync(AppServices services,
        HierarchyResult result, string? localFolder, string? networkFolder, Func<string?> sharedHmiFolder)
    {
        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var mark = HmiLookupDiagnostics.MarkIfNew(services.Cfg.Get(SentKey), today, result.VersionRaw);
            if (mark is null) return;

            var user = services.CurrentUserName;
            var text = await System.Threading.Tasks.Task.Run(() =>
                BuildAndName(result, user, localFolder, networkFolder, sharedHmiFolder));

            var dir = LoaderDiagnostics.QueueFolder(ConfigService.AppData);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, text.Name), text.Json, new UTF8Encoding(false));
            LoaderDiagnostics.Trim(dir);

            services.Cfg.Set(SentKey, mark);
        }
        catch (Exception)
        {
            // Молча: след на беду не имеет права становиться второй бедой.
        }
    }

    /// <summary>Собрать запись — только диск, ни одного обращения к базе: зовётся из фона.</summary>
    private static (string Name, string Json) BuildAndName(HierarchyResult result, string user,
        string? localFolder, string? networkFolder, Func<string?> sharedHmiFolder)
    {
        string? shared;
        try { shared = sharedHmiFolder(); }
        catch (Exception) { shared = null; }

        var probes = new List<HmiLookupDiagnostics.FolderProbe>
        {
            HmiLookupDiagnostics.Inspect(KindLocal, localFolder, withLayout: true),
            HmiLookupDiagnostics.Inspect(KindNetwork, networkFolder, withLayout: true),
            HmiLookupDiagnostics.Inspect(KindShared, shared, withLayout: false),
        };
        // Путь из записи осматриваем, только когда он есть и ведёт не туда, куда мы уже посмотрели:
        // пустой hmi_path виден по флагу ниже, а дубль той же папки только мешал бы читать.
        if (!string.IsNullOrWhiteSpace(result.HmiPath)
            && !string.Equals(result.HmiPath, shared, StringComparison.OrdinalIgnoreCase))
            probes.Add(HmiLookupDiagnostics.Inspect(KindStored, result.HmiPath, withLayout: false));

        var snapshot = new HmiLookupDiagnostics.Snapshot
        {
            At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Machine = Environment.MachineName,
            User = user,
            AppVersion = AppUpdateService.CurrentVersionText,
            Equipment = result.EquipmentType,
            Subtype = result.SubtypeName,
            Controller = result.Controller,
            Version = result.VersionRaw,
            Layout = LayoutOf(probes),
            HmiPathStored = !string.IsNullOrWhiteSpace(result.HmiPath),
            HmiHintStored = !string.IsNullOrWhiteSpace(result.HmiExecutableHint),
            FullScanFoundHmi = probes.Exists(p => p.HmiFiles.Count > 0),
            Rule = HmiLookupDiagnostics.RuleFor(probes),
            Probes = probes,
        };

        return (HmiLookupDiagnostics.FileNameFor(snapshot), HmiLookupDiagnostics.Serialize(snapshot));
    }

    /// <summary>Режим чтения САМОЙ версии — по сетевой папке, а при её недоступности по локальной
    /// копии. Берём у первой папки версии, которая на месте: «перестроенная/совместимость» — это
    /// свойство раскладки, и у двух копий одной версии оно одинаковое.</summary>
    private static string LayoutOf(List<HmiLookupDiagnostics.FolderProbe> probes)
    {
        foreach (var kind in new[] { KindNetwork, KindLocal })
            foreach (var probe in probes)
                if (probe.Kind == kind && probe.Exists)
                    return probe.Layout;
        return HmiLookupDiagnostics.LayoutUnknown;
    }
}
