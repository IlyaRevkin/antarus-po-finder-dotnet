using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>Чем рисовать файл-заглушку. Отдельным интерфейсом по той же причине, что и
/// <see cref="IShortcutCreator"/>: одностраничный PDF с кириллицей собирается средствами WPF (текст
/// рисуется в визуал и кладётся картинкой), а Core про WPF ничего не знает и обязан работать в
/// тестах без окна. null вместо реализации — заглушки просто не создаются, всё остальное работает
/// как раньше.</summary>
public interface IInstructionStubWriter
{
    /// <summary>Одностраничный документ с одной строкой текста по центру. Бросает — вызывающий
    /// превращает это в предупреждение и работу из-за заглушки не отменяет.</summary>
    void Write(string path, string text);
}

/// <summary>Заглушка «Инструкция в разработке» — файл, который кладётся в папку инструкции, когда
/// настоящего документа ещё нет (docs/hierarchy-rework-plan.md, этап 1b).
///
/// <b>Зачем.</b> Пустая папка «Инструкция» на диске неотличима от «инструкцию потеряли»: наладчик
/// открывает её в проводнике и не понимает, ждать документ или искать по почте. Заглушка отвечает на
/// этот вопрос одним взглядом, не заводя ни таблиц, ни состояний.
///
/// <b>Имя — каноническое, то же самое, что у настоящего документа</b>
/// (<c>инструкция_&lt;версия&gt;.pdf</c>, см. <see cref="InstructionNaming"/>). Это не косметика, а
/// главное свойство: наклейку с QR печатают и клеят на шкаф ДО того, как инструкцию дописали, и
/// ссылка обязана вести на существующий файл и тогда, и потом. Настоящий документ ложится ровно на
/// то же место — «путь не меняется, меняется только файл», а уже наклеенный QR продолжает работать
/// (ровно это решение записано в docs/hierarchy-rework-plan.md, п. 1 раздела «Целевая раскладка»).
/// Версия неизвестна (общая папка «Инструкция» контроллера принадлежит всем его версиям сразу) —
/// тогда имя <see cref="GenericFileName"/>: одна папка, одна заглушка.
///
/// <b>Раз имя совпадает с настоящим документом, отличать их приходится по содержимому.</b> В конец
/// файла дописывается строка-метка <see cref="Marker"/> — обычный комментарий PDF после %%EOF,
/// читалки его игнорируют. Метку ставит Core (здесь), а не рисовальщик: тогда любая реализация
/// <see cref="IInstructionStubWriter"/> — и настоящая, и тестовая — даёт одинаково опознаваемый файл.
/// Проверка идёт по хвосту файла и только у файлов с подходящим именем, результат запоминается по
/// «путь + время + размер»: обходы папок на сетевом диске повторяются постоянно, и лишнее открытие
/// файла там дороже, чем кажется.
///
/// <b>Три правила, без которых заглушка вредна больше, чем полезна:</b>
/// <list type="number">
/// <item><description><b>Заглушка — не инструкция.</b> Все резолверы документов пропускают её так же,
/// как ярлыки (<see cref="DocFileResolver.IsNotADocument"/>): признак «инструкция ✓» на карточке,
/// «Печать инструкции» и «Редактировать» обязаны относиться к настоящему документу. Иначе программа
/// врала бы бодрее, чем пустая папка. Единственное исключение — наклейка с QR: ей нужен ПУТЬ, а не
/// документ, и путь этот одинаковый.</description></item>
/// <item><description><b>Заглушка уходит сама.</b> Как только в папку кладут настоящую инструкцию,
/// заглушка удаляется (<see cref="RemoveFrom"/> зовётся из <see cref="InstructionStorage"/>) — иначе
/// рядом с документом вечно лежал бы файл, утверждающий, что документа нет, а имя канонического
/// файла оставалось бы занятым.</description></item>
/// <item><description><b>Настоящий документ не затирается никогда.</b> Заглушка пишется только в
/// папку, где документа нет вовсе (<see cref="HasRealInstruction"/>).</description></item>
/// </list></summary>
public static class InstructionStub
{
    /// <summary>Текст внутри документа — дословно то, что просили показывать.</summary>
    public const string Text = "Инструкция в разработке";

    /// <summary>Имя заглушки там, где версия неизвестна: общая папка «Инструкция» контроллера
    /// принадлежит всем его версиям сразу, и назвать лежащий там файл именем одной из них было бы
    /// выдумкой.</summary>
    public const string GenericFileName = "Инструкция в разработке.pdf";

    /// <summary>Метка в конце файла, по которой заглушка отличается от настоящего документа с тем же
    /// именем. Комментарий PDF (строка с «%»), дописанный после %%EOF: читалки ищут %%EOF в
    /// последнем килобайте и лишнюю строку-комментарий спокойно переживают.</summary>
    public const string Marker = "%ANTARUS-INSTRUCTION-STUB";

