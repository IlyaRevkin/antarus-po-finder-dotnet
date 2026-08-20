using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.App.Services;

/// <summary>«теги добавлены: 2 насоса, жокей; убраны: черновик» — единственное место, где правка
/// набора тегов превращается в человеческую фразу.
///
/// Тикет коллеги: «добавить более подробную информацию, какие теги и куда добавились». Безликое
/// «изменены теги» в уведомлении и в списке «готово к отправке» не давало понять, что именно
/// уедет коллегам, — а откатить уже отправленное дороже, чем прочитать одну строку.
///
/// Вынесено из EditFirmwareDialog, потому что ровно та же фраза нужна файлам параметров ПЧ/УПП:
/// там сообщение было «Теги обновлены: <имя файла>» вообще без перечисления.</summary>
public static class TagChangeText
{
    /// <summary>Что изменилось в наборе тегов. Пустая строка — если не изменилось ничего.
    ///
    /// Порядок алфавитный: набор тегов — множество, «как ввели» тут смысла не несёт.</summary>
    public static string Describe(IEnumerable<string> before, IEnumerable<string> after)
    {
        var was = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
        var now = new HashSet<string>(after, StringComparer.OrdinalIgnoreCase);

        var added = now.Except(was, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();
        var removed = was.Except(now, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();

        var bits = new List<string>();
        if (added.Count > 0) bits.Add("добавлены: " + string.Join(", ", added));
        if (removed.Count > 0) bits.Add("убраны: " + string.Join(", ", removed));
        return bits.Count == 0 ? "" : "теги " + string.Join("; ", bits);
    }
}
