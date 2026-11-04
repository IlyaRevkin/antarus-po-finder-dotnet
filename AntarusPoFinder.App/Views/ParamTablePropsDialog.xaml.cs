using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>СВОЙСТВА СВОБОДНОЙ ТАБЛИЦЫ ПАРАМЕТРОВ: чей это аппарат и к каким шкафам таблица
/// относится.
///
/// Жалоба Ильи дословно: «я в бете загружаю из файла таблицу ПЧ/УПП, но я не могу указать
/// производителя и к какому типу шкафов привязать». Так и было: производитель у документа в базе
/// есть давно (ParamTable.Manufacturer), но заполнялся он ТОЛЬКО копией из записи файла параметров
/// — у свободной таблицы файла нет, и поле оставалось пустым навсегда, спросить его было негде.
/// Привязка к шкафам у документа при файле ВЫВОДИТСЯ из записей param_files, а у свободной выводить
/// неоткуда — она живёт своим полем (см. Core/Services/ParamTableBinding.cs).
///
/// Окно маленькое и отдельно от правки строк намеренно: шапка документа правится без заведения
/// новой ревизии. Ревизия — снимок ЗНАЧЕНИЙ, и заводить её из-за того, что кто-то дописал
/// производителя, значит засорить историю документа записями ни о чём.
///
/// Свой выбор подтипов здесь НЕ пишется: берётся тот же SubtypeMultiSelect, что в форме загрузки
/// прошивки и на странице «Параметры». Производитель — из справочника (GetParamManufacturers), а не
/// списком строк в коде.</summary>
public partial class ParamTablePropsDialog : Window
{
    /// <summary>Пункт «не указан». Пустая строка в списке выглядела бы как промах отрисовки, а
    /// производителя у таблицы может честно не быть — например, пока её только набивают.</summary>
    private const string NotSet = "— не указан —";

    public string ResultManufacturer { get; private set; } = "";
    public List<int> ResultSubtypeIds { get; private set; } = new();

    public ParamTablePropsDialog(Database db, string tableName, string manufacturer, IEnumerable<int> subtypeIds)
    {
        InitializeComponent();
        TitleLabel.Text = tableName.Length > 0 ? $"Таблица: {tableName}" : "Новая таблица параметров";

        ManufacturerCombo.Items.Add(NotSet);
        foreach (var name in db.GetParamManufacturers()) ManufacturerCombo.Items.Add(name);
        // Производитель, которого в справочнике уже нет (убрали из списка, приехал с чужой машины),
        // добавляется пунктом сам: иначе сохранение молча стёрло бы то, что у таблицы записано.
        if (manufacturer.Length > 0 && !ManufacturerCombo.Items.Contains(manufacturer))
            ManufacturerCombo.Items.Add(manufacturer);
        ManufacturerCombo.SelectedItem = manufacturer.Length > 0 ? manufacturer : NotSet;

        // Подтипы — из ВСЕХ типов шкафа сразу, как и у файла параметров: один и тот же аппарат ПЧ
        // стоит в шкафах разных типов (у прошивки ПЛК так не бывает, там список в пределах группы).
        var targets = ParamTableBinding.Candidates(db);
        SubtypesSelect.SetItems(targets.Select(t => t.Subtype),
            preselectedIdsInOrder: subtypeIds,
            groupNamesBySubtypeId: targets.ToDictionary(t => t.Id, t => t.GroupName));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var picked = ManufacturerCombo.SelectedItem as string ?? NotSet;
        ResultManufacturer = picked == NotSet ? "" : picked;
        ResultSubtypeIds = SubtypesSelect.Selected
            .Where(s => s.Id is not null)
            .Select(s => s.Id!.Value)
            .ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
