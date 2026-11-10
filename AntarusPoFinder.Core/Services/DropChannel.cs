using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>ОДНА отправка файла на страницу приёма — общая для всех, кто ею пользуется.
///
/// Страницей приёма (<c>files.rev-il.ru/drop</c>) сегодня пользуются трое: журналы заливок в ПЛК
/// (<see cref="LoaderDiagnostics"/>), автовыгрузка тикетов (<see cref="TicketAutoDrop"/>) и слепок
/// диска (<see cref="DiskSnapshotDrop"/>). Первые два завели себе по собственной копии этих
/// двадцати строк, и третья копия означала бы, что смену правила («страница просит ещё одно поле»,
/// «код теперь в заголовке») придётся вспомнить в трёх местах. Поэтому отправка здесь одна.
///
/// Поле <c>silent=1</c> — не настройка, а свойство канала: сюда уезжает только то, чего человек не
/// просил и о чём ему не надо сообщать (правило проекта — фоновая работа беззвучна). Телеграм при
/// этом молчит, а файл ложится в подпапку <c>diag/</c>.</summary>
public static class DropChannel
{
    /// <summary>Чем кончилась отправка. <paramref name="Error"/> готов к показу и к записи в
    /// журнал.</summary>
    public sealed record SendResult(bool Ok, string? Error)
    {
        public static readonly SendResult Success = new(true, null);
        public static SendResult Fail(string error) => new(false, error);
    }

    /// <summary>Общий HttpClient с коротким терпением: отправка в этот канал — фоновая мелочь, и
    /// висеть на ней минутами незачем. Слепок диска бывает мегабайтным и просит больше времени,
    /// поэтому его отправка передаёт свой клиент (см. <see cref="DiskSnapshotDrop"/>).</summary>
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static HttpClient DefaultClient => Shared;

    /// <summary>Отправить один файл. Пустой адрес или код — молчаливый отказ без попытки: канал
    /// просто не настроен, и это штатное состояние, а не ошибка.</summary>
    public static async Task<SendResult> PostAsync(
        string url, string code, string comment, string fileName, byte[] body, string contentType,
        HttpClient? http = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(code))
            return SendResult.Fail("страница приёма не настроена");

        try
        {
            using var form = new MultipartFormDataContent();
            // Поле «code» идёт ПЕРВЫМ: страница сверяет его до чтения тела файла.
            form.Add(new StringContent(code), "code");
            form.Add(new StringContent(comment), "comment");
            form.Add(new StringContent("1"), "silent");

            var part = new ByteArrayContent(body);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, "file", fileName);

            using var response = await (http ?? Shared).PostAsync(url, form, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? SendResult.Success
                : SendResult.Fail($"страница приёма ответила {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Fail(ex.Message);
        }
    }
}
