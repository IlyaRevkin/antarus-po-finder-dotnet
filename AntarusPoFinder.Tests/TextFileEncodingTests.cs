using System.IO;
using System.Text;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Определение кодировки текстовых файлов параметров (TextFileEncoding).
///
/// Заведено оно не от любви к кодировкам: в ОДНОЙ папке на диске лежат файл параметров в cp1251,
/// Readme в UTF-8 и остатки DOS-времён в cp866. Промах здесь виден не как «кракозябры» — виден он
/// как разбор, не нашедший ни одного параметра, потому что все русские буквы превратились в «?».</summary>
public class TextFileEncodingTests
{
    private const string Russian = "Максимальная частота, указать в соответствии с ПЛК";

    [Fact]
    public void Utf8WithBom_IsReadWithoutTheBom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(Russian));

        var (text, name) = TextFileEncoding.Decode(bytes);

        Assert.Equal(Russian, text);
        Assert.Equal("UTF-8", name);
        // Метка порядка байтов, оставленная в тексте, — это невидимый первый символ, из-за которого
        // первая строка файла перестаёт совпадать с чем бы то ни было.
        Assert.False(text.StartsWith('\uFEFF'));
    }

    [Fact]
    public void Utf16_IsRecognizedByItsBom()
    {
        var le = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(Russian));
        var be = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(Russian));

        Assert.Equal((Russian, "UTF-16 LE"), TextFileEncoding.Decode(le));
        Assert.Equal((Russian, "UTF-16 BE"), TextFileEncoding.Decode(be));
    }

    [Fact]
    public void Utf8WithoutBom_IsRecognized()
    {
        var (text, name) = TextFileEncoding.Decode(new UTF8Encoding(false).GetBytes(Russian));

        Assert.Equal(Russian, text);
        Assert.Equal("UTF-8", name);
    }

    [Fact]
    public void Cp1251_IsRecognized()
    {
        var (text, name) = TextFileEncoding.Decode(TextFileEncoding.Cp1251.GetBytes(Russian));

        Assert.Equal(Russian, text);
        Assert.Equal("Windows-1251", name);
    }

    [Fact]
    public void Cp866_IsRecognized()
    {
        var (text, name) = TextFileEncoding.Decode(TextFileEncoding.Cp866.GetBytes(Russian));

        Assert.Equal(Russian, text);
        Assert.Equal("CP866 (DOS)", name);
    }

    [Fact]
    public void PlainLatin_IsNotAGuessingGame()
    {
        // Латиница в UTF-8, 1251 и 866 записывается одинаково — спорить не о чем, и «UTF-8» здесь
        // правильный ответ, а не удачное совпадение.
        var (text, name) = TextFileEncoding.Decode(Encoding.ASCII.GetBytes("P0-02(2) - Modbus"));

        Assert.Equal("P0-02(2) - Modbus", text);
        Assert.Equal("UTF-8", name);
    }

    [Fact]
    public void EmptyFile_DoesNotThrow()
    {
        var (text, _) = TextFileEncoding.Decode(System.Array.Empty<byte>());

        Assert.Equal("", text);
    }

    [Fact]
    public void ManualChoice_OverridesTheGuess()
    {
        var bytes = TextFileEncoding.Cp1251.GetBytes(Russian);

        // Ради этого переключатель в предпросмотре и заведён: определение может промахнуться на
        // коротком файле, и последнее слово остаётся за человеком.
        Assert.Equal(Russian, TextFileEncoding.DecodeAs(bytes, "Windows-1251").Text);
        Assert.NotEqual(Russian, TextFileEncoding.DecodeAs(bytes, "CP866 (DOS)").Text);
        Assert.Equal(Russian, TextFileEncoding.DecodeAs(bytes, "Определить сама").Text);
        Assert.Equal(Russian, TextFileEncoding.DecodeAs(bytes, null).Text);
    }

    [Fact]
    public void ManualUtf8Choice_StillStripsTheBom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(Russian));

        Assert.Equal(Russian, TextFileEncoding.DecodeAs(bytes, "UTF-8").Text);
    }

    [Fact]
    public void EveryChoiceInTheDropdown_IsUnderstoodByDecodeAs()
    {
        // Список для выпадающего меню и разбор выбранного — в одном классе, и разъехаться они не
        // должны: лишний пункт молча означал бы «определить сама».
        var bytes = TextFileEncoding.Cp1251.GetBytes(Russian);

        foreach (var choice in TextFileEncoding.Choices)
            Assert.NotEqual("", TextFileEncoding.DecodeAs(bytes, choice).EncodingName);
    }

    [Fact]
    public void ReadFile_ReadsWhatIsOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"antarus_enc_{System.Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllBytes(path, TextFileEncoding.Cp866.GetBytes(Russian));

            var (text, name) = TextFileEncoding.ReadFile(path);

            Assert.Equal(Russian, text);
            Assert.Equal("CP866 (DOS)", name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Windows-1251")]
    [InlineData("CP866 (DOS)")]
    public void FileInAnyCyrillicEncoding_ParsesIntoTheSameTable(string encodingName)
    {
        // Итог, ради которого всё это и заведено: разобранная таблица не зависит от того, чем файл
        // был сохранён. Промах в кодировке даёт не «странные буквы», а ноль найденных параметров.
        const string source = """
            ==[Настройка ШУ]
            P0-02(2) - Выбор канала команды запуска - Протокол связи
            P0-10(?) - Максимальная частота
            """;
        var bytes = encodingName == "Windows-1251"
            ? TextFileEncoding.Cp1251.GetBytes(source)
            : TextFileEncoding.Cp866.GetBytes(source);

        var (text, detected) = TextFileEncoding.Decode(bytes);
        var parsed = ParamTextParser.Parse(text);

        Assert.Equal(encodingName, detected);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal("Выбор канала команды запуска", parsed.Rows[0].Title);
    }
}

internal static class ByteArrayConcatExtensions
{
    /// <summary>Склейка байтов для наглядности теста: «метка порядка байтов, а следом текст».</summary>
    public static byte[] Concat(this byte[] head, byte[] tail)
    {
        var result = new byte[head.Length + tail.Length];
        head.CopyTo(result, 0);
        tail.CopyTo(result, head.Length);
        return result;
    }
}
