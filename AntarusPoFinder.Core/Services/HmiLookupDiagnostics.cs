using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntarusPoFinder.Core.Services;

/// <summary>ПОЧЕМУ У ЭТОЙ ВЕРСИИ «НЕТ HMI» — СЛЕД НА ОТРИЦАТЕЛЬНОМ ОТВЕТЕ.
///
/// <b>Зачем.</b> Жалоба Киселёва 05.11.2026: «при обновлении новой версии ПО, на примере XL, Finder
/// не обнаружил файл HMI». Воспроизвести нечем — ни того шкафа, ни той папки, ни той версии, — а
/// чинить по догадке мы уже пробовали (см. ModerationDiagnostics: догадка оказалась неверной, и
/// доказал это тест, а не правка). Решение Ильи: завести следы, две недели последить и чинить по
/// фактам; не повторится — закрыть.
///
/// <b>Что записывается.</b> Ровно то, чем потом объясняется ответ: КАКИЕ папки смотрели, что в них
/// реально лежало (ИМЕНА, без содержимого), в каком режиме читалась версия (перестроенная —
/// «Прошивка\» на месте — или совместимость, файлы в корне) и какое правило дало «нет».
///
/// <b>Только отрицательный ответ.</b> Писать на каждую удачную карточку значит утопить нужный случай
/// в тысяче одинаковых — ровно тем это и кончилось бы: карточек в выдаче десятки, и обход идёт при
/// каждом поиске.
///
/// <b>Чего здесь нет.</b> Содержимого файлов — ни байта; это имена и факт существования, не выгрузка
/// чужого диска. Путей в личные папки человека — профиль и его подпапки в путях заменяются на
/// <c>%ПРОФИЛЬ%</c> (см. <see cref="Redact"/>): локальный кэш прошивок живёт внутри профиля, и имя
/// учётной записи коллеги в журнале не нужно никому.
///
/// <b>Куда уезжает.</b> Тем же молчаливым каналом, что журналы заливок и снимки модерации: очередь на
/// диске → <see cref="LoaderDiagnostics.FlushAsync"/> → страница приёма с <c>silent=1</c>. Отсечка
/// «один случай с одной машины не чаще раза в день» — в App/Services/HmiLookupDiagnosticsReporter.</summary>
public static class HmiLookupDiagnostics
{
    /// <summary>Сколько имён из папки записываем. Сорок — это «видно, что там вообще лежит», и при
    /// этом запись остаётся размером с письмо. Остальное считается числом (см.
    /// <see cref="FolderProbe.EntriesTotal"/>): для объяснения важно «там 300 файлов», а не их
    /// список.</summary>
    public const int MaxNames = 40;

    /// <summary>Сколько найденных файлов панели перечисляем. Больше трёх-пяти не нужно: если они
    /// есть, вопрос уже не «где они», а «почему их не увидели».</summary>
    public const int MaxHmiFiles = 10;

    /// <summary>Режим, в котором читается папка версии. Отвечает на него диск, а не настройка, —
    /// см. <see cref="VersionLayout.IsNewLayout"/>.</summary>
    public const string LayoutRebuilt = "перестроенная";
    public const string LayoutLegacy = "совместимость";
    public const string LayoutUnknown = "папки нет";

    /// <summary>Одна осмотренная папка. «Осмотренная» — значит мы реально в неё заглянули и готовы
    /// сказать, что там было: без этого запись «HMI не нашли» ничем не лучше жалобы.</summary>
    public sealed record FolderProbe
    {
        /// <summary>Чья это папка — «локальная копия», «папка версии на диске», «общая папка HMI
        /// контроллера». Словами, а не путём: путь у каждого свой, а роль одинаковая.</summary>
        [JsonPropertyName("kind")] public string Kind { get; init; } = "";

        [JsonPropertyName("path")] public string Path { get; init; } = "";
        [JsonPropertyName("exists")] public bool Exists { get; init; }

        /// <summary>Режим раскладки ЭТОЙ папки версии (у папки документа пусто).</summary>
        [JsonPropertyName("layout")] public string Layout { get; init; } = "";

        /// <summary>Имена верхнего уровня; у папок в конце обратная косая черта.</summary>
        [JsonPropertyName("entries")] public List<string> Entries { get; init; } = new();

        /// <summary>Сколько их всего — <see cref="Entries"/> обрезан по <see cref="MaxNames"/>.</summary>
        [JsonPropertyName("entriesTotal")] public int EntriesTotal { get; init; }

        /// <summary>Файлы панели, найденные ПОЛНЫМ обходом этой папки со вложенными (имена
        /// относительно неё). Главная улика: если здесь пусто — панели в этом месте правда нет, и
        /// дальше вопрос к тому, кто её туда не положил; если не пусто — значит её не увидел тот,
        /// кто смотрел, и виноват порядок обхода или список расширений.</summary>
        [JsonPropertyName("hmiFiles")] public List<string> HmiFiles { get; init; } = new();

