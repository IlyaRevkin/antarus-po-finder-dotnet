namespace AntarusPoFinder.Core.Services;

/// <summary>Что делать по итогу очередного клика по номеру версии: ничего (ещё не набрали серию),
/// открыть фотографию или задать/сменить её.</summary>
public enum EasterEggAction
{
    None,
    Open,
    Set,
}

/// <summary>Счётчик «быстрых кликов подряд» по номеру версии — вся логика пасхалки, отделённая от
/// WPF, чтобы её можно было проверить тестами без окна. Правило: нужно ровно
/// <see cref="RequiredClicks"/> кликов, каждый не позже чем через <see cref="_window"/> после
/// предыдущего; пауза длиннее окна обнуляет серию и клик после паузы начинает новую с единицы.
/// Состояние Ctrl берётся у того клика, который замкнул серию (двенадцатого): с Ctrl — «задать»,
/// без — «открыть». После срабатывания счётчик сбрасывается, так что следующие двенадцать сработают
/// снова.</summary>
public sealed class EasterEggClickCounter
{
    public const int RequiredClicks = 12;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(1500);

    private readonly int _required;
    private readonly TimeSpan _window;
    private int _count;
    private DateTime _lastClick;
    private bool _hasLast;

    public EasterEggClickCounter(int required = RequiredClicks, TimeSpan? window = null)
    {
        _required = required;
        _window = window ?? DefaultWindow;
    }

    /// <summary>Сколько кликов уже засчитано в текущей серии — только для тестов/диагностики.</summary>
    public int Count => _count;

    /// <summary>Засчитать клик, случившийся в момент <paramref name="now"/> при состоянии Ctrl
    /// <paramref name="ctrlDown"/>. Возвращает действие, если серия только что замкнулась.</summary>
    public EasterEggAction Click(DateTime now, bool ctrlDown)
    {
        if (_hasLast && now - _lastClick > _window)
            _count = 0;

        _hasLast = true;
        _lastClick = now;
        _count++;

        if (_count < _required)
            return EasterEggAction.None;

        _count = 0;
        return ctrlDown ? EasterEggAction.Set : EasterEggAction.Open;
    }
}
