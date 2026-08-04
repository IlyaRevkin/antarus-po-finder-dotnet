using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Подсказки при наведении.
///
/// <b>Жалоба (третий раз подряд):</b> «подсказки при наведении обрезаются». Отчитывались, что
/// переносы включены, — а они не работали. Причина: строку WPF рисует TextBlock'ом из СВОЕГО
/// шаблона ContentPresenter, а внутри шаблона неявные стили ищутся в ресурсах самого шаблона и
/// приложения, но не в окружающем дереве. Стиль с TextWrapping лежал в ресурсах Border внутри
/// шаблона ToolTip — до сгенерированного TextBlock он не доезжал, и длинная подсказка оставалась
/// одной строкой, обрезанной по MaxWidth.
///
/// Проверяется настоящий стиль из Themes/Styles.xaml — тот самый, который уходит в сборку.</summary>
public class ToolTipWrappingTests
{
    /// <summary>Самая длинная подсказка проекта (300 знаков) — дословно из SettingsView.xaml,
    /// «Способ проверки пароля». На ней и проверяем: если переносится она, перенесётся любая.</summary>
    private const string LongestToolTip =
        "Домен — прямой запрос к контроллеру домена. Веб-проверка — запрос к веб-адресу компании, " +
        "работает там, где до домена не достучаться. Оба вместе — сначала домен, при недоступном " +
        "домене веб-проверка. Корпоративный вход (Keycloak / OpenID Connect) — вход через браузер, " +
        "пароль приложение не видит вовсе.";

    /// <summary>WPF живёт только в STA-потоке, а xUnit крутит тесты в MTA — поэтому окно замера
    /// поднимается на своём потоке. Исключение переносится наружу, иначе провал теста выглядел бы
    /// как молчаливый успех.</summary>
    private static T OnSta<T>(Func<T> body)
    {
        var result = default(T)!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new InvalidOperationException(failure.Message, failure);
        return result;
    }

    /// <summary>Словарь стилей грузится из САМОЙ СБОРКИ приложения — проверяем то, что уедет
    /// пользователю, а не копию разметки в тесте.
    ///
    /// Экземпляр Application при этом не создаётся сознательно: он глобален на весь процесс, и
    /// появившийся Application.Current увёл бы в маршалинг на мёртвый Dispatcher соседние тесты
    /// (BusyTracker специально рассчитан на Application.Current == null). Достаточно указать сборку
    /// ресурсов — Application.Current для этого не нужен.</summary>
    private static Style ToolTipStyle()
    {
        Application.ResourceAssembly ??= typeof(QrArt).Assembly;
        var dict = (ResourceDictionary)Application.LoadComponent(
            new Uri("/AntarusPoFinder.App;component/Themes/Styles.xaml", UriKind.Relative));
        return (Style)dict[typeof(ToolTip)];
    }

    private static Size MeasureTip(string text)
    {
        var tip = new ToolTip { Style = ToolTipStyle(), Content = text };
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tip.DesiredSize;
    }

    /// <summary>Длинная подсказка переносится по строкам и не выходит за свою предельную ширину.
    /// Одна строка на таком тексте означала бы, что перенос снова не доехал, а MaxWidth просто
    /// отрезал хвост, — ровно то, на что жаловались.</summary>
    [Fact]
    public void LongestToolTipInTheProject_WrapsIntoSeveralLines()
    {
        var (longSize, shortSize) = OnSta(() => (MeasureTip(LongestToolTip), MeasureTip("Ширина")));

        Assert.True(longSize.Width <= 420.5, $"подсказка шире предельных 420: {longSize.Width:0.#}");
        Assert.True(longSize.Height > shortSize.Height * 3,
            $"подсказка не перенеслась по строкам: высота {longSize.Height:0.#} против {shortSize.Height:0.#} у однострочной");
    }

    /// <summary>Короткой подсказке перенос не мешает: она остаётся одной строкой и по ширине
    /// растягивается ровно по тексту, а не на все 420.</summary>
    [Fact]
    public void ShortToolTip_StaysOnOneLine()
    {
        var size = OnSta(() => MeasureTip("Ширина"));

        Assert.True(size.Width < 200, $"короткая подсказка растянулась: {size.Width:0.#}");
        Assert.InRange(size.Height, 1, 60);
    }

    /// <summary>Перенос обязан быть общим для всего приложения, а не для отдельных подсказок:
    /// в стиле ToolTip должен стоять шаблон содержимого, иначе строковые подсказки снова окажутся
    /// в сгенерированном TextBlock без переносов.</summary>
    [Fact]
    public void ToolTipStyle_SetsContentTemplateGlobally()
    {
        var (hasSelector, maxWidth) = OnSta(() =>
        {
            var style = ToolTipStyle();
            var selector = false;
            double width = 0;
            foreach (var setter in style.Setters)
            {
                if (setter is not Setter s) continue;
                if (s.Property == ContentControl.ContentTemplateSelectorProperty) selector = s.Value is DataTemplateSelector;
                if (s.Property == FrameworkElement.MaxWidthProperty) width = (double)s.Value;
            }
            return (selector, width);
        });

        Assert.True(hasSelector, "у стиля подсказок нет шаблона содержимого — переносы снова не доедут");
        Assert.InRange(maxWidth, 200, 480);
    }
}
