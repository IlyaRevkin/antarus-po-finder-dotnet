using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AntarusPoFinder.App.ViewModels;

namespace AntarusPoFinder.App.Services;

/// <summary>ЖИВОЙ ПРОГОН, КОТОРЫЙ ПРИЛОЖЕНИЕ ДЕЛАЕТ САМО.
///
/// Правило проекта — перед релизом посмотреть окна глазами: сборка и тесты не ловят ни контраст, ни
/// наезжающий текст, ни вёрстку в узком окне. Проблема в том, что снять окно снаружи получается не
/// всегда: на машине без активного рабочего стола (отключённая сессия, RDP в фоне) и свежеоткрытое
/// окно, и PrintWindow дают белый прямоугольник — у окна просто нет кадра в кэше DWM. На этом
/// прогон срывался уже дважды.
///
/// Здесь другой путь: приложение рисует само себя в картинку изнутри, через RenderTargetBitmap. Это
/// настоящее визуальное дерево с настоящими стилями и настоящей темой — не «проверка, что контрол
/// создаётся», как в тестах рендера, а то самое окно, которое увидит человек. Рабочий стол при этом
/// не нужен вовсе.
///
/// ⚠️ Включается ТОЛЬКО переменной окружения <c>ANTARUS_UI_SNAPSHOT_DIR</c> — тем же способом, что
/// и остальные испытательные швы этого проекта (ANTARUS_TEST_APPDATA, ANTARUS_TEST_INSTANCE_ID).
/// Без неё класс не делает ничего и в обычном запуске не участвует.</summary>
public static class UiSnapshotService
{
    public static string? Directory => Environment.GetEnvironmentVariable("ANTARUS_UI_SNAPSHOT_DIR");

    public static bool Enabled => !string.IsNullOrWhiteSpace(Directory);

    /// <summary>Строка в журнал прогона. Публичная, потому что отметки нужны и на пути запуска
    /// приложения: первые попытки прогона повисали ДО главного окна, и без следа было непонятно, на
    /// чём именно — окно входа, подключение диска или что-то ещё.</summary>
    public static void Note(string line)
    {
        if (!Enabled) return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory!);
            File.AppendAllText(Path.Combine(Directory!, "_log.txt"),
                $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { /* журнал прогона не должен мешать самому прогону */ }
    }

    /// <summary>Темы, которые надо обойти. По умолчанию — все доступные: смысл прогона как раз в
    /// том, чтобы увидеть каждую, а не ту, что была включена последней.</summary>
    private static IEnumerable<AppTheme> Themes()
    {
        var wanted = Environment.GetEnvironmentVariable("ANTARUS_UI_SNAPSHOT_THEMES");
        if (string.IsNullOrWhiteSpace(wanted)) return ThemeCatalog.Available;

        var ids = wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ids.Select(ThemeCatalog.Resolve).Distinct();
    }

