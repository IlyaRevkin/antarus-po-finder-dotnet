using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Свои столбцы документа: завести, переименовать, переставить, убрать.
///
/// Отдельным окном, а не рядами кнопок в окне документа: столбцы правят редко и не тогда же, когда
/// правят строки, а место в шапке документа занято тем, чем пользуются на объекте, — выбором
/// документа и отбором по аппарату.
///
/// Правки записываются СРАЗУ, без «Сохранить». Причина та же, что у кнопки-корзины в модерации:
/// окно закрывают крестиком, и «Сохранить», которое человек не нажал, выглядит как потерянная
/// работа. Ревизий это не заводит — столбцы принадлежат документу, а не редакции.
///
/// Правила (какое название годится, куда встаёт столбец) живут в ParamTableColumnEditing под
/// тестами; окно только показывает и спрашивает.</summary>
public partial class ParamTableColumnsDialog : Window
{
    private readonly Database _db;
    private readonly int _tableId;
    private List<ParamTableColumn> _columns = new();

    /// <summary>Хоть что-то поменялось — окну документа надо перечитать таблицу и подтолкнуть
    /// отправку конфига.</summary>
    public bool Changed { get; private set; }

    private sealed class ColumnVm
    {
        public ParamTableColumn Source { get; init; } = new();
        public string Title => Source.Title;
        public string Note { get; init; } = "";
    }

    public ParamTableColumnsDialog(Database db, int tableId, string documentName)
    {
        InitializeComponent();
        _db = db;
        _tableId = tableId;
        Title = "Свои столбцы документа: " + documentName;
        Reload(null);
    }

    private void Reload(string? selectKey)
    {
        _columns = _db.GetParamTableColumns(_tableId);

        // Сколько строк последней редакции столбец уже заполняет — единственное, что о нём стоит
        // сказать: по этому числу видно, живой он или заведён и забыт.
        var latest = ParamTableNumbering.LiveRevisions(_db, _tableId).FirstOrDefault();
        var rows = latest?.Id is int revisionId ? _db.GetParamTableRows(revisionId) : new List<ParamTableRow>();
        var filled = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            foreach (var key in ParamRowExtra.Parse(row.Extra).Keys)
                filled[key] = filled.TryGetValue(key, out var count) ? count + 1 : 1;

        ColumnsList.ItemsSource = _columns.Select(c => new ColumnVm
        {
            Source = c,
            Note = filled.TryGetValue(c.Key, out var count)
                ? $"заполнен в строках последней редакции: {count}"
                : "пока не заполнен ни в одной строке",
        }).ToList();

        CountLabel.Text = _columns.Count == 0
            ? "Своих столбцов пока нет"
            : $"Своих столбцов: {_columns.Count}";

        var at = selectKey is null ? -1 : _columns.FindIndex(c =>
            string.Equals(c.Key, selectKey, System.StringComparison.OrdinalIgnoreCase));
        ColumnsList.SelectedIndex = at >= 0 ? at : _columns.Count > 0 ? 0 : -1;
        UpdateButtons();
    }

    private ParamTableColumn? Current => (ColumnsList.SelectedItem as ColumnVm)?.Source;

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var at = ColumnsList.SelectedIndex;
        var has = at >= 0;
        RenameBtn.IsEnabled = has;
        RemoveBtn.IsEnabled = has;
        UpBtn.IsEnabled = has && at > 0;
        DownBtn.IsEnabled = has && at + 1 < _columns.Count;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var title = TextPromptDialog.Prompt(this, "Новый столбец",
            "Как назвать столбец — так он и будет подписан в таблице:");
        if (title is null) return;

        if (Refused(title, exceptId: null)) return;

        _db.AddParamTableColumn(_tableId, title);
        Changed = true;
        Reload(ParamTableColumnEditing.KeyFor(title));
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { Id: int id } column) return;

        var title = TextPromptDialog.Prompt(this, "Переименовать столбец",
            "Новый заголовок. Значения, уже записанные в этот столбец, останутся на месте:", column.Title);
        if (title is null || title.Trim() == column.Title) return;

        if (Refused(title, exceptId: id)) return;

        _db.UpdateParamTableColumn(id, title, column.SortOrder);
        Changed = true;
        Reload(column.Key);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        var at = ColumnsList.SelectedIndex;
        if (at < 0) return;
        var key = _columns[at].Key;

        ParamTableColumnEditing.ApplyOrder(_db, ParamTableColumnEditing.Moved(_columns, at, delta));
        Changed = true;
        Reload(key);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { Id: int id } column) return;

        var answer = AppMessageBox.Show(
            $"Убрать свой столбец «{column.Title}»?\n\n"
            + "В новых редакциях его не будет. В уже сохранённых он останется вместе с тем, что в него записали: "
            + "ревизия — снимок, и переписывать её задним числом нельзя.",
            "Свои столбцы документа", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _db.TombstoneParamTableColumn(id);
        Changed = true;
        Reload(null);
    }

    /// <summary>Сказать вслух, что с названием не так, и вернуть true, если заводить его нельзя.
    /// Правило считает ядро — окно только показывает ответ.</summary>
    private bool Refused(string title, int? exceptId)
    {
        var why = ParamTableColumnEditing.WhyTitleWontDo(
            _db.AllParamTableColumnsIncludingDeleted(_tableId), title, exceptId);
        if (why is null) return false;

        AppMessageBox.Show(why, "Свои столбцы документа", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }
}
