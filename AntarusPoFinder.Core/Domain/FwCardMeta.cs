using System.Collections.Generic;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Строка под названием прошивки на карточке выдачи — та, где исполнение, комплектация,
/// контроллер, тип оборудования и дата.
///
/// Вынесена из кода карточки в Core затем, что это НЕ оформление, а решение о том, что человек
/// прочитает первым. Пока строка собиралась прямо в обработчике, проверить её можно было только
/// глазами, и каждая правка подписи возвращалась жалобой.
///
/// Главное правило здесь: подписи вида «Конфигурация: …» не пишутся. Название комплектации уже
/// говорит само за себя — «2 насоса» понятно без слова «конфигурация», а «Конфигурация:
/// 2 конфигурация» не значит вообще ничего. Дословно: «название конфигурации (по типу „2 насоса")
/// уже достаточно, слово конфигурация и какая конфигурация избыточно для вывода». Что означает
/// пометка, объясняет подсказка при наведении, а не каждая строка выдачи.</summary>
public static class FwCardMeta
{
    public const string Separator = "  ·  ";

    /// <summary>Слово, которым ОПЦ-прошивка называет себя в строке карточки. Вынесено константой,
    /// потому что ровно это же слово стоит на значке в шапке карточки и в папке на диске
    /// (HierarchyDefaults.Opc) — разъехавшись, они читались бы как три разные вещи.</summary>
    public const string Marker = "ОПЦ";

    /// <summary>Собирает части строки в том порядке, в каком их читают.
    ///
    /// Исполнение идёт ПЕРВЫМ: это ответ на вопрос «чем эта прошивка отличается от соседней» —
    /// «2 насоса», «3 и более насосов». Именно его наладчик и ищет глазами, а не номер и не дату.</summary>
    public static List<string> Parts(
        string execution, string configName, string controller, string equipmentType,
        string workType, string uploadDate, int usageCount)
    {
        var parts = new List<string>();

        Add(execution);
        // Без подписи — см. описание класса.
        Add(configName);
        // У контроллера подпись ОСТАЁТСЯ: «SMH5» само по себе ни о чём не говорит тому, кто
        // пришёл в программу впервые, а спутать его с названием шкафа легко.
        if (!string.IsNullOrWhiteSpace(controller)) parts.Add($"Контроллер: {controller.Trim()}");
        Add(equipmentType);
        Add(workType);
        Add(uploadDate);

        // «По такому же запросу эту версию уже ставили N раз» — то, из-за чего она стоит выше
        // остальных (см. Database.FwUsage.cs). Без этой строки подъём выглядел бы необъяснимым.
        if (usageCount > 0)
            parts.Add(usageCount == 1
                ? "по этому запросу выбирали 1 раз"
                : $"по этому запросу выбирали {usageCount} раз");

        return parts;

        void Add(string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) parts.Add(value.Trim());
        }
    }

    public static string Line(
        string execution, string configName, string controller, string equipmentType,
        string workType, string uploadDate, int usageCount) =>
        string.Join(Separator, Parts(execution, configName, controller, equipmentType, workType, uploadDate, usageCount));

    /// <summary>Короткая строка карточки — только то, что наладчик читает глазами на бегу:
    /// исполнение, комплектация, контроллер.
    ///
    /// Тикет: «избавиться от лишних пилюль в карточке» и «мб добавить значок i, тогда продумать, что
    /// там ещё можно отобразить». Выдача — это столбик одинаковых карточек, и строка из шести пунктов
    /// через точку в нём не читается: глаз скользит мимо всей строки целиком, включая нужное.
    /// Поэтому тип оборудования, вид работ, дата загрузки и счётчик выборов уезжают под значок —
    /// они отвечают на вопросы, которые задают РЕДКО и по одной карточке, а не по всей выдаче.</summary>
    public static string ShortLine(string execution, string configName, string controller)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(execution)) parts.Add(execution.Trim());
        if (!string.IsNullOrWhiteSpace(configName)) parts.Add(configName.Trim());
        if (!string.IsNullOrWhiteSpace(controller)) parts.Add($"Контроллер: {controller.Trim()}");
        return string.Join(Separator, parts);
    }

    /// <summary>Что показывает значок «i»: пары «подпись — значение» в том порядке, в каком их
    /// спрашивают. Пустые значения не попадают сюда вовсе — строка «Автор: —» не отвечает ни на
    /// какой вопрос, а место занимает.</summary>
    public static List<(string Label, string Value)> Details(
        string equipmentType, string workType, string uploadDate, int usageCount,
        string sourceName, string author, string hardware, string requestNum, string cabinetSn)
    {
        var rows = new List<(string, string)>();
        Add("Исходное название", sourceName);
        Add("Автор", author);
        Add("Тип оборудования", equipmentType);
        Add("Вид работ", workType);
        Add("Железо", hardware);
        Add("Загружена", uploadDate);
        Add("Заявка", requestNum);
        Add("Номер шкафа", cabinetSn);
        if (usageCount > 0)
            rows.Add(("По этому запросу выбирали", usageCount == 1 ? "1 раз" : $"{usageCount} раз"));
        return rows;

        void Add(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value.Trim()));
        }
    }

    /// <summary>Отдельная строка карточки — номера шкафа, под который собрана ОПЦ. ОТДЕЛЬНАЯ, а не
    /// часть строки выше, и это главное требование: «более явно номер заявки или SN». В общем ряду
    /// через точку, да ещё приглушённым цветом, они терялись между исполнением, контроллером и
    /// датой — а для разовой сборки это единственное, чем она себя называет.
    ///
    /// Подписи у номеров остаются: голые «01312» и «778899» не отличить друг от друга, а ищут
    /// прошивку то по одному, то по другому.</summary>
    public static string OpcLine(string requestNum, string cabinetSn)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestNum)) parts.Add($"Заявка {requestNum.Trim()}");
        if (!string.IsNullOrWhiteSpace(cabinetSn)) parts.Add($"SN {cabinetSn.Trim()}");
        return string.Join(Separator, parts);
    }
}
