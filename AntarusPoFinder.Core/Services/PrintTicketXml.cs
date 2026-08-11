using System.Linq;
using System.Xml.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Правка задания печати (PrintTicket) на уровне XML.
///
/// Зачем вообще: Илья просил про паспорт дословно — «важно, чтобы при печати настройки не сбивались:
/// печатать с двух сторон, разворачивать относительно короткого края». Отправка файла на печать через
/// ассоциацию Windows («напечатать этот PDF») никаких настроек не несёт: печатается тем, что стоит у
/// принтера сейчас, то есть каждый раз «как получится». Единственное место, где эти два параметра
/// живут в Windows, — PrintTicket очереди печати; отсюда и эта правка.
///
/// Почему XML, а не объект System.Printing.PrintTicket: PrintTicket сериализуется в этот же самый
/// XML (Print Schema Framework) и из него же читается, а XML — обычные данные, которые можно
/// проверить тестом на любой машине. Прикладной слой (App) остаётся тонким: прочитал текущий тикет
/// очереди, отдал сюда, записал обратно. Без этого разделения проверить «переворот относительно
/// короткого края действительно проставлен» можно было бы только на машине с принтером.
///
/// «Относительно короткого края» в схеме — это psk:TwoSidedShortEdge у признака
/// psk:JobDuplexAllDocumentsContiguously (двусторонняя печать всего задания); psk:TwoSidedLongEdge —
/// это переворот относительно ДЛИННОГО края, то самое «книжкой».
///
/// ДВА РАЗНЫХ РЕЖИМА (просьба Ильи): «для паспорта мне нужно как буклет печатать, разворачивая
/// относительно короткого края, а инструкцию просто как лист с двусторонней печатью». Отсюда две
/// сборки тикета:
///   • ПАСПОРТ — буклет: две страницы на лист (psk:JobNUpAllDocumentsContiguously, PagesPerSheet=2)
///     плюс двусторонняя печать с переворотом по КОРОТКОМУ краю. Настоящую брошюровку (перекладку
///     страниц) тикет переносимо выразить не может — её делает драйвер; поэтому буклет собирается
///     честно как «2-up + короткий край», ровно как и разрешил владелец на случай, когда драйвер
///     брошюровку через тикет не поддерживает. См. <see cref="ApplyPassportBooklet"/>.
///   • ИНСТРУКЦИЯ — обычный лист: двусторонняя печать с переворотом по ДЛИННОМУ краю (привычная
///     «двусторонняя», страница листается как в книге) и одна страница на лист (без буклета). См.
///     <see cref="ApplyInstructionDuplex"/>.</summary>
public static class PrintTicketXml
{
    public static readonly XNamespace Framework = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    public static readonly XNamespace Keywords = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

    /// <summary>Пространства имён XML-схемы — нужны только затем, чтобы значение числа страниц на лист
    /// в NUp несло тип (xsi:type="xsd:integer"), как его сериализует и сам Windows.</summary>
    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    public static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    /// <summary>Признак «двусторонняя печать всего задания» в терминах схемы.</summary>
    public const string DuplexFeature = "JobDuplexAllDocumentsContiguously";

    /// <summary>Признак «сколько страниц печатать на одном листе» (N-up). У паспорта-буклета — 2.</summary>
    public const string NUpFeature = "JobNUpAllDocumentsContiguously";

    /// <summary>Свойство внутри NUp, несущее само число страниц на лист.</summary>
    public const string PagesPerSheetProperty = "PagesPerSheet";

    /// <summary>Переворот относительно КОРОТКОГО края — то, что нужно паспорту-буклету.</summary>
    public const string TwoSidedShortEdge = "TwoSidedShortEdge";

    public const string TwoSidedLongEdge = "TwoSidedLongEdge";
    public const string OneSided = "OneSided";

    /// <summary>Возвращает тикет с проставленной двусторонней печатью и переворотом относительно
    /// короткого края. Всё остальное в тикете (бумага, лоток, качество, поля) остаётся как было —
    /// это настройки принтера, и переписывать их за человека мы не собирались. Пустой/непонятный
    /// тикет — соберём минимальный сами: лучше задание с одним нужным параметром, чем ничего.</summary>
    public static string ApplyTwoSidedShortEdge(string? ticketXml) => ApplyDuplex(ticketXml, TwoSidedShortEdge);

