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
        string workType, string uploadDate, int usageCount,
        bool isOpc = false, string requestNum = "", string cabinetSn = "")
    {
        var parts = new List<string>();

        // ОПЦ — ПЕРВЫМ, раньше исполнения, и с подписями у номеров. Это единственная пометка,
        // которая меняет сам смысл строки: обычную прошивку ставят всем шкафам линейки, ОПЦ —
        // ровно одному, под который её и собрали. Жалоба дословно: «я загрузил опц, нигде не
        // написано что это опц, в итоге номер заявки или сн не отображается в карточке» и «в
        // поисковой выдаче мне не отличить обычную от опц».
        //
        // Подписи «Заявка:» и «SN:» здесь обязательны, в отличие от названия комплектации: голые
        // «01312» и «SN00042» в ряду через точку читаются как мусор, а перепутать их между собой
        // легко — оба просто числа, и ищут прошивку то по одному, то по другому.
        if (isOpc) parts.Add(Marker);
        if (!string.IsNullOrWhiteSpace(requestNum)) parts.Add($"Заявка: {requestNum.Trim()}");
        if (!string.IsNullOrWhiteSpace(cabinetSn)) parts.Add($"SN: {cabinetSn.Trim()}");

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
        string workType, string uploadDate, int usageCount,
        bool isOpc = false, string requestNum = "", string cabinetSn = "") =>
        string.Join(Separator, Parts(execution, configName, controller, equipmentType, workType, uploadDate,
            usageCount, isOpc, requestNum, cabinetSn));
}
