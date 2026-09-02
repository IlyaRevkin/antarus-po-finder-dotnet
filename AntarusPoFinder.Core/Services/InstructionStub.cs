using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Domain;

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

    /// <summary>Набор макетов, которым рисует этот писатель (см. <see cref="StubLayoutSet"/>). Живёт
    /// на писателе, а не параметром в шести точках вызова: макет читается из общих настроек, то есть
    /// известен приложению, а Core о настройках не знает. Заодно отсюда берётся отпечаток макета,
    /// которым помечается готовый файл.</summary>
    StubLayoutSet Layouts => StubLayoutSet.Default;

    /// <summary>Нарисовать страницу нужного вида. Реализация по умолчанию сводит вызов к старой
    /// подписи — тестовым «рисовальщикам» (они кладут файл-пустышку и проверяют логику, а не
    /// картинку) переписываться не нужно.</summary>
    void Write(string path, StubKind kind, string? versionRaw) =>
        Write(path, Layouts.For(kind).Title);
}

/// <summary>Что сделали с заглушкой в папке.</summary>
public enum StubAction
{
    /// <summary>Ничего: файл на месте и нарисован по действующему макету, либо рисовать нечем.</summary>
    None,

    /// <summary>Файла не было — создали.</summary>
    Created,

    /// <summary>Файл был, но нарисован по ПРОШЛОМУ макету — перерисовали по тому же пути.</summary>
    Refreshed,
}

/// <summary>Страница, которая лежит в папке «Инструкция» вместо документа или рядом с ним, — то, что
/// реально открывается по QR-коду с наклейки на шкафу. Три повода и три вида — см.
/// <see cref="StubKind"/>.
///
/// <b>Зачем.</b> Пустая папка «Инструкция» на диске неотличима от «инструкцию потеряли»: наладчик
/// открывает её в проводнике и не понимает, ждать документ или искать по почте. Заглушка отвечает на
/// этот вопрос одним взглядом, не заводя ни таблиц, ни состояний. А заказчику она в любом случае
/// показывает телефон сервиса (<see cref="ServiceContacts"/>) — ради этого всё и затевалось.
///
/// <b>Имя — каноническое, то же самое, что у настоящего документа</b>
/// (<c>инструкция_&lt;версия&gt;.pdf</c>, см. <see cref="InstructionNaming"/>). Это не косметика, а
/// главное свойство: наклейку с QR печатают и клеят на шкаф ДО того, как инструкцию дописали, и
/// ссылка обязана вести на существующий файл и тогда, и потом. Настоящий документ ложится ровно на
/// то же место — «путь не меняется, меняется только файл», а уже наклеенный QR продолжает работать
/// (ровно это решение записано в docs/hierarchy-rework-plan.md, п. 1 раздела «Целевая раскладка»).
/// Версия неизвестна (общая папка «Инструкция» контроллера принадлежит всем его версиям сразу) —
/// тогда имя <see cref="GenericFileName"/>: одна папка, одна заглушка. У страницы-дополнения
/// (<see cref="StubKind.ServiceNote"/>) имя своё и постоянное: она лежит РЯДОМ с документом, и
/// каноническое имя занято им.
///
/// <b>Раз имя совпадает с настоящим документом, отличать их приходится по содержимому.</b> В конец
/// файла дописывается строка-метка <see cref="Marker"/> — обычный комментарий PDF после %%EOF,
/// читалки его игнорируют. В той же строке стоят вид страницы и ОТПЕЧАТОК МАКЕТА, по которому она
/// нарисована (<see cref="StubLayout.Stamp"/>): без отпечатка правка макета не доезжала до уже
/// лежащих файлов — «меняю макет, а заглушки прежние». Метку ставит Core (здесь), а не рисовальщик:
/// тогда любая реализация <see cref="IInstructionStubWriter"/> — и настоящая, и тестовая — даёт
/// одинаково опознаваемый файл. Проверка идёт по хвосту файла и только у файлов с подходящим именем,
/// результат запоминается по «путь + время + размер»: обходы папок на сетевом диске повторяются
/// постоянно, и лишнее открытие файла там дороже, чем кажется.
///
/// <b>Три правила, без которых заглушка вредна больше, чем полезна:</b>
/// <list type="number">
/// <item><description><b>Заглушка — не инструкция.</b> Все резолверы документов пропускают её так же,
/// как ярлыки (<see cref="DocFileResolver.IsNotADocument"/>): признак «инструкция ✓» на карточке,
/// «Печать инструкции» и «Редактировать» обязаны относиться к настоящему документу. Иначе программа
/// врала бы бодрее, чем пустая папка. Единственное исключение — наклейка с QR: ей нужен ПУТЬ, а не
/// документ, и путь этот одинаковый.</description></item>
/// <item><description><b>Заглушка-«вместо» уходит сама.</b> Как только в папку кладут настоящую
/// инструкцию, она удаляется (<see cref="RemoveFrom"/> зовётся из <see cref="InstructionStorage"/>) —
/// иначе рядом с документом вечно лежал бы файл, утверждающий, что документа нет, а имя канонического
/// файла оставалось бы занятым. На её место встаёт страница-дополнение.</description></item>
/// <item><description><b>Настоящий документ не затирается никогда.</b> Заглушка-«вместо» пишется
/// только в папку, где документа нет вовсе (<see cref="HasRealInstruction"/>).</description></item>
/// </list></summary>
public static class InstructionStub
{
    /// <summary>Текст внутри документа — дословно то, что просили показывать.</summary>
    public const string Text = "Инструкция в разработке";

