using System.IO;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>К каким ТИПАМ И ПОДТИПАМ ШКАФОВ относится документ-таблица параметров.
///
/// Жалоба владельца была дословной: «в таблице теряется привязка к типам подтипам и тп это не
/// предусмотрено». Проверка показала, что данные лежат правильно, а вот в интерфейсе связь была
/// не видна вовсе — открыв документ, нельзя было понять, к какому шкафу он относится.
///
/// <b>Своей привязки у документа нет и заводить её нельзя.</b> param_tables ключуется ФАЙЛОМ
/// (disk_path + filename), а не строкой param_files, и это не оплошность: у одного файла параметров
/// в param_files по строке на КАЖДЫЙ привязанный подтип (см. ParamFileLinkService), и внешний ключ
/// размножил бы документ по числу подтипов — пять копий одной таблицы, расходящихся с первой же
/// правкой. Заведи документу СВОЙ список подтипов — и появился бы второй, независимый ответ на один
/// и тот же вопрос: файл привязан к трём подтипам, документ — к двум, и кто прав, не знает никто.
///
/// Поэтому привязка ВЫВОДИТСЯ: тот же файл → те же записи param_files → те же подтипы. Правится она
/// там же, где и у файла (EditParamSubtypesDialog), и правка видна сразу обоим.
///
/// Отдельный случай — документ, у которого записи файла нет вовсе (файл на диске есть, а в базе не
/// значится: удалили запись, приехал документ с машины с другим корнем диска). Тогда привязки нет,
/// и её надо ЗАВЕСТИ — см. <see cref="Register"/>.</summary>
public static class ParamTableBinding
{
    /// <summary>Один подтип, под которым виден файл документа.</summary>
    public record Link(int ParamFileId, int SubtypeId, string GroupName, string SubtypeName, bool IsPrimary)
    {
        /// <summary>«ПЖ / 2.0» — тип шкафа и подтип вместе. Подтипа может не быть («—»), тогда
        /// остаётся один тип: писать «ПЖ / —» значит показать человеку прочерк вместо ответа.</summary>
        public string Display => SubtypeName is "—" or "" ? GroupName : $"{GroupName} / {SubtypeName}";
    }

    public record Result(List<Link> Links, ParamFile? Primary)
    {
        /// <summary>Знает ли программа вообще что-нибудь про этот файл. false — записи в param_files
        /// нет, и это ровно тот случай, когда привязку надо заводить.</summary>
        public bool Known => Primary is not null;

        /// <summary>Строка для шапки окна документа.</summary>
        public string Describe() => Links.Count == 0
            ? "Ни к одному подтипу шкафа не привязан"
            : "Относится к: " + string.Join(", ", Links.Select(l => l.Display));
    }

    /// <summary>Привязка документа по его файлу.
    ///
    /// <paramref name="hierarchy"/> и <paramref name="rootPath"/> нужны ровно для одного — понять,
    /// какая из записей ОСНОВНАЯ, то есть в чьей папке файл физически лежит. Это важно: основной
    /// подтип отвязать нельзя (это и есть сам файл, а не ссылка на него), и ошибиться здесь значит
    /// запретить человеку снять не ту галочку. Иерархии под рукой нет — берётся самая ранняя запись:
    /// первой заводится именно основная (ParamFileLinkService.LinkToExtraSubtypes добавляет
    /// остальные уже после неё).</summary>
    public static Result For(Database db, string? diskPath, string? filename,
        HierarchyService? hierarchy = null, string? rootPath = null)
    {
        var links = new List<Link>();
        if (string.IsNullOrWhiteSpace(diskPath) || string.IsNullOrWhiteSpace(filename))
            return new Result(links, null);

        var rows = db.GetParamFilesSharingFile(diskPath, filename)
            .Where(r => r.Id is not null && r.SubtypeId is not null)
            .GroupBy(r => r.SubtypeId!.Value)
            .Select(g => g.OrderBy(r => r.Id!.Value).First())
            .OrderBy(r => r.Id!.Value)
            .ToList();
        if (rows.Count == 0) return new Result(links, null);

        var primary = ResolvePrimary(rows, diskPath, hierarchy, rootPath);
        foreach (var row in rows)
            links.Add(new Link(row.Id!.Value, row.SubtypeId!.Value, row.GroupName,
                row.SubtypeName.Length > 0 ? row.SubtypeName : row.FolderName,
                ReferenceEquals(row, primary)));

        return new Result(links, primary);
    }

    private static ParamFile ResolvePrimary(List<ParamFile> rows, string diskPath,
        HierarchyService? hierarchy, string? rootPath)
    {
        if (hierarchy is null || string.IsNullOrWhiteSpace(rootPath)) return rows[0];

        foreach (var row in rows)
        {
            string folder;
            try
            {
                folder = hierarchy.ParamsPath(rootPath, row.GroupName,
                    row.SubtypeName.Length > 0 ? row.SubtypeName : row.FolderName, row.Manufacturer);
            }
            catch (ArgumentException)
            {
                // Кривое имя в справочнике — не повод не ответить вовсе: остаётся запасной путь.
                continue;
            }
            if (SamePath(folder, diskPath)) return row;
        }
        return rows[0];
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Завести запись файла параметров для документа, у которого её нет.
    ///
    /// Это НЕ загрузка файла: файл уже лежит на диске рядом с документом, копировать нечего. Это
    /// именно регистрация — «программа, знай, что этот файл относится вот к такому шкафу», ровно то
    /// же, что делает разбор старого диска для прошивок.
    ///
    /// Заводится ОДНА запись — основная. Остальные подтипы добавляются обычным путём
    /// (ParamFileLinkService.Apply), чтобы ярлыки в их папках заводились там же, где и всегда, а не
    /// вторым способом.</summary>
    public static ParamFile Register(Database db, ParamTable table, int subtypeId, string? description = null)
    {
        var file = new ParamFile
        {
            SubtypeId = subtypeId,
            Manufacturer = table.Manufacturer,
            Filename = table.Filename,
            DiskPath = table.DiskPath,
            Description = description ?? "",
            // Дата — сегодняшняя: это дата, когда файл ЗАВЕЛИ в программе, а когда он появился на
            // диске, программа честно не знает.
            UploadDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        file.Id = db.AddParamFile(file);
        return file;
    }

    /// <summary>Подтипы всех типов шкафов — то, из чего выбирают привязку. Файл параметров
    /// частотника подходит сразу нескольким типам шкафа (в отличие от прошивки ПЛК), поэтому список
    /// не ограничен «своим» типом (см. ParamFileLinkService).</summary>
    public static List<ParamFileLinkService.SubtypeTarget> Candidates(Database db)
    {
        var groups = db.GetAllEquipmentGroups().ToDictionary(g => g.Id ?? 0, g => g.Name);
        return db.GetAllEquipmentSubtypes()
            .Where(s => s.Id is not null)
            .Select(s => new ParamFileLinkService.SubtypeTarget(s,
                groups.TryGetValue(s.GroupId, out var name) ? name : ""))
            .ToList();
    }
}
