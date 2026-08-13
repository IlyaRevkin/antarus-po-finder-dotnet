using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>КОНФИГУРАЦИИ шкафа — заранее заготовленные варианты ОДНОЙ И ТОЙ ЖЕ прошивки.
///
/// Зачем. Одна программа ПЛК обслуживает шкафы, различающиеся только комплектацией: два насоса; два
/// насоса плюс жокей и одна задвижка; задвижек нет вовсе. Прошивка при этом физически одна и та же —
/// отличается только НАЗВАНИЕ ШКАФА, по которому её ищет наладчик:
///   «Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(9-14А)-АВР-FD-Ст» — два насоса и всё;
///   «…ПЖ-ПП-2-(24-32А)/Пд-(6А)/Зд1(4А)-АВР-FD-Ст» — те же два насоса плюс жокей и одна задвижка.
/// Программист заранее «запараметрирует» прошивку под весь ряд комплектаций, а наладчик просто вводит
/// название своего шкафа и получает нужный вариант.
///
/// Как устроено. Конфигурация — это ОТДЕЛЬНАЯ СТРОКА fw_versions с тем же disk_path и version_raw, что
/// у самой прошивки, непустым config_name и СВОИМ набором тегов. Файлы на диске НЕ копируются, вообще
/// никогда: шара WebDAV и медленная, а вариантов у одной прошивки бывает десяток — десять копий одних
/// и тех же байт означали бы и десятикратную заливку, и риск, что копии разойдутся (правку внесли в
/// одну, остальные остались старыми). Ровно тем же приёмом уже живут копии прошивки под другие подтипы
/// шкафа (<see cref="FirmwareSubtypeLinkService"/>), и весь код вокруг его понимает: файлы не удаляются,
/// пока на них ссылается хоть одна запись (Database.IsDiskPathSharedByOtherVersions), вывод из
/// модерации снимается со всех записей разом (MarkFwVersionReleasedWithLinked), удаление уезжает
/// надгробием.
///
/// Что видит наладчик. В выдаче — ОДНУ строку на прошивку: поиск отдаёт одну запись на пару
/// подтип+контроллер, с максимальным рангом (Database.Deduplicate), то есть ту конфигурацию, чьи теги
/// совпали с запросом. Десяти одинаковых прошивок в выдаче не появляется. Очередь модерации и история
/// версий конфигурации не показывают вовсе (Database.NotConfig) — это не отдельные версии и проверять
/// в них нечего.</summary>
public static class FirmwareConfigService
{
    /// <summary>Одна желаемая конфигурация: имя варианта плюс его собственные теги (как правило —
    /// одно точное название шкафа, но может быть и шаблон со звёздочкой, см. <see cref="TagPattern"/>:
    /// «…ПЖ-ПП-2-(*-*А)-АВР-FD-Ст» одной строкой закрывает весь ряд амперажей).</summary>
    public record ConfigSpec(string Name, List<string> Tags);

    /// <summary>Существующая конфигурация: та же пара имя+теги плюс id её строки в fw_versions.</summary>
    public record FirmwareConfig(int FwVersionId, string Name, List<string> Tags);

    public record ApplyResult(List<string> Added, List<string> Updated, List<string> Removed)
    {
        public bool Changed => Added.Count > 0 || Updated.Count > 0 || Removed.Count > 0;
    }

    /// <summary>Разделитель «имя | теги» в строке массового ввода и разделитель тегов внутри неё.</summary>
    private const char NameSeparator = '|';
    private const char TagSeparator = ';';

