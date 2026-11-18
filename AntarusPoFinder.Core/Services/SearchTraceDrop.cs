using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>РАЗБОР ПОИСКА УЕЗЖАЕТ ТЕМ ЖЕ ТИХИМ КАНАЛОМ, что слепок диска и журнал приложения
/// (<see cref="DropChannel"/>): <c>silent=1</c>, файл ложится в <c>diag/</c>, телеграм молчит.
/// Нового канала здесь нет и заводить его незачем — смена правила приёма должна чиниться в одном
/// месте, а не в четырёх.
///
/// <b>ПОИСК ДЕЛАЮТ СОТНИ РАЗ В ДЕНЬ, и это главное ограничение.</b> Отправлять разбор каждого
/// поиска — значит за неделю завалить <c>diag/</c> так, что читать перестанут всё, включая журналы
/// заливок, ради которых папка и заводилась. Поэтому отбор тройной, и каждая ступень отсекает свой
/// вид мусора:
///
/// <list type="number">
/// <item><b>Случай интересен</b> (<see cref="Interesting"/>) — что-то ИСЧЕЗЛО НЕЗАМЕТНО ДЛЯ
///   ЧЕЛОВЕКА. Таких случая два, и оба — прямая форма жалобы «залил прошивку, она не находится»:
///   строку спрятало правило «слова по запросу», либо при схлопывании выбыла версия СВЕЖЕЕ
///   показанной. Обычное схлопывание (свежая вытеснила прежние), пустая выдача по опечатке и
///   отсев фильтрами сюда НЕ попадают: это нормальная работа поиска, она происходит постоянно и
///   объяснять в ней нечего.</item>
/// <item><b>Случай ещё не присылали</b> (<see cref="SearchTraceLedger"/>) — по отпечатку
///   содержимого. Человек, у которого прошивка «пропала», повторит один и тот же запрос раз
///   десять, прежде чем поверит; все десять раз это одна и та же запись.</item>
/// <item><b>Потолок в сутки</b> — страховка от того, чего мы не предусмотрели. Если завтра
///   найдётся способ породить тысячу РАЗНЫХ отпечатков за день, канал всё равно не захлебнётся.</item>
/// </list>
///
/// Выключатель — общий для всей автоматической диагностики (<c>ConfigService.AutoDiagnosticsEnabled</c>):
/// заводить третий тумблер к двум существующим значило бы, что в день разбора выключен окажется
/// ровно тот, про который забыли.</summary>
public static class SearchTraceDrop
{
    /// <summary>Имя файла внутри архива. Одно и то же всегда: разбор не должен угадывать.</summary>
    public const string EntryName = "search-trace.json";

    /// <summary>Приставка имени архива. По ней разборы поиска отделяются от слепков (<c>_disk_</c>),
    /// журналов (<c>_log_</c>) и тикетов в той же папке <c>diag/</c>.</summary>
    public const string FilePrefix = "search";

    /// <summary>Имя архива: время, приставка, машина — тем же правилом, что у слепка и журнала.</summary>
    public static string FileName(string machine, DateTime at) =>
        $"{at:yyyyMMdd_HHmmss}_{FilePrefix}_{DiskSnapshotStorage.SanitizeMachine(machine)}.zip";

    /// <summary>Стоит ли вообще рассказывать об этом поиске — см. ступень 1 в заголовке класса.</summary>
    public static bool Interesting(SearchTrace? trace) => trace is not null && trace.AnythingHidden;

    /// <summary>Упаковать разбор. Без BOM: читает это разбор, а не Блокнот.</summary>
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

