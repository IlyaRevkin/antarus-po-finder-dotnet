using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Паспорт теперь эфемерный: «Сформировать паспорт» подставляет название шкафа в КОПИЮ
/// шаблона-файла и печатает — ничего не сохраняя ни в общую папку шаблонов (каталог), ни в базу.
/// Хранимых записей паспортов больше нет вовсе, и синхронизация их больше не переносит.
///
/// Здесь проверяется именно это обещание: «сформировал из шаблона → предпросмотр → печать →
/// всё, ничего не сохраняется; нужен снова — формируется заново».</summary>
public class PassportGenerationTests : IDisposable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string MakeDocx(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = zip.CreateEntry("word/document.xml").Open();
        new XDocument(new XElement(W + "document",
            new XElement(W + "body",
                new XElement(W + "p",
                    new XElement(W + "r", new XElement(W + "t", text)))))).Save(stream);
        return path;
    }

    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    /// <summary>Формирование паспорта из шаблона НЕ трогает общую папку шаблонов: файл-шаблон и всё
    /// рядом с ним остаются байт в байт, а заполненная копия уходит В ДРУГОЕ место (временную папку).</summary>
    [Fact]
    public void GeneratingAPassport_WritesNothingIntoTheTemplateFolder()
    {
        var templates = Path.Combine(_root.Path, "Конфиг", "Паспорта");
        var template = MakeDocx(Path.Combine(templates, "НКУ.docx"), "Паспорт шкафа {{Название}}");

        var before = Snapshot(templates);

        // Ровно то, что делает окно «Сформировать паспорт»: подставляет название в КОПИЮ во временной
        // папке, сам шаблон не трогает.
        var dst = Path.Combine(_root.Path, "temp", "НКУ — ЩУН-3.docx");
        var replacements = DocxTemplateFiller.Fill(template, dst, "{{Название}}", "ЩУН-3");

        Assert.Equal(1, replacements);
        Assert.True(File.Exists(dst));
        // Копия легла ВНЕ папки шаблонов.
        Assert.False(dst.StartsWith(templates, StringComparison.OrdinalIgnoreCase));
        // Папка шаблонов — тот же набор файлов с теми же хэшами: ни одного нового файла, ни одной правки.
        Assert.Equal(before, Snapshot(templates));
    }

    /// <summary>Паспорта больше не часть синхронизируемого каталога: свежий снимок этой версии секции
    /// «passports» не содержит вовсе (Passports == null) — приёмник читает это как «отправитель о
    /// паспортах не знает», а не «удалить все».</summary>
    [Fact]
    public void PassportsAreNoLongerPartOfTheExportedCatalog()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        Assert.Null(db.ExportHierarchyData().Passports);
    }

    /// <summary>Совместимость: снимок СТАРОГО клиента ещё несёт секцию «passports». Новый клиент её
    /// просто игнорирует — импорт не падает и ничего в паспортах не считает (их у него больше нет).</summary>
    [Fact]
    public void AnOldClientsPassportSection_IsSilentlyIgnored_WithoutBreaking()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var oldSnapshot = db.ExportHierarchyData();
        oldSnapshot.Passports = new List<ExportedPassport>
        {
            new() { Name = "НКУ", General = 1, SyncId = Guid.NewGuid().ToString() },
        };

        var counts = db.ImportHierarchyData(oldSnapshot);

        Assert.Equal(0, counts.Passports);
        Assert.Equal(0, counts.PassportsRemoved);
        Assert.Equal(0, counts.PassportsUpdated);
    }
}
