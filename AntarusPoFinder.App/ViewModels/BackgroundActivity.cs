using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AntarusPoFinder.App.ViewModels;

/// <summary>Одна идущая прямо сейчас фоновая работа: пока объект не освобождён (using), внизу окна
/// рядом с индикатором диска видно, что программа занята и чем именно. Текст можно менять по ходу
/// дела, а Report — показать долю сделанного вместо «бегущей» полоски.</summary>
public interface IBusyScope : IDisposable
{
    string Text { get; set; }

    /// <summary>Известно, сколько всего шагов — полоска становится обычной, с процентами.
    /// total &lt;= 0 возвращает её в неопределённое состояние.</summary>
    void Report(int done, int total);
}

/// <summary>Общий индикатор фоновой работы в статус-строке. Появился по жалобе наладчика: пока
/// программа синхронизировалась с сетевым диском, окно просто не отвечало, и это было неотличимо
/// от «зависла». Теперь долгие операции, во-первых, ушли с потока интерфейса (см. HierarchyService,
/// блок про двухфазные операции), во-вторых — честно пишут снизу, что происходит.
///
/// Несколько операций одновременно (например, автообновление прошивок во время синхронизации
/// конфига) — нормальная ситуация: показывается последняя начатая, а остальные — счётчиком «+N»,
/// чтобы строка не прыгала и не превращалась в простыню.</summary>
public sealed partial class BusyTracker : ObservableObject
{
    private readonly List<Scope> _active = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private double _progress;

    public IBusyScope Begin(string text)
    {
        var scope = new Scope(this, text);
        _active.Add(scope);
        Refresh();
        return scope;
    }

    private void Remove(Scope scope)
    {
        _active.Remove(scope);
        Refresh();
    }

    private void Refresh()
    {
        // Свойства читает привязка WPF — обновлять их можно только с потока интерфейса. Сами
        // операции живут на нём же, но Dispose иногда прилетает из продолжения задачи, поэтому
        // маршалим на всякий случай, а не надеемся на вызывающего.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Refresh);
            return;
        }

        if (_active.Count == 0)
        {
            IsBusy = false;
            Text = "";
            IsIndeterminate = true;
            Progress = 0;
            return;
        }

        var current = _active[^1];
        var others = _active.Count - 1;
        Text = others > 0 ? $"{current.Text}  (+{others})" : current.Text;
        IsIndeterminate = current.Total <= 0;
        Progress = current.Total > 0 ? Math.Clamp(current.Done * 100.0 / current.Total, 0, 100) : 0;
        IsBusy = true;
    }

    /// <summary>Идёт ли прямо сейчас работа с этим текстом — нужно только для тестов/диагностики.</summary>
    public bool HasScope(string text) => _active.Any(s => s.Text == text);

    private sealed class Scope : IBusyScope
    {
        private readonly BusyTracker _owner;
        private string _text;
        private bool _disposed;

        public Scope(BusyTracker owner, string text)
        {
            _owner = owner;
            _text = text;
        }

        public string Text
        {
            get => _text;
            set { _text = value; _owner.Refresh(); }
        }

        public int Done { get; private set; }
        public int Total { get; private set; }

        public void Report(int done, int total)
        {
            Done = done;
            Total = total;
            _owner.Refresh();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Remove(this);
        }
    }
}
