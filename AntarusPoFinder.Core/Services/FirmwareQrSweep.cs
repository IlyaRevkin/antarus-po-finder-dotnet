using System.Collections.Generic;
using AntarusPoFinder.Core.Data;

namespace AntarusPoFinder.Core.Services;

/// <summary>Проход «привести таблицу QR-кодов в соответствие с базой»: завести недостающие,
/// перестроить разошедшиеся, убрать лишние. Один проход закрывает все три действия сразу — именно
/// потому, что ни одно из них не привязано к конкретному событию (почему так, а не «ловить события»
/// — см. <see cref="FirmwareQrCodes"/>).
///
/// <b>Сети и диска не требует вовсе.</b> Всё, из чего считается ссылка, лежит в базе и настройках,
/// поэтому проход одинаково отрабатывает и при живом сетевом диске, и когда тот отвалился, — ради
/// чего задача и ставилась. Обхода папок здесь нет и быть не должно: стоит сюда попасть хоть одному
/// <c>Directory.Exists</c> по сетевому пути, и проход начнёт зависать на минуты у человека вне
/// офиса.
///
/// <b>Старые версии, которым QR ещё не считали</b>, разбираются этим же проходом в общем порядке:
/// отдельной «разовой миграции» для них нет и не нужно — у версии без строки отпечаток не совпадает
/// ни с чем, значит она просто «разошедшаяся». Первый проход на уже наполненной базе заводит их все
/// разом (тысячи строк — это тысячи коротких хэшей, доли секунды), дальше проходы почти всегда
/// холостые: совпал отпечаток — в базу не пишем ничего.</summary>
public static class FirmwareQrSweep
{
    /// <summary>Итог прохода. Нужен для журнала и для тестов; человеку он не показывается — проход
    /// фоновый и обязан быть молчаливым.</summary>
    public sealed record Result(int Added, int Updated, int Removed, int Unchanged)
    {
        public bool Changed => Added > 0 || Updated > 0 || Removed > 0;
        public int Total => Added + Updated + Unchanged;
    }

    /// <summary>Настройки, от которых зависит ссылка. Отдельной записью, чтобы проход не зависел от
    /// <see cref="ConfigService"/> — так его можно прогнать тестом, не поднимая конфиг целиком.</summary>
    public sealed record Options(string BaseUrl, string DiskRoot, TranslitMap Translit, string HoleText);

    /// <param name="toUnc">Перевод локального пути в сетевой — живёт в приложении (см.
    /// App.Services.NetworkPathHelper), поэтому передаётся функцией. null — путь идёт как есть.</param>
    public static Result Run(Database db, Options options, Func<string, string>? toUnc = null)
    {
        var removed = db.PruneFwQrCodes();

        var stored = db.GetFwQrCodes();
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var subject in db.GetFwQrSubjects())
        {
            stored.TryGetValue(subject.FwVersionId, out var row);

            // Сохранённая строка годится, если отпечаток в ней сходится с тем, что даёт ЕЁ
            // СОБСТВЕННЫЙ источник при сегодняшних настройках. Проверять именно так, а не по
            // instructions_path версии, — единственный способ не затереть точную ссылку, которую
            // собрало окно наклейки, увидев живой диск (см. FirmwareQrCodes.Inputs). А то, что
            // instructions_path тоже входит в отпечаток, не даёт этой уступке превратиться в дыру:
            // перепривязали документ — отпечаток разошёлся, и строка всё равно перестроится.
            if (row is not null && row.MatchesFingerprint(FirmwareQrCodes.Fingerprint(new FirmwareQrCodes.Inputs(
                    row.SourcePath, subject.InstructionPath, options.BaseUrl, options.DiskRoot,
                    options.Translit.Signature))))
            {
                unchanged++;
                continue;
            }

            // Перестраиваем по записанному у версии пути: диска под рукой может не быть, а
            // приблизительная ссылка лучше отсутствующей. Как только окно наклейки откроют при живом
            // диске, строка уточнится сама (FirmwareQrSweep.Ensure).
            var inputs = FirmwareQrCodes.Inputs.FromRecord(
                subject.InstructionPath, options.BaseUrl, options.DiskRoot, options.Translit.Signature);
            var payload = FirmwareQrCodes.Build(inputs, options.Translit, toUnc);
            db.SaveFwQrCode(subject.FwVersionId, subject.SyncId, payload.Content,
                FirmwareQrCodes.KindName(payload.Kind), options.HoleText, subject.InstructionPath,
                payload.Fingerprint);

            if (row is null) added++; else updated++;
        }

        return new Result(added, updated, removed, unchanged);
    }

    /// <summary>Код ОДНОЙ версии, пригодный к показу прямо сейчас: сохранённый, если он сходится с
    /// сегодняшними входными данными, иначе пересчитанный (и тут же записанный). Этим же путём
    /// получает свой код версия, до которой фоновый проход ещё не дошёл, — поэтому окно наклейки
    /// никогда не ждёт прохода и не показывает пустоту «потому что не успели посчитать».
    ///
    /// <paramref name="sourcePath"/> — документ, который вызывающий УЖЕ нашёл (окно наклейки ходит
    /// на диск и знает точный файл). Пусто — не нашёл; тогда строим по записанному у версии пути,
    /// ровно как фоновый проход.</summary>
    public static FirmwareQrCodes.Payload Ensure(Database db, int fwVersionId, string syncId,
        string sourcePath, string instructionPath, Options options, Func<string, string>? toUnc = null)
    {
        if (fwVersionId <= 0) return FirmwareQrCodes.Payload.Empty;

        var source = string.IsNullOrWhiteSpace(sourcePath) ? instructionPath : sourcePath;
        var inputs = new FirmwareQrCodes.Inputs(
            source, instructionPath, options.BaseUrl, options.DiskRoot, options.Translit.Signature);
        var fingerprint = FirmwareQrCodes.Fingerprint(inputs);

        var stored = db.GetFwQrCode(fwVersionId);
        if (stored is not null && stored.MatchesFingerprint(fingerprint))
            return new FirmwareQrCodes.Payload(stored.Payload, stored.PayloadKind, fingerprint);

        var payload = FirmwareQrCodes.Build(inputs, options.Translit, toUnc);
        db.SaveFwQrCode(fwVersionId, syncId, payload.Content, FirmwareQrCodes.KindName(payload.Kind),
            options.HoleText, source, payload.Fingerprint);
        return payload;
    }

    /// <summary>Последнее, что мы знали про код этой версии, — БЕЗ пересчёта и без оглядки на
    /// отпечаток. Нужно ровно для одного случая: сетевой диск недоступен, свежую ссылку собрать не
    /// из чего, и показать сохранённую — единственное, что отличает работающую кнопку «QR
    /// инструкции» от пустого окна. Пусто — про версию мы и правда ничего не знаем.</summary>
    public static string LastKnown(Database db, int fwVersionId) =>
        db.GetFwQrCode(fwVersionId)?.Payload ?? "";
}
