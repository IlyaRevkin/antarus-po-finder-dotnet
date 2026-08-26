using System.IO;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>РАЗОВЫЙ ПЕРЕНОС накопленных текстовых файлов параметров в документы-таблицы.
///
/// Жалоба владельца дословно: «Автоматически файлы не перенёс что были». Таблицы появились, а
/// завести документ можно было только руками, по одному, через «Новый документ из файла…» — то есть
/// всё, что копилось годами, осталось снаружи.
///
/// <b>Перенос не молчаливый и обратимый — это условие, а не украшение.</b> Разбор чужого текста
/// ошибается (см. ParamTextParser: форматов у девяти производителей девять), а документ уезжает к
/// коллегам общим конфигом. Поэтому здесь два раздельных шага: <see cref="Scan"/> ничего не пишет и
/// говорит, что получится, а <see cref="Import"/> пишет ровно то, что человек отметил, и возвращает
/// список заведённого — чтобы <see cref="Undo"/> мог снять именно его, а не «всё, что похоже».
///
/// <b>Откуда берётся текст.</b> Документ живёт РЯДОМ с файлом параметров и ключуется именно им
/// (папка + имя, см. Domain/ParamTable.cs). Сам файл параметров чаще всего проприетарная выгрузка
/// конфигуратора (.par, .dwp, .dcparamsbak) — разбирать в ней нечего; читаемое задание лежит
/// СОСЕДНИМ txt в той же папке. Поэтому источник ищется так: сам файл, если он текстовый, иначе
/// соседние текстовые файлы в его папке. Документ при этом всё равно заводится на ЗАРЕГИСТРИРОВАННЫЙ
/// файл — иначе окно таблицы, открытое с карточки этого файла, своего же документа не нашло бы.</summary>
public static class ParamTableBulkImport
{
    /// <summary>Расширения, которые имеет смысл пробовать читать как текст. Список закрытый: пройтись
    /// разбором по .zip и .pdf значит показать человеку сотню строк мусора и предложить их сохранить.</summary>
    public static readonly string[] TextExtensions = { ".txt", ".md", ".ini", ".cfg", ".log" };

    /// <summary>Одна строка предпросмотра переноса: какой файл, из чего, что получится.</summary>
    public class Item
    {
        public required ParamFile File { get; init; }

        /// <summary>Полный путь к тексту, из которого разбирается таблица. Пусто — источника нет.</summary>
        public string SourcePath { get; init; } = "";

        public string SourceName => SourcePath.Length == 0 ? "" : Path.GetFileName(SourcePath);

        public string DocumentName { get; set; } = "";
        public int ParamRows { get; init; }
        public int NoteRows { get; init; }
        public string EncodingName { get; init; } = "";
        public List<ParamTableRow> Rows { get; init; } = new();
        public List<string> Warnings { get; init; } = new();

        /// <summary>Почему перенести нельзя. null — можно.</summary>
        public string? Refusal { get; init; }

        public bool CanImport => Refusal is null;

        /// <summary>Отмечен ли к переносу. По умолчанию отмечено всё, что переносится: человек
        /// открывает окно ради переноса, а не ради того, чтобы отметить полсотни строк вручную.
        /// Снять отметку он может у любой.</summary>
        public bool Selected { get; set; }

        /// <summary>Итог для человека — одной строкой, и он же попадает в отчёт.</summary>
        public string Outcome => Refusal ?? (NoteRows == 0
            ? $"Параметров: {ParamRows}"
            : $"Параметров: {ParamRows}, пояснений: {NoteRows}");

        public string Subtypes { get; init; } = "";
    }

    public record ImportedDocument(int TableId, string Name, string Filename, int Rows);

    public record ImportResult(List<ImportedDocument> Created, List<string> Failed)
    {
        public string Describe() => Created.Count == 0
            ? "Ни один документ не заведён."
            : $"Заведено документов: {Created.Count}, строк всего: {Created.Sum(c => c.Rows)}"
              + (Failed.Count > 0 ? $"; не удалось: {Failed.Count}" : "");
    }

    /// <summary>Что можно перенести. Ничего не пишет.
    ///
    /// <paramref name="readFile"/> подменяется в тестах — иначе им понадобился бы настоящий диск.</summary>
    public static List<Item> Scan(Database db, Func<string, byte[]>? readFile = null,
        Func<string, IEnumerable<string>>? listFolder = null)
    {
        readFile ??= File.ReadAllBytes;
        listFolder ??= folder => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
            : Enumerable.Empty<string>();

        var items = new List<Item>();
        // Один и тот же файл лежит в param_files по строке на каждый подтип (ParamFileLinkService).
        // Переносим ОДИН РАЗ: пять записей одного файла дали бы пять одинаковых документов, и все
        // пять показались бы в окне таблицы одного и того же файла.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Соседний txt тоже разбирается ровно один раз: две записи параметров в общей папке иначе
        // разобрали бы одно и то же задание дважды.
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in db.GetParamFiles().OrderBy(f => f.Id ?? 0))
        {
            if (string.IsNullOrWhiteSpace(file.DiskPath) || string.IsNullOrWhiteSpace(file.Filename)) continue;
            var key = (file.DiskPath + "|" + file.Filename).ToUpperInvariant();
            if (!seen.Add(key)) continue;

            var subtypes = string.Join(", ", ParamTableBinding
                .For(db, file.DiskPath, file.Filename).Links.Select(l => l.Display));

            if (db.GetParamTablesForFile(file.DiskPath, file.Filename).Count > 0)
            {
                items.Add(new Item { File = file, Subtypes = subtypes, Refusal = "У этого файла документ уже есть" });
                continue;
            }

            var sources = SourcesFor(file, listFolder).Where(usedSources.Add).ToList();
            if (sources.Count == 0)
            {
                items.Add(new Item
                {
                    File = file, Subtypes = subtypes,
                    Refusal = "Рядом нет текстового файла с заданием",
                });
                continue;
            }

            foreach (var source in sources)
                items.Add(Read(file, source, subtypes, readFile));
        }

