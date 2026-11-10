using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>КУДА УЕЗЖАЕТ СЛЕПОК ДИСКА — тихий канал диагностики, а не бакет на хостинге.
///
/// <b>Почему не S3, хотя выкладка слепка туда уже написана (<see cref="DiskSnapshotService"/>).</b>
/// Две причины, и обе решающие:
///
/// 1. <b>Бакет публичен на чтение.</b> Адрес <c>fs.elitacompany.ru</c> заведён ровно для того,
///    чтобы QR на наклейке открывался с телефона без всяких ключей. Слепок — это ОПИСЬ внутренней
///    сетевой папки конторы: имена шкафов, заявок, серийники, заказчики в названиях папок. Такому
///    в открытом доступе места нет, и «мы же не публикуем ссылку» защитой не является.
/// 2. <b>Ключей от бакета на машине может не быть.</b> Это не домысел: 21.10.2026 выяснилось, что
///    на машине Ильи их в профиле нет, и ровно из-за этого журналы заливок и тикеты ездят не в
///    бакет, а на страницу приёма. Слепок, который молчит на той самой машине, ради которой он
///    заводится, бесполезен.
///
/// Страница приёма от обеих бед свободна: она частная, скачивания через веб нет вовсе (забирают по
/// SSH), код доступа лежит в настройках, а <c>silent=1</c> кладёт файл в <c>diag/</c> и не будит
/// телеграм. Выкладка в бакет при этом остаётся как была — у неё своя задача (история слепков под
/// постоянным адресом) и свои условия (ключи есть, их владелец так решил).
///
/// <b>Почему zip, а не gzip.</b> Сжать надо обязательно — опись это десятки тысяч строк, в zip она
/// ужимается раз в десять. Из двух способов выбран тот, которым через эту страницу уже ездят
/// (разовая выгрузка тикетов, <c>tickets_*.zip</c>): расширение заведомо проходит её белый список,
/// и проверять догадку «а .gz она тоже примет» на рабочей машине, где слепок снимается раз в
/// сутки, — не та цена за десяток лишних килобайт.</summary>
public static class DiskSnapshotDrop
{
    /// <summary>Имя файла внутри архива. Одно и то же всегда: разбор не должен угадывать.</summary>
    public const string EntryName = "snapshot.json";

    /// <summary>Приставка имени архива. По ней слепки отделяются от журналов заливок и тикетов,
    /// лежащих в той же папке <c>diag/</c>.</summary>
    public const string FilePrefix = "disk";

    /// <summary>Имя архива: время, приставка, машина. Время ПЕРВЫМ — файлы в <c>diag/</c> лежат
    /// вперемешку от всех машин, и сортировка по имени должна совпадать с сортировкой по времени.
    /// Имя машины приводится к латинице тем же правилом, что и в бакете
    /// (<see cref="DiskSnapshotStorage.SanitizeMachine"/>): кириллица в именах файлов, которые
    /// потом забирают по SSH и разбирают скриптом, — лишний источник приключений.</summary>
    public static string FileName(string machine, DateTime takenAt) =>
        $"{takenAt:yyyyMMdd_HHmmss}_{FilePrefix}_{DiskSnapshotStorage.SanitizeMachine(machine)}.zip";

    /// <summary>Упаковать слепок. Без BOM: читает это разбор, а не Блокнот.</summary>
    public static byte[] Pack(string json)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(EntryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            var bytes = new UTF8Encoding(false).GetBytes(json ?? "");
            stream.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    /// <summary>Достать слепок обратно из архива — тем же кодом, которым его клали. Нужно тестам и
    /// разбору: «мы это отправили» и «это можно прочитать» — разные утверждения, и проверять надо
    /// второе.</summary>
    public static string Unpack(byte[] archive)
    {
        using var input = new MemoryStream(archive);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var entry = zip.GetEntry(EntryName) ?? throw new InvalidDataException($"в архиве нет {EntryName}");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Сколько ждать ответа. Дольше, чем у журналов заливок: там уезжают килобайты, здесь
    /// — мегабайты по конторскому каналу.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Shared = new() { Timeout = Timeout };

    public sealed record Result(bool Ok, string? Error, string FileName, int Bytes);

    /// <summary>Отправить слепок. Проверка на секреты — ЗДЕСЬ, у самой отправки, а не у сборки:
    /// между сборкой и отправкой слепок может пройти через что угодно, а отказаться надо до того,
    /// как байты ушли наружу (см. <see cref="DiskSnapshotSafety"/>).</summary>
    public static async Task<Result> SendAsync(
        string json, string machine, DateTime takenAt, string dropUrl, string dropCode,
        HttpClient? http = null, CancellationToken ct = default)
    {
        var name = FileName(machine, takenAt);

        if (DiskSnapshotSafety.Refusal(json) is { } refusal)
            return new Result(false, refusal, name, 0);

        var body = Pack(json);
        var outcome = await DropChannel.PostAsync(dropUrl, dropCode,
            $"слепок диска, {machine} (автоматически)", name, body, "application/zip",
            http ?? Shared, ct).ConfigureAwait(false);

        return new Result(outcome.Ok, outcome.Error, name, body.Length);
    }
}
