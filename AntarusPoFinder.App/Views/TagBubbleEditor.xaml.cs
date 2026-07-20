using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AntarusPoFinder.App.Views;

/// <summary>Reusable bubble-style tag picker: existing tags render as removable chips, a trailing
/// "+ тег" chip turns into a single inline editable bubble — type free text or click a matching
/// suggestion chip, commit with Enter or by moving focus elsewhere. Used read-only (bubbles only,
/// no editing) on FirmwareCard, and editable in EditFirmwareDialog/Upload/Settings→Теги.</summary>
public partial class TagBubbleEditor : UserControl
{
    private readonly List<string> _tags = new();
    private Func<List<string>>? _allTagsProvider;
    private bool _readOnly;
    private bool _editing;
    private TextBox? _editInput;
    private StackPanel? _suggestionsPanel;

    /// <summary>Fires whenever a tag is added or removed — lets a host that has no explicit "Save"
    /// button (e.g. FirmwareCard's inline bubbles on a search result) persist the change immediately.</summary>
    public event EventHandler? TagsChanged;

    public TagBubbleEditor()
    {
        InitializeComponent();
    }

    public void Configure(IEnumerable<string> initialTags, Func<List<string>>? allTagsProvider = null, bool readOnly = false)
    {
        _tags.Clear();
        _tags.AddRange(initialTags.Where(t => !string.IsNullOrWhiteSpace(t)));
        _allTagsProvider = allTagsProvider;
        _readOnly = readOnly;
        _editing = false;
        Render();
    }

    public List<string> Tags => _tags.ToList();
    public string TagsText => string.Join(' ', _tags);

    private void Render()
    {
        BubblesPanel.Children.Clear();
        foreach (var tag in _tags)
            BubblesPanel.Children.Add(MakeBubble(tag));
        if (!_readOnly)
            BubblesPanel.Children.Add(_editing ? MakeEditBubble() : MakeAddBubble());
    }

    private Border MakeBubble(string tag)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center });
        if (!_readOnly)
        {
            var removeBtn = new Button
            {
                Content = "×",
                Style = (Style)FindResource("TagRemoveButton"),
                Margin = new Thickness(6, 0, 0, 0),
            };
            removeBtn.Click += (_, _) => { _tags.Remove(tag); Render(); TagsChanged?.Invoke(this, EventArgs.Empty); };
            panel.Children.Add(removeBtn);
        }
        return new Border { Style = (Style)FindResource("TagBubbleBorder"), Child = panel, Margin = new Thickness(0, 0, 6, 6) };
    }

    private Border MakeAddBubble()
    {
        var btn = new Button { Content = "+ тег", Style = (Style)FindResource("TagAddButton") };
        btn.Click += (_, _) => { _editing = true; Render(); };
        return new Border { Child = btn, Margin = new Thickness(0, 0, 6, 6) };
    }

    private Border MakeEditBubble()
    {
        var input = new TextBox
        {
            Width = 100,
            Height = 24,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
        };
        input.PreviewKeyDown += EditInput_PreviewKeyDown;
        input.LostFocus += (_, _) => CommitInlineEdit(continueEditing: false);
        input.TextChanged += EditInput_TextChanged;
        input.Loaded += (_, _) => input.Focus();
        _editInput = input;

        // Mouse-only way to commit the typed tag, same as pressing Enter — without this the only
        // way to add via mouse was clicking a suggestion chip; typing something with no matching
        // suggestion left no mouse path to confirm it. Deliberately a TextBlock, not a real Button —
        // same reasoning as MakeSuggestionChip below: a real Button would take focus on click, firing
        // the TextBox's LostFocus (which always commits with continueEditing:false) before this
        // handler runs, so the box wouldn't reopen the way Enter does.
        var commitBtn = new Border
        {
            Style = (Style)FindResource("TagBubbleBorder"),
            Child = new TextBlock { Text = "+", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center },
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Добавить тег",
        };
        commitBtn.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CommitInlineEdit(continueEditing: true);
        };

        _suggestionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(input);
        row.Children.Add(commitBtn);
        row.Children.Add(_suggestionsPanel);

        return new Border { Style = (Style)FindResource("TagBubbleBorder"), Child = row, Margin = new Thickness(0, 0, 6, 6) };
    }

    private void EditInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suggestionsPanel is null || _editInput is null) return;
        _suggestionsPanel.Children.Clear();
        var text = _editInput.Text.Trim();
        if (text.Length == 0) return;

        var suggestions = (_allTagsProvider?.Invoke() ?? new List<string>())
            .Where(t => !_tags.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t.Contains(text, StringComparison.OrdinalIgnoreCase) && !t.Equals(text, StringComparison.OrdinalIgnoreCase))
            .Take(4);

        foreach (var suggestion in suggestions)
            _suggestionsPanel.Children.Add(MakeSuggestionChip(suggestion));
    }

    /// <summary>Suggestion chips are non-focusable (TextBlock, not Button) so a click on them never
    /// steals focus from the edit TextBox — that would otherwise fire LostFocus/CommitInlineEdit
    /// with the not-yet-updated text before the suggestion's own click handler gets to run.</summary>
    private Border MakeSuggestionChip(string tag)
    {
        var border = new Border
        {
            Style = (Style)FindResource("TagBubbleBorder"),
            Child = new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center },
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            Opacity = 0.7,
        };
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _editInput!.Text = tag;
            CommitInlineEdit(continueEditing: true);
        };
        return border;
    }

    private void EditInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitInlineEdit(continueEditing: true); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelInlineEdit(); e.Handled = true; }
    }

    /// <summary>Commits whatever's in the edit box as a new tag (if non-empty/non-duplicate).
    /// <paramref name="continueEditing"/> keeps a fresh edit bubble open right after — used for
    /// Enter/suggestion-click so typing several tags in a row ("тег1" Enter "тег2" Enter …) never
    /// needs a re-click on "+ тег" — but only when a tag actually got added; an empty/duplicate
    /// commit always collapses back to "+". LostFocus (the user clicking elsewhere) always passes
    /// false — reopening a new box right under the cursor there would fight the user's intent.</summary>
    private void CommitInlineEdit(bool continueEditing)
    {
        if (_editInput is null) return;
        var text = _editInput.Text.Trim();
        _editInput = null;
        _suggestionsPanel = null;
        var added = text.Length > 0 && !_tags.Any(t => t.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (added) _tags.Add(text);
        _editing = continueEditing && added;
        Render();
        if (added) TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelInlineEdit()
    {
        _editInput = null;
        _suggestionsPanel = null;
        _editing = false;
        Render();
    }
}