    /// <summary>Снять все страницы во всех темах и закрыть приложение.
    ///
    /// Только навигация и снимок: никаких нажатий. Прогон идёт на изолированном профиле, но страницы
    /// ходят на общий диск и в чужие данные — трогать там что-либо ради картинки нельзя.</summary>
    public static async void RunAndExit(Window window, MainWindowViewModel vm, AppServices services)
    {
        // Прогон идёт под АДМИНИСТРАТОРОМ: у остальных ролей половина страниц и вкладок скрыта, и
        // проверить глазами то, чего не видно, нельзя. Профиль изолированный, на чужие права это
        // не влияет никак.
        if (!string.Equals(vm.CurrentRole, "administrator", StringComparison.Ordinal))
            vm.SwitchRole("administrator");

        var dir = Directory!;
        System.IO.Directory.CreateDirectory(dir);

        // Журнал пишется ПО ХОДУ, а не в конце: если прогон где-то повиснет (страница лезет на
        // недоступный сетевой диск и ждёт таймаута), итоговый файл не появился бы вовсе, и было бы
        // неизвестно, докуда дошли. Первая же находка такого прогона — именно «встало вот здесь».
        Note($"обход начат, окно {window.ActualWidth:F0}x{window.ActualHeight:F0}, роль {vm.CurrentRole}");

        var pages = RolesConfig.NavItems.Select(n => n.PageId).Append("settings").Distinct().ToList();

        foreach (var theme in Themes())
        {
            // Тему ставим через настройку, а не только применением: экраны настроек показывают
            // ВЫБРАННУЮ тему, и снимок, где применена одна, а в списке значится другая, толкал бы
            // к ложному выводу «список не работает».
            services.Cfg.SetTheme(theme.Id);
            ThemeManager.Apply(theme.Id, ThemeManager.CurrentAccent);
            await Settle(window);

            foreach (var page in pages)
            {
                try
                {
                    vm.NavigateCommand.Execute(page);
                    await Settle(window);
                    Save(window, Path.Combine(dir, $"{theme.Id}--{page}.png"));
                    Note($"{theme.Id}/{page}: ok");
                }
                catch (Exception ex)
                {
                    // Страница, которая не открылась, — это и есть находка прогона. Записываем и
                    // идём дальше: один сломанный экран не должен прятать состояние остальных.
                    Note($"{theme.Id}/{page}: ОШИБКА {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ВКЛАДКИ НАСТРОЕК. Страница «Настройки» снимается на той вкладке, что открыта по умолчанию,
        // — то есть «Общие». Всё остальное (ряд кнопок у «Прошивок», таблица модерации) до этого
        // глазами перед релизом не проверялось ни разу, а именно там и ломалась вёрстка: кнопка
        // «Теги…» наезжала на «Обновить», нижний ряд кнопок уезжал за край окна.
        await SnapshotSettingsTabs(window, vm, dir, Note);

        // КАРТОЧКА ПРОШИВКИ отдельно: на пустом или чужом профиле выдача может не дать ни одной
        // карточки, а разделённая кнопка «Загрузить в ПЛК» видна только на ней.
        await SnapshotFirmwareCard(dir, Note);

        // ОКНА, А НЕ ТОЛЬКО СТРАНИЦЫ. Окно загрузки в ПЛК — главный экран, на который смотрят
        // минутами, и именно на нём стилизация оборудования должна быть видна. В обход страниц оно
        // не попадает вовсе: это отдельное окно, открываемое по кнопке на карточке.
        await SnapshotLoaderDialogs(dir, Note);

        Note("обход закончен");
        Application.Current.Shutdown();
    }

    /// <summary>Дать разметке пересчитаться и отрисоваться.
    ///
    /// Именно ожидание, а не синхронная «прокачка очереди»: прогон запускается из обработчика
    /// отрисовки, то есть УЖЕ в потоке интерфейса, и синхронный Invoke с фоновым приоритетом там
    /// встаёт намертво — первый же запуск повис на три минуты и не снял ни одного кадра.</summary>
    private static async System.Threading.Tasks.Task Settle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        // 420 мс, а не 120: в бете страница не просто проявляется (160 мс), а собирается каскадом
        // карточек (MainWindow.StaggerCards — до ~320 мс на пятой). На прежней паузе снимок ловил
        // страницу В СЕРЕДИНЕ движения: полупрозрачной и со съехавшими карточками. Такой кадр
        // читается как «бледная кривая вёрстка» и толкает чинить то, что не сломано.
        await System.Threading.Tasks.Task.Delay(420);
        window.UpdateLayout();
    }

    private static void Save(Window window, string path)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) return;

        // 96 dpi — снимок в логических пикселях, то есть ровно в той сетке, в которой свёрстано
        // окно. Экран этой машины 125 %, и снимать в его dpi значило бы каждый раз делить на 1.25
        // при сравнении.
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Вкладки «Настроек», которые иначе в обход не попадают (см. вызов выше).</summary>
    private static async System.Threading.Tasks.Task SnapshotSettingsTabs(
        Window window, MainWindowViewModel vm, string dir, Action<string> note)
    {
        try
        {
            vm.NavigateCommand.Execute("settings");
            await Settle(window);
            var settings = FindChild<Views.SettingsView>(window);
            if (settings is null)
            {
                note("вкладки настроек: страница не найдена в дереве");
                return;
            }

            // «tickets» — вкладка, а не страница бокового меню (бета), поэтому в общий обход страниц
            // она не попадает: снимать её надо здесь, иначе единственный экран, где живут тикеты,
            // перед релизом не видит никто.
            foreach (var tab in new[] { "firmware", "moderation", "hierarchy", "loader", "tickets" })
            {
                settings.ShowTabForSnapshot(tab);
                await Settle(window);
                Save(window, Path.Combine(dir, $"settings--{tab}.png"));
                note($"настройки/{tab}: ok");
            }
        }
        catch (Exception ex)
        {
            note($"вкладки настроек: ОШИБКА {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Карточка прошивки сама по себе: ряд кнопок с разделённой «Загрузить в ПЛК».
    /// Собирается из придуманной записи — на диск и в базу не ходит.</summary>
    private static async System.Threading.Tasks.Task SnapshotFirmwareCard(string dir, Action<string> note)
    {
        try
        {
            var card = new Views.FirmwareCard();
            card.Configure(new Core.Services.HierarchyResult
            {
                Name = "НГР-КПЧ SMH4",
                Controller = "SMH4",
                EquipmentType = "НГР",
                SubtypeName = "КПЧ",
                VersionRaw = "2.1.0004.0002",
                Description = "Две скважины, частотник, датчик давления",
                Tags = "частотник;скважина",
            }, new Views.FirmwareCardFlags
            {
                HasLfs = true,
                HasPsl = true,
                SupportsLoader = true,
                LoaderConnected = true,
                HasHmi = true,
                ConnectionMode = "ethernet",
                ConnectionHint = "адрес ПЛК: 192.168.1.10",
            });

            var host = new Window
            {
                Width = 720,
                Height = 420,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Content = card,
            };
            host.SetResourceReference(Window.BackgroundProperty, "BgBrush");
            host.Show();
            await System.Threading.Tasks.Task.Delay(400);
            host.UpdateLayout();
            Save(host, Path.Combine(dir, "card--firmware.png"));
            host.Close();
            note("карточка прошивки: ok");
        }
        catch (Exception ex)
        {
            note($"карточка прошивки: ОШИБКА {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Первый потомок нужного типа в визуальном дереве. Нужен ровно затем, чтобы дотянуться
    /// до страницы настроек и переключить её вкладку.</summary>
    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is T deeper) return deeper;
        }
        return null;
    }

    /// <summary>Снимки окна загрузки для разных объектов: насосная, канализационная, тепловой пункт.
    ///
    /// Нужны потому, что стилизацию полосы иначе не проверить ничем: страницы её не показывают, а
    /// глазами на живой заливке её видит только наладчик у шкафа. Окно открывается с заданием,
    /// которое НИЧЕГО не грузит (операция сборки, несуществующий файл) — нам нужен только его вид.</summary>
    private static async System.Threading.Tasks.Task SnapshotLoaderDialogs(string dir, Action<string> note)
    {
        var cases = new (string Name, string Equipment, string Controller)[]
        {
            ("pump", "НГР-КПЧ SMH4", "SMH4"),
            ("sewage", "НГР-КНС SMH4", "SMH4"),
            ("heat", "ТГР SMH4", "SMH4"),
            ("fire", "ПЖ-ПИ SMH4", "SMH4"),
            ("valve", "ШУЗ задвижка", "SMH4"),
            ("plain", "ВЗУ непонятное", "SMH4"),
        };

        foreach (var (name, equipment, controller) in cases)
        {
            try
            {
                var window = Views.LoaderDialog.CreateForSnapshot(new Core.Loader.LoaderJob
                {
                    Operation = Core.Loader.LoaderOperation.Build,
                    VersionName = equipment + " 2.0.0004.0007",
                    EquipmentName = equipment,
                    ControllerName = controller,
                    SourcePath = "",
                }, percent: 58);
                window.Show();
                await System.Threading.Tasks.Task.Delay(400);
                window.UpdateLayout();
                Save(window, Path.Combine(dir, $"loader--{name}.png"));
                window.Close();
                note($"окно загрузки/{name}: ok");
            }
            catch (Exception ex)
            {
                note($"окно загрузки/{name}: ОШИБКА {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