    /// <summary>Имя заглушки «в разработке» там, где версия неизвестна: общая папка «Инструкция»
    /// контроллера принадлежит всем его версиям сразу, и назвать лежащий там файл именем одной из них
    /// было бы выдумкой.</summary>
    public const string GenericFileName = "Инструкция в разработке.pdf";

    /// <summary>Имя страницы-дополнения, которая лежит РЯДОМ с настоящей инструкцией. Постоянное, а
    /// не каноническое: каноническое занято самим документом, а два файла с одним именем в папке не
    /// живут.</summary>
    public const string NoteFileName = "Если остались вопросы.pdf";

    /// <summary>Имя ОДНОЙ НА ВСЕХ страницы «инструкции не будет» — она лежит в корне диска прошивок
    /// (<see cref="SharedNotPlannedPath"/>) и не привязана ни к типу, ни к подтипу, ни к контроллеру.
    ///
    /// Именно этого и просили: «одна статическая заглушка для рациональных шкафов, на которые в целом
    /// никогда не будет инструкции, без привязки к типу, подтипу и т. п.». Класть её копией в каждую
    /// папку значило бы завести сотни одинаковых файлов, каждый из которых надо перерисовывать при
    /// правке макета и по одному выкладывать на хостинг, — притом что различать их нечем и незачем.
    /// Один файл — один адрес на хостинге, одна перерисовка, один объект в бакете.</summary>
    public const string SharedNotPlannedFileName = "Инструкции не будет.pdf";

    /// <summary>Метка в конце файла, по которой заглушка отличается от настоящего документа с тем же
    /// именем. Комментарий PDF (строка с «%»), дописанный после %%EOF: читалки ищут %%EOF в
    /// последнем килобайте и лишнюю строку-комментарий спокойно переживают. За меткой в той же строке
    /// идут «kind=…» и «stamp=…» — см. <see cref="StubInfo"/>.</summary>
    public const string Marker = "%ANTARUS-INSTRUCTION-STUB";

    /// <summary>Сколько байт хвоста читать в поисках метки. С запасом: метка дописывается последней,
    /// но между ней и концом файла может оказаться перевод строки любой длины.</summary>
    private const int TailBytes = 256;

    /// <summary>Что мы знаем о лежащем файле-заглушке. <paramref name="Stamp"/> пуст у заглушек,
    /// положенных до появления отпечатков, — такие считаются устаревшими и перерисовываются один раз
    /// при первом же удобном случае.</summary>
    public sealed record StubInfo(StubKind Kind, string Stamp);

