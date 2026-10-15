using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Domain;

/// <summary>ЧЕМ ИМЕННО ОБЪЯВЛЯЕТСЯ ЗАГРУЖЕННАЯ ПРОШИВКА.
///
/// Жалоба Ильи 15.10.2026: «вот коллега выложил прошивку, в уведомлении написано только номер
/// прошивки, надо ещё отображать что изменилась какой тип прошивки и в целом больше инфы».
///
/// Текст уведомления рождается один раз, при загрузке (UploadView), и дальше едет как есть: в
/// накопитель правок, в журнал ревизии общего конфига и на экран коллеги — плашкой «Поступили
/// изменения». Поэтому в нём должно быть всё, что нужно, чтобы понять, о чём речь, НЕ открывая
/// ничего: голый «2.1.0004.0002» не говорит ни о шкафе, ни о контроллере, ни о том, что изменилось.
///
/// Собирается здесь, в Core, а не прямо в обработчике кнопки, по одной причине: текст — это
/// договорённость между машинами (он же и приезжает коллеге), и проверять его надо тестом, а не
/// глазами на живой загрузке.</summary>
public static class FwChangeAnnouncement
{
    /// <summary>Сколько символов описания берём в строку уведомления. Описание бывает на абзац, а
    /// строка едет в плашку и в журнал ревизии, где длинный текст вытесняет всё остальное.</summary>
    public const int MaxDescription = 90;

    /// <summary>«Загружена прошивка НГР/КНС/SMH4 2.1.0004.0002 · 3 насоса · ОПЦ (заявка 47137) —
    /// добавлен контроль сухого хода».
    ///
    /// Чего здесь намеренно нет: имени автора. Оно и так едет отдельным полем журнала
    /// (SyncChangeEntry.Author) и показывается в подробностях — дублировать его в тексте значит
    /// тратить ширину плашки на то, что уже видно.</summary>
    public static string Uploaded(string groupName, string subtypeName, string controllerName,
        string versionRaw, string execution = "", string description = "",
        bool isOpc = false, string requestNum = "", string cabinetSn = "")
    {
        var where = string.Join("/", new[] { groupName, subtypeName, controllerName }
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0));

        var head = string.IsNullOrEmpty(where)
            ? $"Загружена прошивка {versionRaw}"
            : $"Загружена прошивка {where} {versionRaw}";

        var parts = new List<string> { head };

        // Исполнение — своя линейка прошивок («3 насоса» и «ПЧ Danfoss» актуальны одновременно, см.
        // FwExecution). Без него два уведомления об одном шкафе отличаются только номером версии.
        if (!string.IsNullOrWhiteSpace(execution)) parts.Add(execution.Trim());

        if (isOpc)
        {
            var marks = new[] { requestNum, cabinetSn }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            parts.Add(marks.Count > 0
                ? $"{FwCardMeta.Marker} ({string.Join(", ", marks.Select(m => m.Trim()))})"
                : FwCardMeta.Marker);
        }

        var line = string.Join(" · ", parts);
        var what = Shorten(description);
        return string.IsNullOrEmpty(what) ? line : $"{line} — {what}";
    }

    /// <summary>Описание в одну строку: переводы строк схлопываются (в плашке многострочный текст
    /// всё равно показан одной строкой, а в журнале ломает формат), длинное обрезается по границе
    /// слова — обрыв посреди слова читается как сбой, а не как сокращение.</summary>
    public static string Shorten(string? description, int max = MaxDescription)
    {
        var text = string.Join(" ", (description ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0));
        if (text.Length <= max) return text;

        var cut = text[..max];
        var space = cut.LastIndexOf(' ');
        if (space > max / 2) cut = cut[..space];
        return cut.TrimEnd(' ', ',', '.', ';', ':', '-', '—') + "…";
    }
}