    /// <summary>Сколько байт хвоста читать в поисках метки. С запасом: метка дописывается последней,
    /// но между ней и концом файла может оказаться перевод строки любой длины.</summary>
    private const int TailBytes = 256;

    /// <summary>Имя файла заглушки для версии. Пустая версия — общее имя (см. <see cref="GenericFileName"/>).</summary>
    public static string FileNameFor(string? versionRaw)
    {
        var name = InstructionNaming.BuildFileName(versionRaw, ".pdf");
        return name.Length > 0 ? name : GenericFileName;
    }

    /// <summary>Полный путь, по которому в этой папке лежала бы заглушка версии — он же путь, по
    /// которому потом ляжет настоящий документ.</summary>
    public static string PathFor(string folder, string? versionRaw) =>
        Path.Combine(folder, FileNameFor(versionRaw));

    // ── Опознание ────────────────────────────────────────────────────────────

    /// <summary>Ключ памятки: путь + время изменения + размер. Файл подменили — ключ другой, ответ
    /// пересчитается; ничего инвалидировать вручную не нужно.</summary>
    private static readonly ConcurrentDictionary<string, bool> Memo = new();

    /// <summary>Это наша заглушка, а не настоящий документ. Недоступный/несуществующий файл — «нет»:
    /// на отвалившейся шаре мы ничего не знаем и ничего не утверждаем.</summary>
    public static bool IsStub(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        var name = Path.GetFileName(path);
        // Общее имя говорит само за себя — читать файл незачем.
        if (string.Equals(name, GenericFileName, StringComparison.OrdinalIgnoreCase)) return true;
        // Дальше проверять стоит только то, что вообще могло быть заглушкой: наш префикс и .pdf.
        if (!name.StartsWith(InstructionNaming.Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;

            var key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            if (Memo.TryGetValue(key, out var known)) return known;

            var answer = TailContainsMarker(path, info.Length);
            // Памятка не должна расти бесконечно на машине, которая сутками не закрывается.
            if (Memo.Count > 4096) Memo.Clear();
            Memo[key] = answer;
            return answer;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TailContainsMarker(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var take = (int)Math.Min(TailBytes, length);
        if (take <= 0) return false;
        fs.Seek(-take, SeekOrigin.End);

        var buffer = new byte[take];
        var read = fs.Read(buffer, 0, take);
        return Encoding.ASCII.GetString(buffer, 0, read).Contains(Marker, StringComparison.Ordinal);
    }

    /// <summary>Заглушка, реально лежащая в этой папке, или null. Нужна наклейке с QR: ссылку надо
    /// строить на существующий файл, даже когда настоящего документа ещё нет.</summary>
    public static string? ExistingIn(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(IsStub);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>В папке есть НАСТОЯЩИЙ документ (не заглушка и не ярлык). Недоступная папка — «нет»:
    /// на отвалившейся шаре ничего не создаём и не удаляем.</summary>
    public static bool HasRealInstruction(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            return Directory.Exists(folder) && Directory
                .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Any(f => !DocFileResolver.IsNotADocument(f));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>В папке лежит ярлык .lnk — документ есть, просто уехал на третий диск и на первом от
    /// него остался указатель (см. <see cref="InstructionStorage"/>). Заглушке здесь не место:
    /// «Инструкция в разработке» рядом с ярлыком на готовый документ — прямая ложь, причём та самая,
    /// от которой заглушка и должна избавлять.</summary>
    public static bool PointsElsewhere(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            return Directory.Exists(folder) && Directory
                .EnumerateFiles(folder!, "*", SearchOption.TopDirectoryOnly)
                .Any(DocFileResolver.IsShortcut);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Документ для этого места уже существует: он лежит в самой папке, в ПАРНОЙ к ней
    /// (зеркало на третьем диске или, наоборот, оригинал на первом) или на него указывает ярлык.
    ///
    /// Парную папку смотреть обязательно. Инструкция физически лежит на ОДНОМ из двух дисков, и
    /// «пусто на первом» само по себе не значит «документа нет»: без этой проверки перестройка
    /// диска, только что унёсшая инструкции на третий диск, тут же положила бы рядом с ними
    /// заглушку «в разработке» на первом.</summary>
    public static bool DocumentExists(params string?[] folders) =>
        folders.Any(f => HasRealInstruction(f) || PointsElsewhere(f));

    // ── Создание и уборка ────────────────────────────────────────────────────

    /// <summary>Положить заглушку в папку, если настоящей инструкции там нет. Идемпотентно: уже
    /// лежащая заглушка вторично не пишется. Если настоящий документ ЕСТЬ — наоборот, подчищает
    /// оставшуюся заглушку (самолечение для папок, куда инструкцию положили руками, мимо программы).
    ///
    /// Возвращает true, только когда файл действительно создан.</summary>
    /// <param name="pairedFolder">Папка-двойник на другом диске (зеркало на третьем или оригинал на
    /// первом), если она известна вызывающему. Документ в ней — такой же повод НЕ класть заглушку,
    /// как документ в самой папке: см. <see cref="DocumentExists"/>.</param>
    public static bool EnsureIn(string? folder, string? versionRaw, IInstructionStubWriter? writer,
        List<string>? warnings = null, string? pairedFolder = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        if (DocumentExists(folder, pairedFolder))
        {
            RemoveFrom(folder);
            return false;
        }

        // Заглушка под другим именем уже лежит (версию узнали позже, папку переименовали) — второй
        // файл с тем же смыслом в папке не нужен.
        if (ExistingIn(folder) is not null) return false;

        var path = PathFor(folder!, versionRaw);
        try
        {
            if (writer is null) return false;
            Directory.CreateDirectory(folder!);
            writer.Write(path, Text);
            MarkAsStub(path);
            return true;
        }
        catch (Exception ex)
        {
            warnings?.Add($"Заглушка инструкции не создана ({folder}): {ex.Message}");
            return false;
        }
    }

    /// <summary>Дописать метку в конец готового файла. Именно здесь, а не в рисовальщике: реализации
    /// <see cref="IInstructionStubWriter"/> живут в приложении и в тестах, и правило «заглушка
    /// помечена» не должно зависеть от того, какая из них сработала.</summary>
    private static void MarkAsStub(string path)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        var bytes = Encoding.ASCII.GetBytes("\n" + Marker + "\n");
        fs.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Заглушка и в папку на первом диске, и в её зеркало на третьем — «во все места, где
    /// инструкцию ищут». Третий диск не настроен или недоступен — просто одна папка, как раньше.
    /// Возвращает число созданных файлов.</summary>
    public static int EnsureForVersion(string? folderOnFirstDisk, string? firstRoot, string? thirdRoot,
        string? versionRaw, IInstructionStubWriter? writer, List<string>? warnings = null)
    {
        var places = Places(folderOnFirstDisk, firstRoot, thirdRoot);

        // Решение одно на обе папки, а не по каждой отдельно: документ лежит ровно на ОДНОМ из
        // дисков, и «на моём диске его нет» не повод объявлять его несуществующим — иначе рядом с
        // готовой инструкцией на третьем диске появлялась бы заглушка на первом.
        if (DocumentExists(places.Cast<string?>().ToArray()))
        {
            foreach (var folder in places) RemoveFrom(folder);
            return 0;
        }

        var created = 0;
        foreach (var folder in places)
            if (EnsureIn(folder, versionRaw, writer, warnings))
                created++;
        return created;
    }

    /// <summary>Убрать заглушки из папки — зовётся, как только туда легла настоящая инструкция.
    /// Ищет по признаку, а не по одному имени: заглушка могла быть положена и под общим именем (когда
    /// версия была неизвестна), и под каноническим. Возвращает число удалённых файлов; недоступная
    /// или несуществующая папка — ноль без ошибки.</summary>
    public static int RemoveFrom(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return 0;
        var removed = 0;
        try
        {
            if (!Directory.Exists(folder)) return 0;
            foreach (var file in Directory.EnumerateFiles(folder!, "*", SearchOption.TopDirectoryOnly).Where(IsStub).ToList())
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception)
                {
                    // Заглушку держит открытой просмотрщик PDF — не повод ронять загрузку инструкции.
                }
            }
        }
        catch (Exception)
        {
            return removed;
        }
        return removed;
    }

    /// <summary>То же для обоих дисков сразу — парная к <see cref="EnsureForVersion"/>.</summary>
    public static int RemoveForVersion(string? folderOnFirstDisk, string? firstRoot, string? thirdRoot) =>
        Places(folderOnFirstDisk, firstRoot, thirdRoot).Sum(RemoveFrom);

    /// <summary>Папка на первом диске и её зеркало на третьем — без повторов и без null. Зеркало
    /// добавляется только при доступном корне третьего диска: заводить дерево папок на отключённой
    /// букве — верный способ насоздавать мусора в корне системного диска.</summary>
    private static List<string> Places(string? folderOnFirstDisk, string? firstRoot, string? thirdRoot)
    {
        var places = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(folderOnFirstDisk)) places.Add(folderOnFirstDisk!);

        var mirror = SafeDirExists(thirdRoot) ? InstructionDiskResolver.Mirror(firstRoot, thirdRoot, folderOnFirstDisk) : null;
        if (mirror is not null &&
            !places.Any(p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), mirror.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)))
            places.Add(mirror);

        return places;
    }

    private static bool SafeDirExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }
}
