using System;
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
/// это переворот относительно ДЛИННОГО края, то самое «книжкой», которого просили избежать.</summary>
public static class PrintTicketXml
{
    public static readonly XNamespace Framework = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    public static readonly XNamespace Keywords = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

    /// <summary>Признак «двусторонняя печать всего задания» в терминах схемы.</summary>
    public const string DuplexFeature = "JobDuplexAllDocumentsContiguously";

    /// <summary>Переворот относительно КОРОТКОГО края — то, что нужно паспорту.</summary>
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
