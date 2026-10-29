using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>«НЕКОТОРЫЕ ОКНА МЫЛЬНЫЕ» — чтобы не вернулось.
///
/// Жалоба повторялась дважды, и причин у неё оказалось две, обе измеренные, а не угаданные.
///
/// <b>Первая — режим DPI процесса.</b> Манифеста у приложения не было вовсе; замер на машине с
/// экраном 125 % давал GetProcessDpiAwareness = 1, то есть PROCESS_SYSTEM_DPI_AWARE. Такое окно
/// знает ровно один масштаб — тот, что был при входе в систему, — и на экране с другим масштабом
/// Windows просто РАСТЯГИВАЕТ уже нарисованную картинку. Отсюда и «некоторые»: мылится то окно,
/// которое оказалось на «неродном» экране. Лечится только объявлением PerMonitorV2 в манифесте;
/// проверяем, что объявление на месте и что сборка про манифест знает.
///
/// <b>Вторая — вёрстка мимо пиксельной сетки.</b> WPF по умолчанию не округляет посчитанные
/// координаты до целых точек экрана. На 100 % это незаметно, на 125 % — замер показал, что из 16
/// надписей на дробной точке оказались ВСЕ 16 (например 49.113 вместо 49), и буквы размазываются
/// между соседними пикселями. Лечится UseLayoutRounding; свойство наследуется, поэтому достаточно
/// задать его окну — что и делает App.ApplyPixelSnapping.
///
/// Диалоги приложения в тестах не строятся (почему — в конце ViewsRenderTests), поэтому здесь
/// проверяется сам крючок на обычном окне: окно, которому он поставил округление, раздаёт его
/// всему содержимому.</summary>
public class DpiAndPixelSnappingTests
{
    // ── Манифест ──────────────────────────────────────────────────────────────

    private static string AppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "AntarusPoFinder.App");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("не нашёл папку AntarusPoFinder.App от " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Манифест_объявляет_per_monitor_v2()
    {
        var manifest = File.ReadAllText(Path.Combine(AppFolder(), "app.manifest"));

        // Главное: PerMonitorV2. Без него процесс снова станет system-aware со всеми
        // растянутыми окнами на втором экране.
        Assert.Contains("<dpiAwareness", manifest);
        Assert.Contains("PerMonitorV2", manifest);

        // Для Windows 8.1 и старше атрибута dpiAwareness ещё нет — там работает только dpiAware,
        // и без него программа на тех системах стала бы вовсе dpi-unaware, то есть хуже, чем была.
        Assert.Contains("<dpiAware", manifest);

        // Без списка supportedOS Windows считает программу написанной до Vista и включает ей свою
        // совместимость — DPI-настройки манифеста при этом игнорируются.
        Assert.Contains("supportedOS", manifest);
    }

    [Fact]
    public void Сборка_подключает_манифест()
    {
        var csproj = File.ReadAllText(Path.Combine(AppFolder(), "AntarusPoFinder.App.csproj"));

        // Файл рядом с проектом сам по себе ничего не значит: без ApplicationManifest сборка
        // возьмёт стандартный манифест apphost, в котором про DPI не сказано ни слова.
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", csproj);
    }

    // ── Округление вёрстки ────────────────────────────────────────────────────

    /// <summary>Крючок ставит округление окну, и оно достаётся содержимому.
    ///
    /// Проверяется именно наследование: ровно на этом развалились два более простых способа —
    /// OverrideMetadata(typeof(FrameworkElement)) запрещён (свойство объявлено этим же типом), а
    /// OverrideMetadata(typeof(Window)) меняет только значение по умолчанию, которое по дереву не
    /// наследуется: у окна True, у содержимого по-прежнему False.</summary>
    [Fact]
    public void Округление_вёрстки_достаётся_всему_содержимому_окна()
    {
        bool windowRounded = false, borderRounded = false, textRounded = false;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                AntarusPoFinder.App.App.ApplyPixelSnapping();

                var text = new TextBlock { Text = "Мыло" };
                var border = new Border { Child = text };
                var window = new Window { Content = border, Width = 200, Height = 200 };

                // Окно не показываем: Loaded — обычное маршрутизируемое событие, и классовый
                // обработчик, ради которого всё затевалось, срабатывает и на ручной подаче. Живое
                // окно посреди прогона тестов мигало бы на экране и требовало бы живого рабочего
                // стола на машине сборки.
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));

                windowRounded = window.UseLayoutRounding;
                borderRounded = border.UseLayoutRounding;
                textRounded = text.UseLayoutRounding;
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "окно так и не построилось за 30 секунд");

        if (failure is not null) throw new InvalidOperationException(failure.Message, failure);

        Assert.True(windowRounded, "окну не поставили UseLayoutRounding");
        Assert.True(borderRounded, "округление не дошло до рамки внутри окна");
        Assert.True(textRounded, "округление не дошло до надписи — а мылится именно она");
    }
}
