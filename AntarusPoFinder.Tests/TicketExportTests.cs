using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Выгрузка тикетов в архив — единственный способ показать тикет тому, кто его будет чинить:
/// сетевого диска конторы у него нет. Поэтому проверяется не «файл создался», а что в архиве лежит
/// всё, ради чего его собирали: полный текст (не обрезанный, как в таблице на странице), вложения и
/// обстоятельства выгрузки. Отдельно — что недоступное вложение не превращает выгрузку в тишину.</summary>
public class TicketExportTests
{
    private static readonly DateTime At = new(2026, 8, 12, 18, 30, 0);

    private static TicketExportService.Meta Meta(string scope = "открытые и в работе") =>
        new("1.68.2", "ANTARUS-PC", "ilia", "Администратор", scope, At);

    private static Ticket Ticket(string id, string text, string status = TicketStatus.Open, string type = TicketType.Bug) =>
        new()
        {
            Id = id,
            Type = type,
            Text = text,
            Status = status,
            CreatedBy = "naladchik1",
            CreatedByRole = "naladchik",
            CreatedAt = "2026-08-10T09:12:00.000",
            UpdatedAt = "2026-08-10T09:12:00.000",
        };

    private static Dictionary<string, string> ReadTextEntries(string zipPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            result[entry.FullName] = reader.ReadToEnd();
        }
        return result;
    }

    [Fact]
    public void Write_PutsBothViewsAndMetaIntoArchive()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "tickets.zip");

        TicketExportService.Write(zipPath, Meta(), [Ticket("id-1", "Не печатается этикетка")], null);

        var entries = ReadTextEntries(zipPath);
        Assert.Contains("tickets.md", entries.Keys);
        Assert.Contains("tickets.json", entries.Keys);

        var md = entries["tickets.md"];
        Assert.Contains("1.68.2", md);
        Assert.Contains("ANTARUS-PC", md);
        Assert.Contains("открытые и в работе", md);
        Assert.Contains("Не печатается этикетка", md);
        Assert.Contains("Баг", md);
    }

    /// <summary>В таблице на странице текст обрезан одной строкой — именно за полным текстом сюда и
    /// приходят. Многострочный тикет обязан доехать целиком.</summary>
    [Fact]
    public void Write_KeepsFullMultilineText()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "tickets.zip");
        var text = "Первая строка\nВторая строка\nШаги: 1) открыть 2) нажать 3) упало";

        TicketExportService.Write(zipPath, Meta(), [Ticket("id-1", text)], null);

        var entries = ReadTextEntries(zipPath);
        Assert.Contains("Шаги: 1) открыть 2) нажать 3) упало", entries["tickets.md"]);

        using var json = JsonDocument.Parse(entries["tickets.json"]);
        var jsonText = json.RootElement.GetProperty("tickets")[0].GetProperty("text").GetString();
        Assert.Equal(text, jsonText);
    }

    [Fact]
    public void Write_JsonCarriesEveryTicketField()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "tickets.zip");

        TicketExportService.Write(zipPath, Meta(),
            [Ticket("id-1", "раз", TicketStatus.InProgress), Ticket("id-2", "два", TicketStatus.Closed, TicketType.Suggestion)], null);

        using var json = JsonDocument.Parse(ReadTextEntries(zipPath)["tickets.json"]);
        Assert.Equal("1.68.2", json.RootElement.GetProperty("appVersion").GetString());

        var tickets = json.RootElement.GetProperty("tickets");
        Assert.Equal(2, tickets.GetArrayLength());
        Assert.Equal("id-1", tickets[0].GetProperty("id").GetString());
        Assert.Equal("in_progress", tickets[0].GetProperty("status").GetString());
        Assert.Equal("Предложение", tickets[1].GetProperty("typeLabel").GetString());
        Assert.Equal("naladchik", tickets[1].GetProperty("createdByRole").GetString());
    }

    [Fact]
    public void Write_PacksAttachmentsUnderTheirTicket()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "tickets.zip");
        var attachDir = Path.Combine(root.Path, "attach", "id-1");
        Directory.CreateDirectory(attachDir);
        File.WriteAllText(Path.Combine(attachDir, "снимок.png"), "picture bytes");
        File.WriteAllText(Path.Combine(attachDir, "log.txt"), "stack trace");

        var result = TicketExportService.Write(zipPath, Meta(), [Ticket("id-1", "упало")],
            id => Path.Combine(root.Path, "attach", id));

        Assert.Equal(2, result.Attachments);
        Assert.Empty(result.Warnings);

        var entries = ReadTextEntries(zipPath);
        Assert.Equal("picture bytes", entries["attachments/id-1/снимок.png"]);
        Assert.Equal("stack trace", entries["attachments/id-1/log.txt"]);
        // Путь в обоих описаниях указывает ровно туда, где файл лежит в архиве.
        Assert.Contains("attachments/id-1/", entries["tickets.md"]);
        Assert.Contains("attachments/id-1/log.txt", entries["tickets.json"]);
    }

    /// <summary>Сетевого диска нет — тексты тикетов лежат в локальной базе и должны выгрузиться всё
    /// равно: без диска человек как раз и остаётся один на один с проблемой.</summary>
    [Fact]
    public void Write_WithoutShare_StillExportsTexts()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "tickets.zip");

        var result = TicketExportService.Write(zipPath, Meta(), [Ticket("id-1", "нет диска")], _ => null);

        Assert.Equal(1, result.Tickets);
        Assert.Equal(0, result.Attachments);
        Assert.Empty(result.Warnings);
        Assert.Contains("нет диска", ReadTextEntries(zipPath)["tickets.md"]);
    }

    /// <summary>Занятый другим процессом файл (открытый скриншот, лог, в который пишут) не должен ни
    /// ронять выгрузку, ни уезжать молча: тикеты собираются, а про пропажу написано.</summary>
    [Fact]
    public void Write_UnreadableAttachment_WarnsButKeepsGoing()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "tickets.zip");
        var attachDir = Path.Combine(root.Path, "attach", "id-1");
        Directory.CreateDirectory(attachDir);
        var locked = Path.Combine(attachDir, "locked.log");
        File.WriteAllText(locked, "занят");
        File.WriteAllText(Path.Combine(attachDir, "ok.txt"), "цел");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = TicketExportService.Write(zipPath, Meta(), [Ticket("id-1", "упало")],
                id => Path.Combine(root.Path, "attach", id));

            Assert.Single(result.Warnings);
            Assert.Contains("locked.log", result.Warnings[0]);
        }

        var entries = ReadTextEntries(zipPath);
        Assert.Equal("цел", entries["attachments/id-1/ok.txt"]);
        Assert.Contains("упало", entries["tickets.md"]);
    }

    [Fact]
    public void Write_NoTickets_ProducesReadableArchiveAnyway()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "tickets.zip");

        var result = TicketExportService.Write(zipPath, Meta(), [], null);

        Assert.Equal(0, result.Tickets);
        Assert.Contains("Тикетов по этому отбору нет", ReadTextEntries(zipPath)["tickets.md"]);
    }

    /// <summary>Битый архив под правильным именем — худший исход: его отправят почтой как настоящий.
    /// Сорвавшаяся выгрузка не должна оставить после себя ни его, ни временного файла.</summary>
    [Fact]
    public void Write_OverExistingArchive_ReplacesItWholly()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "tickets.zip");
        File.WriteAllText(zipPath, "старый мусор на месте архива");

        TicketExportService.Write(zipPath, Meta(), [Ticket("id-1", "новый тикет")], null);

        Assert.False(File.Exists(zipPath + ".tmp"));
        Assert.Contains("новый тикет", ReadTextEntries(zipPath)["tickets.md"]);
    }

    [Theory]
    [InlineData("Короткая строка", "Короткая строка")]
    [InlineData("\n\n  Первая непустая  \nвторая", "Первая непустая")]
    [InlineData("", "(без текста)")]
    public void Headline_TakesFirstMeaningfulLine(string text, string expected) =>
        Assert.Equal(expected, TicketExportService.Headline(text));

    [Fact]
    public void Headline_LongLine_IsCutByWord()
    {
        var line = string.Join(" ", Enumerable.Repeat("слово", 40));

        var headline = TicketExportService.Headline(line);

        Assert.True(headline.Length <= 81, $"заголовок длиной {headline.Length}");
        Assert.EndsWith("…", headline);
        Assert.DoesNotContain("слов…", headline); // режем по пробелу, а не посреди слова
    }

    [Fact]
    public void SuggestedFileName_IsSortableAndAscii()
    {
        var name = TicketExportService.SuggestedFileName(At);

        Assert.Equal("tickets_20260812_1830.zip", name);
    }
}
