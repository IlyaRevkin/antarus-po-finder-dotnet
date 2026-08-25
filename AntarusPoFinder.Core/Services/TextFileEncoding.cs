using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>В какой кодировке лежит текстовый файл параметров.
///
/// Отдельным классом, потому что на одном живом примере с диска Ильи, В ОДНОЙ ПАПКЕ, лежат файлы в
/// РАЗНЫХ кодировках: сами параметры — cp1251, соседний Readme — UTF-8, а от прежних лет остаются
/// файлы в cp866 (сохранённые из DOS-редакторов и из far). Зашить одну кодировку — значит на каждом
/// втором файле получить «╨б ╨┤╤А╤Г...» вместо текста и разбор, который не нашёл ни одного параметра.
///
/// Читать через <see cref="Encoding.Default"/> нельзя по той же причине, что и через жёсткий 1251:
/// в .NET это UTF-8, и файл в 1251 читается с заменой на «��».</summary>
public static class TextFileEncoding
{
    /// <summary>Кодовые страницы 1251/866 в .NET Core нужно ЗАРЕГИСТРИРОВАТЬ — без этого
    /// Encoding.GetEncoding(1251) кидает ArgumentException прямо на первом же файле. Провайдер
    /// ставится один раз на процесс; повторный вызов Register безвреден, но флагом дешевле.</summary>
    private static bool _providerRegistered;

    private static void EnsureProvider()
    {
        if (_providerRegistered) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _providerRegistered = true;
    }

    public static Encoding Cp1251
    {
        get { EnsureProvider(); return Encoding.GetEncoding(1251); }
    }

    public static Encoding Cp866
    {
        get { EnsureProvider(); return Encoding.GetEncoding(866); }
    }

    /// <summary>Читает файл, определив кодировку. Возвращает и текст, и то, чем его прочли —
    /// предпросмотр импорта показывает это человеку: если разбор дал ерунду, первым делом смотрят
    /// именно сюда, и переключить кодировку руками должно быть можно.</summary>
    public static (string Text, string EncodingName) ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    /// <summary>То же по уже прочитанным байтам — так это проверяется тестами, не заводя файлов.</summary>
    public static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        EnsureProvider();

        // 1. BOM — единственный однозначный признак, спорить с ним нечего.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), "UTF-8");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");

        // 2. Валидный UTF-8 без BOM. Проверка строгая (throwOnInvalidBytes), потому что смысл её
        //    ровно в том, чтобы ОТКАЗАТЬСЯ: почти любая кириллическая строка в 1251 как UTF-8 не
        //    раскладывается, и исключение здесь — это ответ «нет», а не сбой.
        //    Чистая латиница проходит эту проверку тоже — и правильно: в 1251 и 866 она читается
        //    так же, спорить не о чем.
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // не UTF-8 — идём дальше
        }

        // 3. Осталось выбрать между двумя однобайтовыми кириллическими. Различаются они только
        //    раскладкой байтов, и отличить их можно лишь по тому, какой текст получается осмысленнее.
        return ScoreCp1251(bytes) >= ScoreCp866(bytes)
            ? (Cp1251.GetString(bytes), "Windows-1251")
            : (Cp866.GetString(bytes), "CP866 (DOS)");
    }

    /// <summary>Насколько текст похож на осмысленную кириллицу в cp1251. Считаются байты в диапазоне
    /// русских букв (0xC0..0xFF плюс Ё/ё) — в cp866 те же байты это псевдографика рамок, которой в
    /// осмысленном тексте не бывает столько.</summary>
    private static int ScoreCp1251(byte[] bytes)
    {
        var score = 0;
        foreach (var b in bytes)
        {
            if (b >= 0xC0) score++;                 // А-я
            else if (b == 0xA8 || b == 0xB8) score++; // Ё, ё
            // 0xB0..0xBF в cp1251 — «°±µ¶·№», в cp866 — рамки. Ни то ни другое не признак.
        }
        return score;
    }

    /// <summary>То же для cp866. Русские буквы там лежат в 0x80..0xAF и 0xE0..0xEF; диапазон
    /// 0xB0..0xDF — псевдографика, и её обилие ЗАСЧИТЫВАЕТСЯ В МИНУС: файл, где половина байтов
    /// рисует рамки, почти наверняка не cp866-текст, а неверно прочитанный 1251.</summary>
    private static int ScoreCp866(byte[] bytes)
    {
        var score = 0;
        foreach (var b in bytes)
        {
            if (b >= 0x80 && b <= 0xAF) score++;
            else if (b >= 0xE0 && b <= 0xEF) score++;
            else if (b >= 0xB0 && b <= 0xDF) score--;
        }
        return score;
    }

    /// <summary>Имена кодировок для выпадающего списка в предпросмотре импорта — на случай, когда
    /// определение всё-таки промахнулось (короткий файл, латиница вперемешку).</summary>
    public static readonly string[] Choices = { "Определить сама", "UTF-8", "Windows-1251", "CP866 (DOS)" };

    /// <summary>Прочитать байты ЗАДАННОЙ кодировкой — то, что делает переключатель в предпросмотре.
    /// «Определить сама» и любое незнакомое имя возвращают к <see cref="Decode"/>.</summary>
    public static (string Text, string EncodingName) DecodeAs(byte[] bytes, string? choice)
    {
        EnsureProvider();
        return choice switch
        {
            "UTF-8" => (new UTF8Encoding(false).GetString(StripBom(bytes)), "UTF-8"),
            "Windows-1251" => (Cp1251.GetString(bytes), "Windows-1251"),
            "CP866 (DOS)" => (Cp866.GetString(bytes), "CP866 (DOS)"),
            _ => Decode(bytes),
        };
    }

    private static byte[] StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
}