        /// <summary>Что помешало посмотреть (нет прав, шара отвалилась) — иначе пусто.</summary>
        [JsonPropertyName("note")] public string? Note { get; init; }
    }

    /// <summary>Запись целиком — один отрицательный ответ на одной машине.</summary>
    public sealed record Snapshot
    {
        [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
        [JsonPropertyName("at")] public string At { get; init; } = "";
        [JsonPropertyName("machine")] public string Machine { get; init; } = "";
        [JsonPropertyName("user")] public string User { get; init; } = "";
        [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";

        /// <summary>Какой шкаф и какая версия — без этого запись не с чем сопоставить: жалоба
        /// называет шкаф («на примере XL»), а не путь.</summary>
        [JsonPropertyName("equipment")] public string Equipment { get; init; } = "";
        [JsonPropertyName("subtype")] public string Subtype { get; init; } = "";
        [JsonPropertyName("controller")] public string Controller { get; init; } = "";
        [JsonPropertyName("version")] public string Version { get; init; } = "";

        /// <summary>Какое правило дало «HMI нет» — см. <see cref="Rules"/>.</summary>
        [JsonPropertyName("rule")] public string Rule { get; init; } = "";

        /// <summary>Режим чтения САМОЙ версии (по её папке на сетевом диске).</summary>
        [JsonPropertyName("layout")] public string Layout { get; init; } = "";

        /// <summary>Записан ли у версии путь проекта панели и подсказка исполняемого файла. Только
        /// «да/нет»: сам путь уже есть среди осмотренных папок, а два поля здесь отвечают на вопрос
        /// «запись пуста или указывает в пустоту» — это разные беды.</summary>
        [JsonPropertyName("hmiPathStored")] public bool HmiPathStored { get; init; }
        [JsonPropertyName("hmiHintStored")] public bool HmiHintStored { get; init; }

        /// <summary>Нашёл ли бы панель ПОЛНЫЙ обход — то есть расходится ли быстрый ответ карточки с
        /// тем, что лежит на диске. true здесь означает «правило ошиблось», и это самое ценное, что
        /// может быть в записи.</summary>
        [JsonPropertyName("fullScanFoundHmi")] public bool FullScanFoundHmi { get; init; }

        [JsonPropertyName("probes")] public List<FolderProbe> Probes { get; init; } = new();
    }

    /// <summary>Правила, по которым ответ оказывается «HMI нет». Строками, а не числами: запись
    /// читает человек, и «пусто в записи, файла панели нет нигде» объясняет больше, чем «rule 3».</summary>
    public static class Rules
    {
        /// <summary>Ни пути в записи, ни подсказки, ни файла панели в папках версии.</summary>
        public const string NothingAnywhere = "в записи пусто и файла панели нет ни в одной папке версии";

        /// <summary>Файл панели на диске ЕСТЬ, а быстрый обход карточки его не увидел. Ради этого
        /// случая всё и заведено: такой ответ — ошибка программы, а не отсутствие панели.</summary>
        public const string MissedExistingFile = "файл панели на диске есть, но обход карточки его не увидел";

        /// <summary>Папок версии нет вовсе (ни локальной копии, ни сетевой) — смотреть было негде.</summary>
        public const string NoFoldersToLookIn = "ни одной папки версии не оказалось на месте";
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(Snapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    /// <summary>Путь без личного. Папка профиля (а значит и локальный кэш прошивок, который лежит
    /// внутри неё) заменяется на <c>%ПРОФИЛЬ%</c>: для разбора важно «это локальная копия», а не чьей
    /// учётной записи. Сетевые пути вида <c>\\сервер\Software\ПО\…</c> остаются как есть — это общий
    /// диск конторы, по нему и ищут.</summary>
    public static string Redact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var result = path!;
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                 })
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            if (result.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                result = "%ПРОФИЛЬ%" + result[folder.Length..];
        }
        return result;
    }

    /// <summary>Заглянуть в папку и записать, что в ней лежало. Ничего не бросает: диагностика не
    /// имеет права мешать выдаче — ради неё человек и искал.</summary>
    public static FolderProbe Inspect(string kind, string? path, bool withLayout)
    {
        var redacted = Redact(path);
        if (string.IsNullOrWhiteSpace(path))
            return new FolderProbe { Kind = kind, Path = redacted, Exists = false };

        bool exists;
        try { exists = Directory.Exists(path); }
        catch (Exception ex) { return new FolderProbe { Kind = kind, Path = redacted, Note = Short(ex) }; }

        var layout = !withLayout ? ""
            : !exists ? LayoutUnknown
            : VersionLayout.IsNewLayout(path) ? LayoutRebuilt
            : LayoutLegacy;

        if (!exists)
            return new FolderProbe { Kind = kind, Path = redacted, Exists = false, Layout = layout };

        var names = new List<string>();
        var total = 0;
        string? note = null;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
            {
                total++;
                if (names.Count >= MaxNames) continue;
                var name = System.IO.Path.GetFileName(entry.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar));
                names.Add(SafeIsDirectory(entry) ? name + "\\" : name);
            }
        }
        catch (Exception ex) { note = Short(ex); }

