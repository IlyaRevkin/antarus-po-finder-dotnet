using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Куда ложится шаблон паспорта шкафа и как заводится/обновляется его запись.
///
/// Раскладка на диске: ПО\&lt;тип&gt;[\&lt;подтип&gt;]\Паспорт\&lt;название&gt;\&lt;файл&gt; — общая папка «Паспорт»
/// принадлежит подтипу (см. HierarchyFolders.Passports), внутри по подпапке на каждый паспорт. Своя
/// подпапка нужна ровно затем, чтобы у одного подтипа могло быть несколько паспортов с одинаковым
/// именем файла («Паспорт.docx» под разные исполнения шкафа) и чтобы рядом с документом лежал его
/// PDF для печати, не мешаясь с чужими.
///
/// Перезаливка ведёт себя ровно как у файлов параметров (Илья просил именно так: «без истории как у
/// прошивок, просто дата загрузки, всегда открывать свежую, а кому нужна старая — пусть откроет
/// папку»): прежний файл уезжает в подпапку «Прежние редакции» под именем «имя (до ГГГГ-ММ-ДД).ext»,
/// новый ложится под своим именем, запись ОБНОВЛЯЕТСЯ (свежая дата + датированная строка в описании),
/// а не плодится второй строкой. Обе операции переиспользованы из ParamFileUploadService — там они
/// написаны без единой отсылки к параметрам, и вторая копия того же кода разошлась бы с оригиналом.</summary>
public static class PassportService
{
    /// <summary>Имя подпапки паспорта: название, как его ввёл оператор, с заменой символов, которые
    /// файловая система не примет. Пустое/полностью «неудобное» название — «Паспорт», чтобы папка
    /// всё равно получилась (валидацию непустого названия делает форма загрузки).</summary>
    public static string FolderName(string name)
    {
        var cleaned = new string((name ?? "").Trim()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        // Точка в конце имени папки Windows молча отбрасывает — уберём сами, чтобы записанный в БД
        // путь совпадал с тем, что реально появилось на диске.
        cleaned = cleaned.TrimEnd('.', ' ');
        return cleaned.Length == 0 ? HierarchyFolders.Passports : cleaned;
    }

    /// <summary>Папка конкретного паспорта на диске.</summary>
    public static string Folder(HierarchyService hierarchy, string root, string groupName, string subName, string name) =>
        Path.Combine(hierarchy.PassportsPath(root, groupName, subName), FolderName(name));

    // ── Типовые паспорта (без привязки к шкафу) ───────────────────────────────────────────────
    // Часть паспортов не ложится ни на один тип и подтип: НКУ, Щит СПЛ, ШР — это бланк, который
    // печатают под конкретный шкаф, вписав его название (просьба Ильи: «по факту та же папка с
    // наклейками, только там паспорт, к которому можно подцепить тег с названием для поиска»).
    // Поэтому у такой записи subtype_id пуст, а файл лежит в ОБЩЕЙ папке диска — рядом с наклейками,
    // а не внутри ПО\<тип>\<подтип>\Паспорт, где ему не из чего выбрать тип. Всё остальное — теги,
    // поиск, синхронизация, перезаливка — остаётся тем же, что у паспорта, привязанного к шкафу.

    /// <summary>Общая папка типовых паспортов. По умолчанию — <c>&lt;диск&gt;\Конфиг\Паспорта</c>, рядом
    /// с <c>Конфиг\Наклейки</c>: она уже доступна всем машинам и раздавать её отдельно не нужно.</summary>
    public const string DefaultTemplatesSubfolder = @"Конфиг\Паспорта";

    /// <summary>Куда складывать и откуда читать типовые паспорта. <paramref name="configured"/> — то,
    /// что задано в настройках (см. SharedFolderPath). null — корень диска не настроен.</summary>
    public static string? TemplatesFolder(string? diskRoot, string? configured) =>
        SharedFolderPath.Resolve(diskRoot, configured, DefaultTemplatesSubfolder);

    /// <summary>Папка конкретного типового паспорта: подпапка по названию внутри общей — ровно так
    /// же, как у паспорта шкафа внутри «Паспорт». Своя подпапка нужна затем же: рядом с бланком
    /// ложится собранный из него PDF, и одноимённые «Паспорт.docx» не сталкиваются.</summary>
    public static string GeneralFolder(string templatesFolder, string name) =>
        Path.Combine(templatesFolder, FolderName(name));

    /// <summary>Что подставить в поле «название шкафа», когда бланк открыли из поиска. Оператор уже
    /// написал в поиске название шкафа — «ЩУН-3», — и нашёлся бланк, подходящий этому шкафу по тегу;
    /// требовать набрать то же самое второй раз незачем (просьба Ильи: «чтобы программа сама
    /// подставляла в зависимости от искомого»).
    ///
    /// Из запроса выбрасывается только НАЗВАНИЕ самого бланка: «НКУ», «Щит СПЛ» — это вид документа,
    /// а не шкаф, и в графе шкафа ему делать нечего. Искали «НКУ» — подставлять нечего, поле
    /// останется пустым; искали «НКУ ЩУН-3» — останется ровно «ЩУН-3».
    ///
    /// А вот теги, наоборот, не трогаем: в них как раз и записаны названия шкафов, под которые этот
    /// бланк подходит (для того тег и цепляют — чтобы бланк нашёлся по шкафу). Запрос «ЩУН-9»,
    /// попавший точно в тег, — это и есть название, которое надо вписать в бланк.
    ///
    /// Подстановка — предположение, а не решение за оператора: поле остаётся обычным, текст в нём
    /// выделен, и одно нажатие клавиши его заменяет.</summary>
    public static string CabinetNameFromQuery(string? query, string blankName)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";

        // Название бланка — целиком и по словам: «Щит СПЛ» в запросе стоит двумя словами.
        var own = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { (blankName ?? "").Trim() };
        foreach (var word in (blankName ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            own.Add(word);
        own.Remove("");

        var rest = words.Where(w => !own.Contains(w)).ToArray();
        return rest.Length == 0 ? "" : string.Join(" ", rest);
    }

    /// <summary>Заводит или обновляет запись паспорта. Существующая ищется по натуральному ключу
    /// «подтип + название» среди живых — это и есть «тот же самый паспорт, перезалитый заново»;
    /// sync_id при обновлении сохраняется, чтобы для коллег это осталась ОДНА строка (см.
    /// Database.ConfigExchange.cs).</summary>
    /// <param name="archivedPreviousName">Имя, под которым сохранена прежняя редакция, либо null.</param>
    public static PassportSaveOutcome SaveRecord(Database db, PassportTemplate record,
        string? archivedPreviousName, DateTime now)
    {
        record.UploadDate = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        // Ключ у типового паспорта тот же по смыслу, только вместо подтипа — «нет подтипа»: два
        // бланка с одинаковым названием — это один бланк, перезалитый заново, а не второй.
        var existing = db.FindLivePassport(record.SubtypeId, record.Name);
        if (existing?.Id is null)
        {
            var id = db.AddPassport(record);
            return new PassportSaveOutcome(id, Updated: false, archivedPreviousName);
        }

        var description = ParamFileUploadService.AppendChangeLog(existing.Description, record.Description, now, archivedPreviousName);
        record.Id = existing.Id;
        record.SyncId = existing.SyncId;
        record.Description = description;
        db.UpdatePassportUpload(existing.Id.Value, record.DiskPath, record.Filename, description, record.UploadDate);
        return new PassportSaveOutcome(existing.Id.Value, Updated: true, archivedPreviousName);
    }

    /// <summary>Что реально лежит в папке паспорта: исходный docx (правка) и PDF (печать), плюс
    /// признак «PDF устарел относительно docx». Тот же резолвер, что у инструкции — задача
    /// дословно та же (docx рядом с собранным из него pdf в одной папке), и разводить два
    /// одинаковых резолвера было бы копией ради имени типа.
    ///
    /// Подпапка с прежними редакциями из рассмотрения исключена: файл копируется на диск со СВОЕЙ
    /// датой изменения, а не с датой загрузки, поэтому убранная в архив редакция вполне может
    /// оказаться «свежее» актуальной — и открывалась бы вместо неё.
    ///
    /// <paramref name="localRoot"/> — корень ЭТОЙ машины: путь в записи абсолютный и записан тем,
    /// кто грузил (у него шара могла быть смонтирована другой буквой).
    ///
    /// <paramref name="templatesFolder"/> — общая папка бланков на ЭТОЙ машине, нужна только для
    /// типовых (см. FolderFor ниже). null — считать по записи, как раньше.</summary>
    public static InstructionDoc ResolveDoc(PassportTemplate passport, string localRoot, string? templatesFolder = null)
    {
        var folder = FolderFor(passport, localRoot, templatesFolder);
        var stored = string.IsNullOrEmpty(passport.Filename) ? null : Path.Combine(folder, passport.Filename);
        return InstructionDocResolver.Resolve(stored, folder, ParamFileUploadService.ArchiveFolderName);
    }

    /// <summary>Папка паспорта, как она выглядит с ЭТОЙ машины.
    ///
    /// У паспорта шкафа всё как у прошивок: путь переприкрепляется к нашему корню по якорю «ПО».
    /// А типовой бланк лежит ВНЕ дерева «ПО» — якоря в его пути нет, и FirmwarePathLocalizer вернул
    /// бы чужой путь как есть, с буквой диска той машины, где бланк грузили: у коллеги с другой
    /// буквой он бы просто не открывался. Зато адрес бланка мы умеем собрать заново сами: общая
    /// папка берётся из настройки (она синхронизируемая — значит, одна на всех), а подпапка — это
    /// название бланка. Ни одного чужого куска в таком пути не остаётся.
    ///
    /// Записанный путь при этом не выбрасывается: если собранной папки нет, а записанная на месте —
    /// открываем её (бланк могли залить, когда настройка указывала в другое место).</summary>
    public static string FolderFor(PassportTemplate passport, string localRoot, string? templatesFolder = null)
    {
        var stored = FirmwarePathLocalizer.Localize(passport.DiskPath, localRoot);
        if (passport.SubtypeId is not null || string.IsNullOrWhiteSpace(templatesFolder)) return stored;

        var here = GeneralFolder(templatesFolder, passport.Name);
        return Directory.Exists(here) || !Directory.Exists(stored) ? here : stored;
    }
}

/// <param name="RecordId">Id записи passports: обновлённой либо только что заведённой.</param>
/// <param name="Updated">true — существующая запись обновлена (перезаливка), false — заведена новая.</param>
/// <param name="ArchivedPreviousName">Имя, под которым сохранена прежняя редакция, либо null.</param>
public record PassportSaveOutcome(int RecordId, bool Updated, string? ArchivedPreviousName);