    /// <summary>Имя файла заглушки этого вида для версии. Пустая версия — общее имя
    /// (см. <see cref="GenericFileName"/>).</summary>
    public static string FileNameFor(string? versionRaw, StubKind kind = StubKind.InDevelopment)
    {
        if (kind == StubKind.ServiceNote) return NoteFileName;
        if (kind == StubKind.NotPlanned) return SharedNotPlannedFileName;

        var name = InstructionNaming.BuildFileName(versionRaw, ".pdf");
        return name.Length > 0 ? name : GenericFileName;
    }

    /// <summary>Полный путь, по которому в этой папке лежала бы заглушка версии — он же путь, по
    /// которому потом ляжет настоящий документ.</summary>
    public static string PathFor(string folder, string? versionRaw, StubKind kind = StubKind.InDevelopment) =>
        Path.Combine(folder, FileNameFor(versionRaw, kind));

    /// <summary>Единственное место одной-на-всех страницы «инструкции не будет» — прямо в корне диска
    /// прошивок. В корне, а не внутри иерархии, именно потому, что она НИ К ЧЕМУ не привязана: любое
    /// место внутри «ПО\&lt;тип&gt;\…» читалось бы как «эта — для такого-то типа», а её адрес на
    /// хостинге получил бы в себе имя типа и подтипа.</summary>
    public static string SharedNotPlannedPath(string root) =>
        Path.Combine(root, SharedNotPlannedFileName);

    // ── Опознание ────────────────────────────────────────────────────────────

    /// <summary>Ключ памятки: путь + время изменения + размер. Файл подменили — ключ другой, ответ
    /// пересчитается; ничего инвалидировать вручную не нужно.</summary>
    private static readonly ConcurrentDictionary<string, StubInfo?> Memo = new();

    /// <summary>Это наша заглушка, а не настоящий документ. Недоступный/несуществующий файл — «нет»:
    /// на отвалившейся шаре мы ничего не знаем и ничего не утверждаем.</summary>
    public static bool IsStub(string? path) => Describe(path) is not null;

    /// <summary>Вид лежащей заглушки, или null — это не заглушка.</summary>
    public static StubKind? KindOf(string? path) => Describe(path)?.Kind;

    /// <summary>Отпечаток макета, по которому нарисована лежащая заглушка. Пусто — заглушка старая
    /// (положена до появления отпечатков) либо это не заглушка вовсе.</summary>
    public static string StampOf(string? path) => Describe(path)?.Stamp ?? "";