        var hmiFiles = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (!PlcOpenResolver.IsHmiProjectFile(file)) continue;
                hmiFiles.Add(Relative(path, file));
                if (hmiFiles.Count >= MaxHmiFiles) break;
            }
        }
        catch (Exception ex) { note ??= Short(ex); }

        return new FolderProbe
        {
            Kind = kind,
            Path = redacted,
            Exists = true,
            Layout = layout,
            Entries = names,
            EntriesTotal = total,
            HmiFiles = hmiFiles,
            Note = note,
        };
    }

    /// <summary>Правило, которым объясняется отрицательный ответ, — выводится из того же, что уже
    /// лежит в осмотрах, чтобы запись не могла противоречить сама себе.</summary>
    public static string RuleFor(IReadOnlyList<FolderProbe> probes)
    {
        if (probes.Any(p => p.HmiFiles.Count > 0)) return Rules.MissedExistingFile;
        return probes.Any(p => p.Exists) ? Rules.NothingAnywhere : Rules.NoFoldersToLookIn;
    }

    /// <summary>Имя файла записи. Машина и номер версии прямо в имени — при разборе двух недель
    /// наблюдения это позволяет отобрать нужное, не открывая каждый файл (тот же приём, что у
    /// <see cref="ModerationDiagnostics.FileNameFor"/>).</summary>
    public static string FileNameFor(Snapshot snapshot)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        return $"hmi_{stamp}_{Safe(snapshot.Machine)}_{Safe(snapshot.Version)}.json";
    }

    /// <summary>Сколько РАЗНЫХ версий в день эта машина вправе описать. Полсотни — это заведомо
    /// больше, чем бывает настоящих случаев за день, и заведомо меньше, чем даст зациклившийся
    /// поиск. Упёрлись в потолок — на сегодня молчим: день, в который «нет HMI» у полусотни версий,
    /// это уже не отдельный случай, а общая беда, и она будет видна и завтра.</summary>
    public const int MaxVersionsPerDay = 50;

    /// <summary>Отсечка «один и тот же случай с одной машины — не чаще раза в день», тем же приёмом,
    /// что у снимков модерации, только с поправкой на версии: карточек в выдаче десятки, и одна
    /// отметка на день заткнула бы все версии, кроме первой попавшейся.
    ///
    /// Возвращает новое значение отметки — его и надо сохранить; <c>null</c> означает «про эту
    /// версию сегодня уже написали (или потолок на день исчерпан), писать не надо».
    ///
    /// Отметка МЕСТНАЯ, в общий конфиг не уезжает (ConfigSyncService.SkipSettingsKeys): уехав, она
    /// заткнула бы запись у всех остальных — а случай нужен как раз с той машины, где он виден.</summary>
    public static string? MarkIfNew(string? stored, string today, string version)
    {
        // Разделитель внутри номера версии развалил бы отметку на лишние куски; в настоящих номерах
        // его не бывает, но отметка переживает любую правку номера руками.
        version = (version ?? "").Replace('|', '_');
        if (version.Length == 0) return null;

        var parts = (stored ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        var sameDay = parts.Length > 0 && string.Equals(parts[0], today, StringComparison.Ordinal);
        var seen = sameDay ? parts.Skip(1).ToList() : new List<string>();

        if (seen.Contains(version, StringComparer.OrdinalIgnoreCase)) return null;
        if (seen.Count >= MaxVersionsPerDay) return null;

        seen.Add(version);
        return today + "|" + string.Join("|", seen);
    }

    private static string Safe(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) value = value.Replace(bad, '-');
        return value.Length > 40 ? value[..40] : value;
    }

    private static string Relative(string root, string file)
    {
        try { return System.IO.Path.GetRelativePath(root, file); }
        catch (Exception) { return System.IO.Path.GetFileName(file); }
    }

    private static bool SafeIsDirectory(string entry)
    {
        try { return Directory.Exists(entry); }
        catch (Exception) { return false; }
    }

    /// <summary>Текст ошибки коротко и без путей: длинное сообщение файловой системы несёт внутри
    /// тот самый путь, который мы только что обезличили.</summary>
    private static string Short(Exception ex) => ex.GetType().Name;
}