    /// <summary>Достать разбор обратно — тем же кодом, которым клали (нужно тестам: «отправили» и
    /// «это можно прочитать» — разные утверждения).</summary>
    public static string Unpack(byte[] archive)
    {
        using var input = new MemoryStream(archive);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var entry = zip.GetEntry(EntryName) ?? throw new InvalidDataException($"в архиве нет {EntryName}");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public sealed record Result(bool Ok, string? Error, string FileName, int Bytes)
    {
        /// <summary>Отправлять было нечего — не ошибка и не удача, а штатная тишина.</summary>
        public static readonly Result Nothing = new(true, null, "", 0);
    }

    /// <summary>Общий HttpClient: килобайты и фоновая мелочь — терпения как у остальных мелких
    /// отправок канала.</summary>
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Отправить разбор. Проверка на секреты — ТА ЖЕ, что у слепка диска
    /// (<see cref="DiskSnapshotSafety"/>), и ЗДЕСЬ, у самой отправки: между сборкой записи и
    /// отправкой она может пройти через что угодно, а отказаться надо до того, как байты ушли
    /// наружу. Нашлось — не отправляем и пишем причину в журнал.</summary>
    public static async Task<Result> SendAsync(
        string json, string machine, DateTime takenAt, string dropUrl, string dropCode,
        HttpClient? http = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dropUrl) || string.IsNullOrWhiteSpace(dropCode)) return Result.Nothing;

        var name = FileName(machine, takenAt);

        if (DiskSnapshotSafety.Refusal(json) is { } refusal)
        {
            AppLog.Error(AppLog.AreaSearch, refusal);
            return new Result(false, refusal, name, 0);
        }

        var body = Pack(json);
        var outcome = await DropChannel.PostAsync(dropUrl, dropCode,
            $"разбор поиска, {machine} (автоматически)", name, body, "application/zip",
            http ?? Shared, ct).ConfigureAwait(false);

        return new Result(outcome.Ok, outcome.Error, name, body.Length);
    }
}

/// <summary>ЧТО УЖЕ ПРИСЫЛАЛИ — ступени 2 и 3 отбора (см. <see cref="SearchTraceDrop"/>).
///
/// Память МЕСТНАЯ и живёт, пока открыта программа. Это осознанно: случай, ради которого всё
/// заводилось, разбирают в тот же день, а не через месяц, — и перезапуск программы, после которого
/// одна лишняя запись всё-таки уедет, дешевле, чем ещё один ключ в общем конфиге, который приедет
/// с чужой машины и заткнёт запись там, где её не было ни разу (ровно та беда, от которой местными
/// сделаны отметки слепка, см. DiskSnapshotSchedule.LastDoneKey).
///
/// Решение чистое: время передаётся снаружи, файлов и сети здесь нет — это и проверяется тестом.</summary>
public sealed class SearchTraceLedger
{
    /// <summary>Потолок записей в сутки с одной машины. Два десятка — это заведомо больше, чем
    /// РАЗНЫХ случаев бывает за день (их обычно один-два), и заведомо меньше, чем нужно, чтобы
    /// папка стала нечитаемой.</summary>
    public int MaxPerDay { get; init; } = 20;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private DateTime _dayStartUtc = DateTime.MinValue;
    private int _sentToday;

    /// <summary>Отправлять ли этот случай. Вызов СЧИТАЕТСЯ отправкой: второй раз тот же отпечаток
    /// уже не пройдёт.</summary>
    public bool Allow(SearchTrace? trace, DateTime nowUtc)
    {
        if (!SearchTraceDrop.Interesting(trace)) return false;

        // Сутки считаются от первой записи, а не по календарю: смысл в «не больше N за сутки», и
        // полночь в середине разбора не должна открывать новый счёт.
        if (nowUtc - _dayStartUtc >= TimeSpan.FromHours(24))
        {
            _dayStartUtc = nowUtc;
            _sentToday = 0;
            // Забытое за сутки повторится — и это правильно: случай, который длится второй день,
            // стоит прислать ещё раз, он уже не «тот же самый десятый повтор запроса».
            _seen.Clear();
        }

        if (_sentToday >= MaxPerDay) return false;
        if (!_seen.Add(trace!.Fingerprint())) return false;

        _sentToday++;
        return true;
    }
}