        return items;
    }

    /// <summary>Откуда брать текст: сам файл, если он текстовый, иначе соседи по папке.</summary>
    private static List<string> SourcesFor(ParamFile file, Func<string, IEnumerable<string>> listFolder)
    {
        var own = Path.Combine(file.DiskPath, file.Filename);
        if (IsText(file.Filename)) return new List<string> { own };

        return listFolder(file.DiskPath)
            .Where(IsText)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsText(string path) =>
        TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static Item Read(ParamFile file, string source, string subtypes, Func<string, byte[]> readFile)
    {
        byte[] bytes;
        try
        {
            bytes = readFile(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Item
            {
                File = file, SourcePath = source, Subtypes = subtypes,
                Refusal = "Файл не читается: " + ex.Message,
            };
        }

        var preview = ParamTableEditing.Preview(bytes, Path.GetFileName(source));
        var paramRows = preview.Rows.Count(r => r.Kind == ParamRowKind.Param);
        if (paramRows == 0)
            return new Item
            {
                File = file, SourcePath = source, Subtypes = subtypes, EncodingName = preview.EncodingName,
                Refusal = "Ни одного параметра не нашлось — это не задание на настройку",
            };

        return new Item
        {
            File = file,
            SourcePath = source,
            Subtypes = subtypes,
            DocumentName = preview.SuggestedName,
            EncodingName = preview.EncodingName,
            ParamRows = paramRows,
            NoteRows = preview.Rows.Count - paramRows,
            Rows = preview.Rows,
            Warnings = preview.Warnings,
            Selected = true,
        };
    }

    /// <summary>Завести документы по отмеченным строкам. Возвращает заведённое поимённо — по этому
    /// списку работает <see cref="Undo"/>.</summary>
    public static ImportResult Import(Database db, IEnumerable<Item> items, string author)
    {
        var created = new List<ImportedDocument>();
        var failed = new List<string>();

        foreach (var item in items.Where(i => i.Selected && i.CanImport))
        {
            var name = (item.DocumentName ?? "").Trim();
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(item.SourcePath);

            try
            {
                var (tableId, _) = ParamTableEditing.CreateFromImport(db, new ParamTable
                {
                    DiskPath = item.File.DiskPath,
                    Filename = item.File.Filename,
                    Name = name,
                    Manufacturer = item.File.Manufacturer,
                    // Теги файла достаются документу: у файла они уже подобраны руками, а искать
                    // документ человек будет теми же словами.
                    Tags = item.File.Tags,
                }, item.Rows, "перенесено из " + item.SourceName, author);

                created.Add(new ImportedDocument(tableId, name, item.File.Filename, item.Rows.Count));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                failed.Add($"{item.File.Filename} ← {item.SourceName}: {ex.Message}");
            }
        }

        return new ImportResult(created, failed);
    }

    /// <summary>Отменить перенос — снять ровно те документы, которые он завёл.
    ///
    /// Тумбстоуном, как и обычное удаление документа: строка обязана продолжать ездить по машинам
    /// как положительный сигнал «это убрали», иначе документ вернулся бы с первым же снимком
    /// конфига с машины, которая об отмене не знает.</summary>
    public static int Undo(Database db, IEnumerable<ImportedDocument> created)
    {
        var count = 0;
        foreach (var document in created)
        {
            var table = db.GetParamTable(document.TableId);
            if (table is null || table.DeletedAt.Length > 0) continue;
            db.TombstoneParamTable(document.TableId);
            count++;
        }
        return count;
    }

    /// <summary>Отчёт человеку — тот же, что показан в окне, но текстом: его копируют в переписку и
    /// прикладывают к тикету.</summary>
    public static string Report(IEnumerable<Item> items, ImportResult? result = null)
    {
        var lines = new List<string>();
        foreach (var item in items)
        {
            var mark = !item.CanImport ? "—" : item.Selected ? "+" : "·";
            var source = item.SourceName.Length > 0 ? " ← " + item.SourceName : "";
            lines.Add($"{mark} {item.File.Filename}{source}: {item.Outcome}");
            foreach (var warning in item.Warnings)
                lines.Add("    ! " + warning);
        }

        if (result is not null)
        {
            lines.Add("");
            lines.Add(result.Describe());
            foreach (var failure in result.Failed) lines.Add("  не удалось: " + failure);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