    /// <summary>Полное описание лежащего файла, если это заглушка. null — обычный документ, ярлык,
    /// недоступный или несуществующий файл.</summary>
    public static StubInfo? Describe(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var name = Path.GetFileName(path);
        var byName = KindByFileName(name);
        // Дальше проверять стоит только то, что вообще могло быть заглушкой: своё имя либо наш
        // префикс и .pdf.
        if (byName is null)
        {
            if (!name.StartsWith(InstructionNaming.Prefix, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                // Файла ещё (или уже) нет. Своё говорящее имя всё равно означает заглушку: этот ответ
                // спрашивают и про ПРЕДПОЛАГАЕМЫЙ путь — например, планируя переименования при
                // перестройке диска, где решают судьбу файла до того, как он появится. Отпечатка у
                // несуществующего файла, разумеется, нет.
                return byName is { } expected ? new StubInfo(expected, "") : null;
            }

            var key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            if (Memo.TryGetValue(key, out var known)) return known;

            var answer = ReadTail(path, info.Length, byName);
            // Памятка не должна расти бесконечно на машине, которая сутками не закрывается.
            if (Memo.Count > 4096) Memo.Clear();
            Memo[key] = answer;
            return answer;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Вид, читаемый по одному лишь имени файла: у страницы «не будет» и у дополнения имена
    /// постоянные и говорят сами за себя. null — имя ничего не решает, нужен хвост файла.</summary>
    private static StubKind? KindByFileName(string name)
    {
        if (string.Equals(name, GenericFileName, StringComparison.OrdinalIgnoreCase)) return StubKind.InDevelopment;
        if (string.Equals(name, SharedNotPlannedFileName, StringComparison.OrdinalIgnoreCase)) return StubKind.NotPlanned;
        if (string.Equals(name, NoteFileName, StringComparison.OrdinalIgnoreCase)) return StubKind.ServiceNote;
        return null;
    }

    private static StubInfo? ReadTail(string path, long length, StubKind? byName)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var take = (int)Math.Min(TailBytes, length);
        if (take <= 0) return byName is { } emptyKind ? new StubInfo(emptyKind, "") : null;
        fs.Seek(-take, SeekOrigin.End);

        var buffer = new byte[take];
        var read = fs.Read(buffer, 0, take);
        var tail = Encoding.ASCII.GetString(buffer, 0, read);

        var at = tail.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0)
        {
            // Метки нет. Имя, говорящее само за себя, всё равно означает заглушку: так помечались
            // файлы до появления меток, и объявить их сейчас документами значило бы показать
            // «инструкция ✓» там, где её нет.
            return byName is { } named ? new StubInfo(named, "") : null;
        }

        var line = tail[at..];
        var end = line.IndexOfAny(new[] { '\r', '\n' });
        if (end >= 0) line = line[..end];

        // Вид берём из метки; её нет (файл положен до появления трёх видов) — из имени, а если и оно
        // ничего не говорит, это «в разработке»: именно им были все заглушки до сих пор.
        var tag = Field(line, "kind=");
        var kind = tag.Length > 0 ? StubKinds.FromTag(tag) : byName ?? StubKind.InDevelopment;
        return new StubInfo(kind, Field(line, "stamp="));
    }

    /// <summary>Значение «имя=значение» из строки-метки. Пусто — поля нет (метка старого образца).</summary>
    private static string Field(string line, string prefix)
    {
        var at = line.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) return "";
        var rest = line[(at + prefix.Length)..];
        var end = rest.IndexOf(' ');
        return (end >= 0 ? rest[..end] : rest).Trim();
    }

    /// <summary>Заглушка-«вместо», реально лежащая в этой папке, или null. Нужна наклейке с QR: ссылку
    /// надо строить на существующий файл, даже когда настоящего документа ещё нет.
    ///
    /// Страница-дополнение сюда НЕ попадает: она лежит рядом с документом, и принять её за «документа
    /// нет» значило бы вернуть ровно ту ложь, ради которой всё это заведено.</summary>
    public static string? ExistingIn(string? folder) => ExistingIn(folder, replacingOnly: true);

    /// <summary>Страница-дополнение, лежащая в этой папке, или null.</summary>
    public static string? ExistingNoteIn(string? folder) =>
        EnumerateStubs(folder).FirstOrDefault(f => KindOf(f) == StubKind.ServiceNote);

    private static string? ExistingIn(string? folder, bool replacingOnly) =>
        EnumerateStubs(folder).FirstOrDefault(f => !replacingOnly || KindOf(f)?.ReplacesInstruction() != false);