    /// <summary>Разбирает массовый ввод: ОДНА СТРОКА — ОДНА КОНФИГУРАЦИЯ. Это и есть «завести пачкой»:
    /// программист вставляет список названий шкафов из своей таблицы и получает готовый ряд вариантов,
    /// а не дублирует прошивку по одной штуке.
    ///
    /// Обычная строка — просто название шкафа: оно становится и именем конфигурации, и её единственным
    /// тегом («всё отличие будет только в тегах, а тегом будет название шкафа»).
    /// Если варианту нужно короткое человеческое имя — «Имя | тег; тег»: слева имя, справа теги через
    /// «;». Пустые строки пропускаются, повторы по имени схлопываются (без учёта регистра) — вставленный
    /// из таблицы список почти всегда содержит и то, и другое.</summary>
    public static List<ConfigSpec> ParseBulk(string? text)
    {
        var result = new List<ConfigSpec>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            string name;
            List<string> tags;
            var sep = line.IndexOf(NameSeparator);
            if (sep >= 0)
            {
                name = line[..sep].Trim();
                tags = line[(sep + 1)..].Split(TagSeparator)
                    .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                // «Имя |» без тегов — имя работает и как тег: вариант без единого тега невозможно
                // найти поиском, а значит он бессмыслен.
                if (tags.Count == 0) tags = new List<string> { name };
            }
            else
            {
                name = line;
                tags = new List<string> { line };
            }

            if (name.Length == 0 || !seen.Add(name)) continue;
            result.Add(new ConfigSpec(name, tags));
        }
        return result;
    }

    /// <summary>Обратное преобразование — текущие конфигурации в тот же построчный вид, которым их
    /// вводят. Нужно редактору: он показывает то, что уже есть, оператор правит текст, и результат
    /// снова уходит в <see cref="ParseBulk"/>. Строка «имя = единственный тег» печатается коротко (без
    /// «|»), иначе редактирование превращало бы каждую строку в «Название | Название».</summary>
    public static string FormatBulk(IEnumerable<FirmwareConfig> configs)
    {
        var lines = new List<string>();
        foreach (var c in configs ?? Enumerable.Empty<FirmwareConfig>())
        {
            var ownTags = c.Tags;
            lines.Add(ownTags.Count == 1 && string.Equals(ownTags[0], c.Name, StringComparison.Ordinal)
                ? c.Name
                : $"{c.Name} {NameSeparator} {string.Join($"{TagSeparator} ", ownTags)}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Конфигурации, заведённые у этой прошивки сейчас. Теги отдаются СОБСТВЕННЫЕ — без
    /// базовых тегов самой прошивки, которые <see cref="Apply"/> подмешивает в строку каждой
    /// конфигурации: редактировать оператор должен ровно то, что вводил (названия шкафов), а не видеть
    /// в каждой строке ещё и «НГР», «SMH5» и прочие автотеги прошивки.</summary>
    public static List<FirmwareConfig> Current(Database db, FwVersionRecord primary)
    {
        var baseTags = new HashSet<string>(TagString.Parse(primary.Tags), StringComparer.OrdinalIgnoreCase);
        return db.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw)
            .Where(c => c.Id is not null)
            .Select(c => new FirmwareConfig(c.Id!.Value, c.ConfigName,
                TagString.Parse(c.Tags).Where(t => !baseTags.Contains(t)).ToList()))
            .ToList();
    }

    /// <summary>Приводит набор конфигураций прошивки к желаемому: чего нет — заводит, у существующих
    /// правит теги, лишние помечает удалёнными (<see cref="Database.TombstoneFwVersion"/> — обычный
    /// DELETE не уехал бы к коллегам, и вариант воскрес бы при следующей синхронизации). Сопоставляются
    /// по ИМЕНИ конфигурации без учёта регистра: имя — это то, что оператор видит и правит.
    ///
    /// Файлы прошивки на диске не трогаются никогда — ни при заведении, ни при удалении: они общие, и
    /// вариант это ссылка на прошивку, а не сама прошивка.
    ///
    /// Теги строки конфигурации = базовые теги самой прошивки + собственные названия шкафов. Базовые
    /// нужны, чтобы конфигурация находилась и по общим словам («НГР SMH5 пожар»), а не только по
    /// точному названию шкафа: иначе на общем запросе вариант просто выпадал бы из выдачи.
    ///
    /// Состояние модерации наследуется от прошивки: вариант уже выпущенной прошивки проверять не надо,
    /// это та же самая прошивка (и в очереди модерации конфигурации не показываются вовсе).</summary>
    public static ApplyResult Apply(Database db, FwVersionRecord primary, IReadOnlyList<ConfigSpec> desired)
    {
        var added = new List<string>();
        var updated = new List<string>();
        var removed = new List<string>();
        // Нет папки на диске — вариантам нечего делить; сама строка уже конфигурация — «вариант
        // варианта» смысла не имеет (config_name у неё занят, и вложенности здесь нет по замыслу).
        if (primary.Id is null || string.IsNullOrWhiteSpace(primary.DiskPath) ||
            !string.IsNullOrEmpty(primary.ConfigName))
            return new ApplyResult(added, updated, removed);

        var baseTags = TagString.Parse(primary.Tags);
        var existing = db.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw)
            .Where(c => c.Id is not null)
            .ToList();
        var byName = new Dictionary<string, FwVersionRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in existing) byName[row.ConfigName] = row;

        var wanted = new HashSet<string>(desired.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var spec in desired)
        {
            var tags = TagString.Join(baseTags.Concat(spec.Tags));
            foreach (var tag in spec.Tags) db.AddTag(tag);

            if (byName.TryGetValue(spec.Name, out var row))
            {
                if (row.Tags != tags)
                {
                    db.UpdateFwVersion(row.Id!.Value, tags: tags);
                    updated.Add(spec.Name);
                }
                continue;
            }

            var copy = CopyOf(primary);
            copy.ConfigName = spec.Name;
            copy.Tags = tags;
            var id = db.AddFwVersion(copy);
            if (id <= 0) continue;
            if (primary.Released) db.MarkFwVersionReleased(id);
            added.Add(spec.Name);
        }

        foreach (var row in existing.Where(r => !wanted.Contains(r.ConfigName)))
        {
            db.TombstoneFwVersion(row.Id!.Value);
            removed.Add(row.ConfigName);
        }

        return new ApplyResult(added, updated, removed);
    }

    /// <summary>Переносит набор конфигураций со СТАРОЙ версии прошивки на новую — то же самое, что
    /// делает наследование тегов и HMI-проекта при загрузке новой версии (см.
    /// FirmwareUploadService.Prepare), только целыми вариантами.
    ///
    /// Без этого «запараметрировать прошивку заранее» работало бы ровно до первого обновления: залил
    /// новую версию — и весь заготовленный ряд комплектаций остался у предыдущей, а свежая версия
    /// находится только по общим словам. Конфигурации описывают ШКАФЫ, а не сборку программы: шкафы
    /// никуда не делись.
    ///
    /// Собственные теги вариантов берутся относительно СТАРОЙ прошивки, а подмешиваются базовые теги
    /// НОВОЙ — у неё они уже свои (в том числе унаследованные). Возвращает имена перенесённых
    /// конфигураций; пусто — переносить было нечего.</summary>
    public static List<string> CarryOver(Database db, FwVersionRecord previous, FwVersionRecord created)
    {
        if (previous.Id is null || created.Id is null) return new();
        // Одна и та же строка (или, что то же самое, одна и та же папка версии) — переносить нечего:
        // конфигурации уже висят на ней.
        if (previous.Id == created.Id) return new();

        var specs = Current(db, previous)
            .Select(c => new ConfigSpec(c.Name, c.Tags))
            .Where(s => s.Tags.Count > 0)
            .ToList();
        if (specs.Count == 0) return new();

        var result = Apply(db, created, specs);
        return result.Added;
    }

    /// <summary>Строка-заготовка конфигурации: всё то же, что у самой прошивки (папка, файлы, номер
    /// версии, вложения, тип пуска), кроме тегов и имени варианта. Id намеренно не копируется — это
    /// новая строка.</summary>
    private static FwVersionRecord CopyOf(FwVersionRecord primary) => new()
    {
        SubtypeId = primary.SubtypeId,
        ControllerId = primary.ControllerId,
        EqPrefix = primary.EqPrefix,
        SubPrefix = primary.SubPrefix,
        HwVersion = primary.HwVersion,
        SwVersion = primary.SwVersion,
        DtStr = primary.DtStr,
        VersionRaw = primary.VersionRaw,
        Filename = primary.Filename,
        DiskPath = primary.DiskPath,
        LocalPath = primary.LocalPath,
        Description = primary.Description,
        Changelog = primary.Changelog,
        LaunchTypes = new List<string>(primary.LaunchTypes),
        IoMapPath = primary.IoMapPath,
        InstructionsPath = primary.InstructionsPath,
        ModbusMapPath = primary.ModbusMapPath,
        HmiPath = primary.HmiPath,
        ExecutableHint = primary.ExecutableHint,
        HmiExecutableHint = primary.HmiExecutableHint,
        IsOpc = primary.IsOpc,
        RequestNum = primary.RequestNum,
        CabinetSn = primary.CabinetSn,
        AuthorId = primary.AuthorId,
        Status = primary.Status,
    };
}
