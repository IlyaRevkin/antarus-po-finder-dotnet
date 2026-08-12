using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Жалоба Ильи: «у меня есть прошивка, а вот инструкции нет, и он почему-то сделал ссылку на
/// Thumbs.db вместо заглушки».
///
/// В папке «Инструкция» лежал ровно один файл — служебный `Thumbs.db`, который проводник создаёт сам
/// при просмотре эскизов. Он проходил как «первый файл, который не ярлык и не заглушка», и дальше
/// врало всё сразу: карточка загоралась «инструкция ✓», заглушка не клалась (папка ведь не пустая),
/// а под QR уходила ссылка на служебный файл Windows — и наклейку с ней клеили на шкаф.</summary>
public class JunkNotADocumentTests
{
    [Theory]
    [InlineData("Thumbs.db")]
    [InlineData("thumbs.DB")]
    [InlineData("desktop.ini")]
    [InlineData("~$инструкция.docx")]
    [InlineData("инструкция.pdf.part")]
    [InlineData("инструкция.tmp")]
    public void Junk_IsNeverADocument(string name) =>
        Assert.True(DocFileResolver.IsNotADocument(Path.Combine(@"C:\ПО", name)), name);

    [Theory]
    [InlineData("инструкция_1.0.0005.0001.pdf")]
    [InlineData("руководство.docx")]
    [InlineData("мнемосхема.svg")]
    [InlineData("старая версия.old")]
    public void RealDocuments_StayDocuments(string name) =>
        Assert.False(DocFileResolver.IsNotADocument(Path.Combine(@"C:\ПО", name)), name);

    [Fact]
    public void FolderWithOnlyJunk_CountsAsHavingNoDocument()
    {
        // Ровно тот случай из жалобы: заглушку сюда положить НАДО, а ссылку в QR строить не от чего.
        using var root = new TempRoot();
        var folder = Path.Combine(root.Path, "Инструкция");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Thumbs.db"), "junk");

        Assert.False(InstructionStub.HasRealInstruction(folder));
        Assert.False(InstructionStub.DocumentExists(folder));
    }

    [Fact]
    public void FolderWithJunkNextToTheDocument_StillFindsTheDocument()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(root.Path, "Инструкция");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Thumbs.db"), "junk");
        var real = Path.Combine(folder, "инструкция_1.0.0005.0001.pdf");
        File.WriteAllText(real, "%PDF-1.4\n%%EOF\n");

        Assert.True(InstructionStub.HasRealInstruction(folder));
        Assert.Equal(real, DocFileResolver.LatestFileIn(folder));
    }

    /// <summary>Список мусора один на всех: то, что чистильщик предлагает удалить, программа не имеет
    /// права считать документом — иначе она удаляла бы то, на что сама же ставит ссылку.</summary>
    [Fact]
    public void TheCleanerAndTheResolver_AgreeOnWhatIsJunk()
    {
        foreach (var name in JunkFiles.Names)
        {
            var path = Path.Combine(@"C:\ПО", name);
            Assert.NotNull(JunkFiles.Reason(path));
            Assert.True(DocFileResolver.IsNotADocument(path), name);
        }
    }
}