    private static IEnumerable<string> EnumerateStubs(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return Array.Empty<string>();
        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Where(IsStub).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
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

    /// <summary>Это ОБЩАЯ папка «Инструкция» контроллера, а документы у него разложены по папкам
    /// версий — то есть общая папка перекрыта и заглушке в ней не место.
    ///
    /// Живая жалоба: «на SMH5 2.0 ПЖ инструкция есть, а туда почему-то заглушка улетела». На диске
    /// было ровно это — настоящий документ в папке ВЕРСИИ
    /// (<c>…/2.0/SMH5/2.0.0005.0001.…/Инструкция/инструкция_….pdf</c>) и заглушка «в разработке»
    /// уровнем ВЫШЕ, в общей папке контроллера (<c>…/2.0/SMH5/Инструкция/</c>), причём выложенная на
    /// хостинг. Заглушку туда клала перестройка структуры — она заводит общую папку каждому
    /// контроллеру и наполняет её не глядя, — а кто попадал на уровень контроллера, получал
    /// «в разработке» при существующей инструкции.
    ///
    /// Уже лежащие заглушки этим НЕ удаляются: их адреса могли быть напечатаны на наклейках, и
    /// оборвать их — хуже, чем оставить. Речь только о том, чтобы не заводить новые.</summary>
    public static bool ShadowedByVersionDocuments(string? controllerInstructionsFolder)
    {
        if (string.IsNullOrWhiteSpace(controllerInstructionsFolder)) return false;
        try
        {
            var controllerFolder = Path.GetDirectoryName(controllerInstructionsFolder);
            if (string.IsNullOrEmpty(controllerFolder) || !Directory.Exists(controllerFolder)) return false;

            // Обход только на один уровень вниз: папки версий лежат прямо в папке контроллера, и
            // рекурсия здесь означала бы обход всего диска на каждую перестройку.
            foreach (var versionDir in Directory.EnumerateDirectories(controllerFolder))
            {
                if (PathsSame(versionDir, controllerInstructionsFolder)) continue;
                if (HasRealInstruction(VersionLayout.SlotFolder(versionDir, HierarchyFolders.Instructions)))
                    return true;
            }
            return false;
        }
        catch (Exception)
        {
            // Недоступная шара — ведём себя как раньше: лучше лишняя заглушка, чем пустая папка.
            return false;
        }
    }

    /// <summary>Файл существует. Отдельно и с проглатыванием ошибок: недоступная шара — это «нет», а
    /// не повод падать посреди обхода диска.</summary>
    private static bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }

    private static bool PathsSame(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>В папке лежит ярлык .lnk — документ есть, на него лишь указывает ярлык (пережиток
    /// прежних версий программы). Заглушке здесь не место: «Инструкция в разработке» рядом с ярлыком
    /// на готовый документ — прямая ложь, причём та самая, от которой заглушка и должна избавлять.</summary>
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

    /// <summary>Документ для этого места уже существует: он лежит в самой папке или на него указывает
    /// ярлык-пережиток.</summary>
    public static bool DocumentExists(string? folder) =>
        HasRealInstruction(folder) || PointsElsewhere(folder);

    // ── Создание, обновление и уборка ────────────────────────────────────────

    /// <summary>Привести содержимое папки в согласие с тем, что в ней лежит:
    /// <list type="bullet">
    /// <item><description>документа нет — лежит заглушка «в разработке», нарисованная по ДЕЙСТВУЮЩЕМУ
    /// макету;</description></item>
    /// <item><description>документ есть — заглушки-«вместо» нет, зато рядом лежит страница-дополнение
    /// с телефоном сервиса, тоже по действующему макету.</description></item>
    /// </list>
    ///
    /// Идемпотентно: файл, нарисованный по нынешнему макету, вторично не пишется — сравнение идёт по
    /// отпечатку в метке (<see cref="StubLayout.Stamp"/>), а не по факту существования файла. Именно
    /// это и чинит «меняю макет, а заглушки прежние»: правка макета меняет отпечаток, и следующий же
    /// проход перерисовывает файл по тому же пути, не трогая напечатанные наклейки.
    ///
    /// Возвращает то, что сделали с заглушкой-«вместо». Страница-дополнение в ответ не попадает
    /// намеренно: вызывающие считают этим числом «сколько мест осталось без инструкции» и показывают
    /// его человеком в отчёте о перестройке диска — дополнение туда не относится.</summary>
    public static StubAction Ensure(string? folder, string? versionRaw, IInstructionStubWriter? writer,
        List<string>? warnings = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return StubAction.None;

        // Обход папки считается ОДИН раз: на сетевом диске рекурсивное перечисление — самая дорогая
        // часть всей этой работы, а Ensure зовётся на каждую папку «Инструкция» при перестройке.
        var hasReal = HasRealInstruction(folder);
        if (hasReal || PointsElsewhere(folder))
        {
            RemoveFrom(folder, StubKind.InDevelopment, StubKind.NotPlanned);
            if (hasReal) WriteNote(folder!, versionRaw, writer, warnings);
            return StubAction.None;
        }

        // Дополнение рядом с исчезнувшим документом — та же ложь наоборот: оно обещает лежащую рядом
        // инструкцию, которой больше нет.
        RemoveFrom(folder, StubKind.ServiceNote);

        // Заглушка под другим именем уже лежит (версию узнали позже, папку переименовали) — второй
        // файл с тем же смыслом в папке не нужен, но перерисовать существующий по новому макету надо.
        var existing = ExistingIn(folder) ?? PathFor(folder!, versionRaw);
        return WriteStub(existing, StubKind.InDevelopment, versionRaw, writer, warnings);
    }

    /// <summary>Прежняя подпись: «true — файл действительно создан». Оставлена ради вызывающих,
    /// которые считают созданные заглушки в отчёте о перестройке диска: перерисовка существующего
    /// файла созданием не является и в их счётчик попадать не должна, а происходить — обязана.</summary>
    public static bool EnsureIn(string? folder, string? versionRaw, IInstructionStubWriter? writer,
        List<string>? warnings = null) =>
        Ensure(folder, versionRaw, writer, warnings) == StubAction.Created;

    /// <summary>Страница-дополнение рядом с настоящим документом: «инструкция лежит рядом, но если
    /// что-то непонятно или она устарела — вот телефон». Кладётся ТОЛЬКО когда в этой самой папке
    /// лежит настоящий документ.
    ///
    /// Именно <see cref="HasRealInstruction"/>, а не <see cref="DocumentExists"/>: у папки с одним
    /// лишь ярлыком-пережитком документ живёт в другом месте, и «инструкция лежит рядом» было бы
    /// неправдой — а рядом с ярлыком появился бы файл, которого там сроду не было.</summary>
    public static StubAction EnsureNoteIn(string? folder, string? versionRaw, IInstructionStubWriter? writer,
        List<string>? warnings = null)
    {
        if (string.IsNullOrWhiteSpace(folder) || !HasRealInstruction(folder)) return StubAction.None;
        return WriteNote(folder!, versionRaw, writer, warnings);
    }

    private static StubAction WriteNote(string folder, string? versionRaw, IInstructionStubWriter? writer,
        List<string>? warnings) =>
        WriteStub(Path.Combine(folder, NoteFileName), StubKind.ServiceNote, versionRaw, writer, warnings);

    /// <summary>Одна-на-всех страница «инструкции не будет» в корне диска. Возвращает, что с ней
    /// сделали; выкладывает на хостинг, если выкладчик задан (адрес постоянный, наклейки на
    /// рациональные шкафы ведут именно на него).</summary>
    public static StubAction EnsureShared(string? root, IInstructionStubWriter? writer,
        List<string>? warnings = null, IInstructionPublisher? publisher = null)
    {
        if (string.IsNullOrWhiteSpace(root)) return StubAction.None;

        var path = SharedNotPlannedPath(root!);
        var action = WriteStub(path, StubKind.NotPlanned, versionRaw: null, writer, warnings);

        // Выкладываем и только что созданную, и уже лежавшую: постоянство ссылки важно и тогда, когда
        // файл был на месте, а вот в бакет он не доехал.
        if (publisher is not null && File.Exists(path))
            publisher.Publish(path, path, root!, warnings ?? new List<string>());

        return action;
    }

    /// <summary>Перерисовать конкретный файл-заглушку, если он нарисован по прошлому макету. Нужна
    /// перезаливке на хостинг: она гоняет наверх байты С ДИСКА, и без этой проверки «перезалить всё»
    /// после правки макета отправляло бы в бакет ровно тот же устаревший файл — что и наблюдалось.
    /// Возвращает, что сделали.</summary>
    public static StubAction Refresh(string? path, IInstructionStubWriter? writer, List<string>? warnings = null)
    {
        if (string.IsNullOrWhiteSpace(path) || Describe(path) is not { } info) return StubAction.None;
        return WriteStub(path!, info.Kind, InstructionNaming.VersionFromFileName(path), writer, warnings);
    }

    /// <summary>Написать (или перерисовать) файл по указанному пути. Единственное место, где заглушка
    /// вообще попадает на диск: и решение «а надо ли», и метка ставятся здесь.</summary>
    private static StubAction WriteStub(string path, StubKind kind, string? versionRaw,
        IInstructionStubWriter? writer, List<string>? warnings)
    {
        if (writer is null) return StubAction.None;

        var wanted = writer.Layouts.Sane().Stamp(kind);
        // «Создали или перерисовали» решается по НАЛИЧИЮ ФАЙЛА, а не по ответу Describe: у страницы со
        // своим постоянным именем он утвердительный и для ещё не существующего пути (см. Describe), и
        // первое создание считалось бы перерисовкой.
        var onDisk = Exists(path);
        var existing = onDisk ? Describe(path) : null;
        // Тот же вид, тот же отпечаток — картинка получится байт в байт прежней, трогать сетевой диск
        // незачем.
        if (existing is not null && existing.Kind == kind && existing.Stamp == wanted) return StubAction.None;

        var refreshing = onDisk;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            writer.Write(path, kind, versionRaw);
            MarkAsStub(path, kind, wanted);
            return refreshing ? StubAction.Refreshed : StubAction.Created;
        }
        catch (Exception ex)
        {
            warnings?.Add($"Заглушка инструкции не создана ({path}): {ex.Message}");
            return StubAction.None;
        }
    }

    /// <summary>Дописать метку в конец готового файла. Именно здесь, а не в рисовальщике: реализации
    /// <see cref="IInstructionStubWriter"/> живут в приложении и в тестах, и правило «заглушка
    /// помечена» не должно зависеть от того, какая из них сработала.</summary>
    private static void MarkAsStub(string path, StubKind kind, string stamp)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        var line = $"{Marker} kind={kind.Tag()} stamp={stamp}";
        var bytes = Encoding.ASCII.GetBytes("\n" + line + "\n");
        fs.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Заглушка в папку «Инструкция» версии на первом диске. Возвращает число СОЗДАННЫХ
    /// файлов (0 или 1); перерисовка устаревших происходит здесь же, но созданием не считается.
    ///
    /// <paramref name="publisher"/> задан — заглушка ТАКЖЕ уходит на хостинг (см.
    /// <see cref="IInstructionPublisher"/>): наклейку с QR печатают и клеят на шкаф ДО того, как
    /// инструкцию дописали, и по постоянной ссылке должно открываться хотя бы «в разработке», иначе
    /// постоянство ссылки не работает. Ключ объекта считается от пути на первом диске. null (по
    /// умолчанию) — хостинг не настроен либо вызывающему не нужен, всё работает как раньше.</summary>
    public static int EnsureForVersion(string? folderOnFirstDisk, string? firstRoot,
        string? versionRaw, IInstructionStubWriter? writer, List<string>? warnings = null,
        IInstructionPublisher? publisher = null)
    {
        if (string.IsNullOrWhiteSpace(folderOnFirstDisk)) return 0;

        var action = Ensure(folderOnFirstDisk, versionRaw, writer, warnings);

        // Выкладываем заглушку — и только что созданную, и уже лежавшую (постоянство ссылки важно и
        // тогда, когда заглушка была там и до этого вызова).
        if (publisher is not null)
        {
            var stub = ExistingIn(folderOnFirstDisk);
            if (stub is not null)
                publisher.Publish(stub, stub, firstRoot ?? "", warnings ?? new List<string>());
        }

        return action == StubAction.Created ? 1 : 0;
    }

    /// <summary>Убрать заглушки из папки — зовётся, как только туда легла настоящая инструкция.
    /// Ищет по признаку, а не по одному имени: заглушка могла быть положена и под общим именем (когда
    /// версия была неизвестна), и под каноническим. Возвращает число удалённых файлов; недоступная
    /// или несуществующая папка — ноль без ошибки.
    ///
    /// <paramref name="kinds"/> пуст — убираются ВСЕ виды (так зовут те, кто расчищает папку целиком).</summary>
    public static int RemoveFrom(string? folder, params StubKind[] kinds)
    {
        if (string.IsNullOrWhiteSpace(folder)) return 0;
        var removed = 0;
        foreach (var file in EnumerateStubs(folder))
        {
            if (kinds.Length > 0 && KindOf(file) is { } kind && !kinds.Contains(kind)) continue;
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
        return removed;
    }
}
