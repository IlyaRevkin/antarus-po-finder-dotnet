using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>Хранилище тикетов поверх настоящего бакета. Вся сеть — здесь, всё слияние — в
/// <see cref="TicketStorageSync"/>: тесты обмена работают с подставным <see cref="ITicketStorage"/>
/// и ни ключей, ни сети не видят.
///
/// Задача этого класса ровно одна, кроме проброса запросов: перевод между ОТНОСИТЕЛЬНЫМ ключом
/// обмена («tickets/state/…») и НАСТОЯЩИМ ключом объекта, у которого спереди может стоять префикс
/// предприятия из настроек (S3Settings.Prefix — бакет общий, и части предприятий в нём разделены
/// именно им). Перевод сделан приклеиванием готового хвоста к переведённому корню, а не прогоном
/// всего ключа через <see cref="S3Settings.KeyFor"/>: хвост — это id тикета с точкой и расширением,
/// и чистка сегментов, рассчитанная на русские имена папок с диска, ему ни к чему.</summary>
public sealed class S3TicketStorage : ITicketStorage
{
    private readonly S3Settings _settings;
    private readonly S3Client _client;

    public S3TicketStorage(S3Settings settings, S3Client? client = null)
    {
        _settings = settings;
        _client = client ?? new S3Client();
    }

    /// <summary>Есть ли смысл ходить в хранилище. Пустые ключи — ШТАТНОЕ состояние (см. S3Settings):
    /// обмен тикетами тогда молча не делается, ровно как выкладка инструкций.</summary>
    public bool CanSync => _settings.CanPublish;

    /// <summary>Настоящий ключ корневой папки тикетов — с префиксом предприятия, если он задан.</summary>
    private string RootPrefix => _settings.KeyFor(TicketStorageSync.RootFolder);

    private string Absolute(string relativeKey) =>
        RootPrefix + relativeKey[TicketStorageSync.RootFolder.Length..];

    private string? Relative(string absoluteKey) =>
        absoluteKey.StartsWith(RootPrefix + "/", StringComparison.Ordinal)
            ? TicketStorageSync.RootFolder + absoluteKey[RootPrefix.Length..]
            : null;

    /// <summary>Перечисляет ВСЕ объекты под префиксом, проходя страницы до конца. Без группировки
    /// по «папкам»: нужен плоский список ключей, а не вид проводника.</summary>
    public async Task<TicketStorageListing> ListAsync(string relativePrefix, CancellationToken ct = default)
    {
        if (!CanSync) return TicketStorageListing.Fail("Хранилище на хостинге не настроено");

        var entries = new List<TicketStorageEntry>();
        string? token = null;
        do
        {
            var page = await _client.ListAsync(_settings, Absolute(relativePrefix.TrimEnd('/')) + "/",
                grouped: false, continuationToken: token, ct: ct).ConfigureAwait(false);
            if (!page.Ok) return TicketStorageListing.Fail(page.Error ?? "хранилище не ответило");

            foreach (var obj in page.Objects)
            {
                var rel = Relative(obj.Key);
                if (rel is not null) entries.Add(new TicketStorageEntry(rel, obj.Size, obj.Modified, obj.ETag));
            }
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token) && !ct.IsCancellationRequested);

        return TicketStorageListing.Success(entries);
    }

    public async Task<(string? Json, string? Error)> GetAsync(string relativeKey, CancellationToken ct = default)
    {
        if (!CanSync) return (null, "Хранилище на хостинге не настроено");
        var (text, error) = await _client.GetStringAsync(_settings, Absolute(relativeKey), ct).ConfigureAwait(false);
        return (text, error);
    }

    public async Task<string?> PutAsync(string relativeKey, string json, CancellationToken ct = default)
    {
        if (!CanSync) return "Хранилище на хостинге не настроено";
        // ⚠️ Тип БЕЗ параметров: S3Client.BuildRequest кладёт его в MediaTypeHeaderValue, а тот на
        // «application/json; charset=utf-8» бросает FormatException — запрос не ушёл бы вовсе, и
        // выглядело бы это как «хранилище не отвечает». JSON и так читается как UTF-8.
        var result = await _client.PutBytesAsync(_settings, Absolute(relativeKey),
            Encoding.UTF8.GetBytes(json), "application/json", ct).ConfigureAwait(false);
        return result.Ok ? null : result.Error ?? "не удалось положить объект";
    }
}