    /// <summary>То же для любого варианта из схемы — вынесено отдельно, чтобы «вернуть как было»
    /// (односторонняя) не требовало второй копии этого кода.</summary>
    public static string ApplyDuplex(string? ticketXml, string option)
    {
        var root = Root(ticketXml);
        var keywordsPrefix = EnsureKeywordsPrefix(root);

        var feature = FindByName(root.Elements(Framework + "Feature"), DuplexFeature);
        if (feature is null)
        {
            feature = new XElement(Framework + "Feature", new XAttribute("name", $"{keywordsPrefix}:{DuplexFeature}"));
            root.Add(feature);
        }
        else
        {
            feature.SetAttributeValue("name", $"{keywordsPrefix}:{DuplexFeature}");
        }

        // У признака ровно один выбранный вариант: прежние Option — это «как печатали раньше», и
        // оставленные рядом они сделали бы задание противоречивым.
        feature.Elements(Framework + "Option").Remove();
        feature.Add(new XElement(Framework + "Option", new XAttribute("name", $"{keywordsPrefix}:{option}")));

        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Что стоит в тикете сейчас — короткое имя варианта (<see cref="TwoSidedShortEdge"/> и
    /// т.п.) либо null, если про двустороннюю печать в тикете не сказано ничего.</summary>
    public static string? DuplexOption(string? ticketXml)
    {
        XElement root;
        try { root = XDocument.Parse(ticketXml ?? "").Root ?? throw new InvalidOperationException(); }
        catch (Exception) { return null; }

        var feature = FindByName(root.Elements(Framework + "Feature"), DuplexFeature);
        var option = feature?.Elements(Framework + "Option").FirstOrDefault();
        return option is null ? null : LocalName(option, option.Attribute("name")?.Value);
    }

    // ── Два режима печати ─────────────────────────────────────────────────────────────────────

    /// <summary>ПАСПОРТ, буклет: две страницы на лист + двусторонняя печать с переворотом по короткому
    /// краю. Порядок правок не важен — каждая трогает свой признак и сохраняет остальной тикет.</summary>
    public static string ApplyPassportBooklet(string? ticketXml) =>
        ApplyPagesPerSheet(ApplyDuplex(ticketXml, TwoSidedShortEdge), 2);

    /// <summary>ИНСТРУКЦИЯ, обычный лист: двусторонняя печать с переворотом по длинному краю (привычная
    /// «двусторонняя») и одна страница на лист — никакого буклета. PagesPerSheet=1 выставляется явно:
    /// на принтере, где до этого печатали буклет, инструкция иначе так и уходила бы по две на лист.</summary>
    public static string ApplyInstructionDuplex(string? ticketXml) =>
        ApplyPagesPerSheet(ApplyDuplex(ticketXml, TwoSidedLongEdge), 1);

    /// <summary>Выставляет число страниц на лист (N-up). Значение несёт тип, как это делает и сам
    /// Windows: <c>&lt;psf:Value xsi:type="xsd:integer"&gt;N&lt;/psf:Value&gt;</c>. Как и у двусторонней
    /// печати, прежний вариант признака заменяется целиком — двух Option у одного признака быть не должно.</summary>
    public static string ApplyPagesPerSheet(string? ticketXml, int pagesPerSheet)
    {
        var root = Root(ticketXml);
        var keywordsPrefix = EnsureKeywordsPrefix(root);
        EnsureSchemaPrefixes(root);

        var feature = FindByName(root.Elements(Framework + "Feature"), NUpFeature);
        if (feature is null)
        {
            feature = new XElement(Framework + "Feature", new XAttribute("name", $"{keywordsPrefix}:{NUpFeature}"));
            root.Add(feature);
        }
        else
        {
            feature.SetAttributeValue("name", $"{keywordsPrefix}:{NUpFeature}");
            feature.Elements(Framework + "Option").Remove();
        }

        feature.Add(new XElement(Framework + "Option",
            new XElement(Framework + "ScoredProperty",
                new XAttribute("name", $"{keywordsPrefix}:{PagesPerSheetProperty}"),
                new XElement(Framework + "Value",
                    new XAttribute(Xsi + "type", "xsd:integer"),
                    pagesPerSheet))));

        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Сколько страниц на лист стоит в тикете сейчас, либо null, если про N-up не сказано
    /// ничего. Нужно тестам: убедиться, что у паспорта 2 (буклет), а у инструкции 1 (обычный лист).</summary>
    public static int? PagesPerSheet(string? ticketXml)
    {
        XElement root;
        try { root = XDocument.Parse(ticketXml ?? "").Root ?? throw new InvalidOperationException(); }
        catch (Exception) { return null; }

        var feature = FindByName(root.Elements(Framework + "Feature"), NUpFeature);
        var value = feature?.Elements(Framework + "Option")
            .Elements(Framework + "ScoredProperty")
            .FirstOrDefault(sp => string.Equals(LocalName(sp, sp.Attribute("name")?.Value), PagesPerSheetProperty, StringComparison.OrdinalIgnoreCase))
            ?.Element(Framework + "Value");
        return value is not null && int.TryParse(value.Value.Trim(), out var n) ? n : null;
    }

    /// <summary>Объявляет префиксы xsi/xsd, если их ещё нет: значение числа страниц на лист ссылается
    /// на xsd:integer, и без объявления драйвер не разобрал бы тип.</summary>
    private static void EnsureSchemaPrefixes(XElement root)
    {
        if (string.IsNullOrEmpty(root.GetPrefixOfNamespace(Xsi)))
            root.SetAttributeValue(XNamespace.Xmlns + "xsi", Xsi.NamespaceName);
        if (string.IsNullOrEmpty(root.GetPrefixOfNamespace(Xsd)))
            root.SetAttributeValue(XNamespace.Xmlns + "xsd", Xsd.NamespaceName);
    }

    private static XElement Root(string? ticketXml)
    {
        if (!string.IsNullOrWhiteSpace(ticketXml))
        {
            try
            {
                if (XDocument.Parse(ticketXml).Root is { } parsed && parsed.Name == Framework + "PrintTicket")
                    return parsed;
            }
            catch (Exception)
            {
                // Тикет не разобрался (обрезанный, чужого формата) — соберём свой ниже. Ронять из-за
                // этого печать нельзя: без правки задание просто уйдёт с настройками принтера.
            }
        }

        return new XElement(Framework + "PrintTicket",
            new XAttribute(XNamespace.Xmlns + "psf", Framework.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "psk", Keywords.NamespaceName),
            new XAttribute("version", "1"));
    }

    /// <summary>Префикс, которым в этом тикете записаны имена из словаря схемы. Свой у каждого
    /// генератора тикета, поэтому берём объявленный, а не «psk» наугад; не объявлен — объявляем.</summary>
    private static string EnsureKeywordsPrefix(XElement root)
    {
        var existing = root.GetPrefixOfNamespace(Keywords);
        if (!string.IsNullOrEmpty(existing)) return existing;
        root.SetAttributeValue(XNamespace.Xmlns + "psk", Keywords.NamespaceName);
        return "psk";
    }

    private static XElement? FindByName(System.Collections.Generic.IEnumerable<XElement> elements, string localName) =>
        elements.FirstOrDefault(e => string.Equals(LocalName(e, e.Attribute("name")?.Value), localName, StringComparison.OrdinalIgnoreCase));

    /// <summary>«psk:TwoSidedShortEdge» → «TwoSidedShortEdge», но только если префикс действительно
    /// указывает на словарь схемы: имя из чужого пространства имён — это чужой признак, и считать
    /// его нашим нельзя.</summary>
    private static string? LocalName(XElement scope, string? qualified)
    {
        if (string.IsNullOrWhiteSpace(qualified)) return null;
        var colon = qualified.IndexOf(':');
        if (colon < 0) return qualified;

        var ns = scope.GetNamespaceOfPrefix(qualified[..colon]);
        return ns is not null && ns != Keywords ? null : qualified[(colon + 1)..];
    }
}
