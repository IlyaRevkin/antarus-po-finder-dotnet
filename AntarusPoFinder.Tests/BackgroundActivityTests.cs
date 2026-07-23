using System;
using AntarusPoFinder.App.ViewModels;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Покрывает BusyTracker — индикатор фоновой работы в статус-строке, появившийся по жалобе
/// «кажется, что приложение зависает, когда оно в фоне что-то делает». Проверять есть что: строка
/// внизу окна общая на всё приложение, а операций одновременно бывает несколько (автообновление
/// прошивок во время синхронизации конфига), и «залипший» индикатор после падения операции
/// выглядел бы ровно как та же зависшая программа, от которой он и должен спасать.
///
/// Application.Current в тестах null — BusyTracker это учитывает и в таком случае считает всё
/// на месте, без маршалинга на Dispatcher (см. его Refresh).</summary>
public class BackgroundActivityTests
{
    [Fact]
    public void Begin_ShowsTextAndIndeterminateBar()
    {
        var tracker = new BusyTracker();

        using var scope = tracker.Begin("Синхронизация прошивок с диском…");

        Assert.True(tracker.IsBusy);
        Assert.Equal("Синхронизация прошивок с диском…", tracker.Text);
        Assert.True(tracker.IsIndeterminate);
    }

    [Fact]
    public void Dispose_ClearsIndicator()
    {
        var tracker = new BusyTracker();
        var scope = tracker.Begin("Проверка диска…");

        scope.Dispose();

        Assert.False(tracker.IsBusy);
        Assert.Equal("", tracker.Text);
    }

    /// <summary>Двойной Dispose приходит сам собой: `using` внутри блока, который в свою очередь
    /// закрывается по исключению. Второй вызов не должен погасить чужую, ещё идущую операцию.</summary>
    [Fact]
    public void Dispose_Twice_DoesNotEndAnotherRunningOperation()
    {
        var tracker = new BusyTracker();
        var first = tracker.Begin("Первая");
        var second = tracker.Begin("Вторая");

        first.Dispose();
        first.Dispose();

        Assert.True(tracker.IsBusy);
        Assert.Equal("Вторая", tracker.Text);

        second.Dispose();
        Assert.False(tracker.IsBusy);
    }

    /// <summary>Несколько операций разом — показывается последняя начатая, остальные счётчиком, чтобы
    /// строка не прыгала и не превращалась в простыню.</summary>
    [Fact]
    public void SeveralAtOnce_ShowsLatestWithCounter()
    {
        var tracker = new BusyTracker();

        using var config = tracker.Begin("Отправка конфига на диск…");
        using var firmware = tracker.Begin("Обновление прошивок…");

        Assert.Equal("Обновление прошивок…  (+1)", tracker.Text);
    }

    [Fact]
    public void Report_WithKnownTotal_SwitchesToPercentage()
    {
        var tracker = new BusyTracker();
        using var scope = tracker.Begin("Обновление прошивок…");

        scope.Report(1, 4);

        Assert.False(tracker.IsIndeterminate);
        Assert.Equal(25, tracker.Progress);
    }

    [Fact]
    public void Report_WithoutTotal_ReturnsToIndeterminate()
    {
        var tracker = new BusyTracker();
        using var scope = tracker.Begin("Обновление прошивок…");
        scope.Report(1, 4);

        scope.Report(0, 0);

        Assert.True(tracker.IsIndeterminate);
    }

    /// <summary>Текст меняется по ходу дела (общее «Обновление прошивок…» → конкретная версия) —
    /// область при этом та же самая, новой строки в индикаторе не появляется.</summary>
    [Fact]
    public void ChangingText_UpdatesSameScope()
    {
        var tracker = new BusyTracker();
        using var scope = tracker.Begin("Обновление прошивок…");

        scope.Text = "Обновление прошивки: ТГР";

        Assert.Equal("Обновление прошивки: ТГР", tracker.Text);
        Assert.False(tracker.HasScope("Обновление прошивок…"));
    }
}
