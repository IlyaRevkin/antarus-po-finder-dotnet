using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AntarusPoFinder.App;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.App.Views;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Каждая страница и каждое окно РЕАЛЬНО строятся и рисуются.
///
/// Зачем это тестом, а не живым прогоном. Страницы — это XAML, и он разбирается в момент выполнения:
/// опечатка в имени обработчика, ссылка на несуществующий ресурс или стиль, забытая кисть — всё это
/// роняет окно при разборе разметки, а компилятор молчит. До сих пор такое ловилось только тем, что
/// кто-то руками открыл нужную страницу; страницу, до которой в тот раз не дошли, узнавал уже
/// пользователь. Ровно так уже приезжали и XAML-гонка инициализации, и pack-URI иконки диалога.
///
/// Живой прогон это не отменяет — контраст и вёрстку он показывает лучше, — но «просто открывается»
/// больше не должно зависеть от того, вспомнил ли человек про эту страницу.
///
/// <b>Как это работает.</b> WPF требует STA-поток и живой <see cref="Application"/> с загруженными
/// словарями ресурсов: без них любой <c>{DynamicResource}</c> отдаёт null, а <c>{StaticResource}</c>
/// бросает. Поэтому весь класс гоняется в одном выделенном STA-потоке (см. <see cref="Ui"/>), в
/// котором приложение поднимается один раз.</summary>
[Collection("wpf")]
public class ViewsRenderTests
{
    // ── STA-поток с поднятым приложением ──────────────────────────────────────

    /// <summary>Один STA-поток на весь класс. xUnit гоняет тесты на пуле, а WPF так не умеет — любое
    /// обращение к визуалу из чужого потока падает. Поток создаётся при первом обращении и живёт до
    /// конца процесса: перезапускать Application нельзя, второй экземпляр в том же процессе
    /// невозможен.</summary>
    private static class Ui
    {
        private static readonly object Gate = new();
        private static Dispatcher? _dispatcher;

        public static void Run(Action action)
        {
            Ensure();
            Exception? error = null;
            _dispatcher!.Invoke(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            if (error is not null) throw new InvalidOperationException(error.Message, error);
        }

        private static void Ensure()
        {
            lock (Gate)
            {
                if (_dispatcher is not null) return;

                var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;

                    // Application нужен: {StaticResource} резолвится в момент РАЗБОРА разметки, то
                    // есть внутри InitializeComponent, — навесить словари на готовый элемент уже
                    // поздно. Живой Application.Current при этом остаётся на весь процесс, и код,
                    // который маршалит работу через Application.Current.Dispatcher, начал бы
                    // откладывать её на ЭТОТ поток: первая версия этого файла так и уронила
                    // BackgroundActivityTests (по отдельности проходили, вместе — нет). Лечится это
                    // на стороне продукта — класс, которому нужен поток интерфейса, должен помнить
                    // СВОЙ поток, а не спрашивать глобальный Application (см. BusyTracker).
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    foreach (var path in new[] { "Themes/Dark.xaml", "Themes/Icons.xaml", "Themes/Styles.xaml" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri($"pack://application:,,,/AntarusPoFinder.App;component/{path}", UriKind.Absolute),
                        });

                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>Прогнать элемент через полный цикл разметки и отрисовки. Просто создать объект мало:
    /// часть ошибок (недостающий ресурс в шаблоне, привязка к несуществующему элементу) вылезает
    /// только когда WPF реально считает размеры и нарисует.</summary>
    private static void Render(FrameworkElement element)
    {
        element.Measure(new Size(1400, 900));
        element.Arrange(new Rect(0, 0, 1400, 900));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
    }

    private static AppServices Services() => new();

    // ── Страницы ──────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Pages() => new[]
    {
        new object[] { "search" },
        new object[] { "inspection" },
        new object[] { "upload" },
        new object[] { "params" },
        new object[] { "newversions" },
        new object[] { "settings" },
        new object[] { "network" },
        new object[] { "hosting" },
        new object[] { "tickets" },
        // Отдельная страница багов прошивок — пункт секции «ДЛЯ ПРОГРАММИСТА» (бета 2.0). Она
        // рисуется тем же TicketsView, но в другом режиме: пилюли сверху скрыты, вкладка
        // зафиксирована. Режим меняет видимость контролов, поэтому и проверяется отдельно.
        new object[] { "fwbugs" },
        // «Чистка диска» пунктом меню больше не является (переехала во вкладку Настроек), но
        // остаётся отдельным контролом — и строиться обязана так же: разбор её разметки от переезда
        // не изменился, а вкладка создаёт её тем же конструктором.
        new object[] { "cleanup" },
    };

    /// <summary>Каждая страница из бокового меню строится и рисуется. Список берётся не руками, а из
    /// RolesConfig (см. отдельный тест ниже): забыть добавить сюда новую страницу нельзя.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryPage_BuildsAndRenders(string pageId)
    {
        Ui.Run(() =>
        {
            var services = Services();
            var host = new MainWindowViewModel(services);
            UserControl page = pageId switch
            {
                "search" => new SearchView(services, host),
                "inspection" => new InspectionView(services, host),
                "upload" => new UploadView(services, host),
                "params" => new ParamsView(services, host),
                "newversions" => new NewVersionsView(services, host),
                "settings" => new SettingsView(services, host),
                "network" => new NetworkSyncView(services, host),
                "hosting" => new HostingView(services, host),
                "tickets" => new TicketsView(services, host),
                "fwbugs" => new TicketsView(services, host, fwBugsOnly: true),
                "cleanup" => new DiskCleanupView(services, host),
                _ => throw new ArgumentOutOfRangeException(nameof(pageId), pageId, "неизвестная страница"),
            };
            Render(page);
        });
    }

    /// <summary>«Чистка диска» переехала из бокового меню во вкладку Настроек, но администраторской
    /// быть не перестала: раньше это была единственная страница, выданная в RoleAccess только
    /// администратору, теперь — видимость кнопки вкладки. Проверяется на настоящем контроле, потому
    /// что правило живёт в ApplyRoleVisibility: «переехала, но потеряла ограничение» не заметил бы
    /// никто, а вкладка удаляет файлы на общем диске.</summary>
    [Theory]
    [InlineData("administrator", true)]
    [InlineData("programmer", false)]
    [InlineData("naladchik", false)]
    public void DiskCleanupTab_IsAdministratorOnly(string role, bool visible)
    {
        Ui.Run(() =>
        {
            var services = Services();
            var before = services.Cfg.CurrentRole();
            try
            {
                services.Cfg.SetRole(role);
                var view = new SettingsView(services, new MainWindowViewModel(services));
                view.ApplyRoleVisibility();
                Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, view.TabBtnCleanup.Visibility);
            }
            finally
            {
                // База у тестового процесса одна на все классы — роль возвращаем, иначе соседний тест
                // получил бы приложение в чужой роли.
                services.Cfg.SetRole(before);
            }
        });
    }

    /// <summary>ТЕКСТ В ПОЛЕ ВВОДА ВИДЕН И НЕ УЕЗЖАЕТ.
    ///
    /// Поля ввода в этой программе почти везде с заданной высотой (32–38) — так они выстраиваются в
    /// ряд с кнопками и выпадающими списками. Когда у полей появился собственный шаблон (ради
    /// скругления из темы), отступ в нём задали полем ScrollViewer'а и выравниванием по
    /// VerticalContentAlignment — и текст уехал: в окне паспорта пропал из виду совсем, в поиске
    /// съехал к нижнему краю. Заметить это снимком страницы нельзя: пустое поле выглядит одинаково
    /// и в исправном, и в сломанном виде.
    ///
    /// Поэтому проверяется НЕ картинка, а положение: где у заполненного поля реально оказалась
    /// область текста. Она обязана целиком помещаться внутри рамки и стоять по центру — именно это
    /// и ломалось.</summary>
    [Theory]
    [InlineData(30.0)]
    [InlineData(32.0)]
    [InlineData(34.0)]
    [InlineData(38.0)]
    public void Текст_в_поле_ввода_виден_целиком(double height)
    {
        Ui.Run(() =>
        {
            var box = new System.Windows.Controls.TextBox
            {
                Height = height,
                Width = 220,
                FontSize = 13,                       // кегль программы: от него зависит, влезет ли строка
                Text = "НГР-КНС SMH5",
            };

            // Тема — светлая и явно: цвета берём те же, что у человека на экране, иначе проверка
            // «есть ли тёмные точки» зависела бы от того, какую тему оставил предыдущий тест.
            AntarusPoFinder.App.ThemeManager.Apply("light", AntarusPoFinder.App.ThemeManager.CurrentAccent);

            // ⚠️ ИМЕННО StackPanel, а не рамка с точной высотой. Страницы программы собраны из
            // StackPanel и строк Auto, а они меряют содержимое БЕСКОНЕЧНОЙ высотой — и беда
            // проявляется только так: при точном измерении (Measure ровно на высоту поля) текст
            // рисуется правильно даже со сломанным шаблоном. Два предыдущих захода теста прошли
            // мимо ошибки ровно по этой причине.
            var host = new System.Windows.Controls.StackPanel
            {
                Width = 240,
                Background = System.Windows.Media.Brushes.White,
            };
            host.Children.Add(box);
            host.Measure(new Size(240, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 240, host.DesiredSize.Height));
            host.UpdateLayout();

            // ⚠️ ПРОВЕРЯЕМ ПИКСЕЛИ, а не раскладку. Разбор 21.10.2026 показал, почему: при нехватке
            // высоты раскладка остаётся «правильной» — строке выделяется вся доступная высота, и
            // любое сравнение «выделено ≥ нужно» проходит. А текст при этом обрезан, и видно это
            // только на картинке. Две проверки подряд (по границам и по желаемой высоте) эту беду
            // пропустили, картинка — поймала.
            // Рисуем ВЕСЬ контейнер, а не одно поле: обрезка приходит от раскладки родителя, и
            // снимок отдельного поля её не покажет (на это уже попались — см. комментарий выше).
            var panelHeight = (int)Math.Ceiling(host.DesiredSize.Height);
            var bitmap = new RenderTargetBitmap(240, panelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);

            var box_top = (int)Math.Round(box.TransformToAncestor(host).Transform(new Point(0, 0)).Y);
            var pixelHeight = (int)Math.Ceiling(height);

            var stride = 240 * 4;
            var pixels = new byte[stride * panelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            // Строки картинки В ПРЕДЕЛАХ ПОЛЯ, где есть заметно тёмные точки, — это нарисованный текст.
            var first = -1;
            var last = -1;
            for (var y = box_top; y < Math.Min(panelHeight, box_top + pixelHeight); y++)
            {
                var dark = 0;
                for (var x = 0; x < 240; x++)
                {
                    var i = y * stride + x * 4;
                    if (pixels[i] < 120 && pixels[i + 1] < 120 && pixels[i + 2] < 120) dark++;
                }

                if (dark < 3) continue;              // одиночные точки — это рамка и сглаживание
                if (first < 0) first = y - box_top;
                last = y - box_top;
            }

            Assert.True(first >= 0, $"в поле высотой {height} текста не видно вовсе");

            var drawn = last - first + 1;
            Assert.True(drawn >= 7,
                $"в поле высотой {height} от строки осталась полоска в {drawn} пикселей — текст обрезан");

            var centre = (first + last) / 2.0;
            Assert.True(Math.Abs(centre - pixelHeight / 2.0) <= 2.5,
                $"текст не по центру поля {height}: середина строки на {centre:F1} при центре {pixelHeight / 2.0:F1}");
        });
    }

    /// <summary>Область текста внутри поля — визуальный потомок с именем TextBoxView; своего
    /// публичного типа у неё нет, поэтому ищем по имени типа.</summary>
    private static FrameworkElement? FindTextView(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child.GetType().Name == "TextBoxView" && child is FrameworkElement element) return element;
            if (FindTextView(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>БОКОВОЙ СПИСОК НАСТРОЕК ЗНАЕТ ПРО ВСЕ РАЗДЕЛЫ.
    ///
    /// В бете разделы настроек стоят столбиком слева, и этот столбик собирается по РУЧНОМУ списку
    /// (SettingsView.SidebarLayout) — только так разделы удаётся разбить подзаголовками по смыслу.
    /// Цена ручного списка известна: вкладка, заведённая в разметке и забытая в нём, в бете не
    /// нарисуется вовсе. Снаружи это выглядит как пропавшая настройка, а не как опечатка в коде.</summary>
    [Fact]
    public void Боковой_список_настроек_не_теряет_разделов()
    {
        Ui.Run(() =>
        {
            var services = Services();
            var view = new SettingsView(services, new MainWindowViewModel(services));
            Assert.Empty(view.TabsMissingFromSidebar());
        });
    }

    /// <summary>Отметить подтип «инструкции не будет» человеку есть чем. Признак поддержан всюду —
    /// база, обмен конфигом, раскладка диска, ссылка под QR, — но ставится он ровно в одном месте:
    /// галочкой в таблице подтипов. Полгода этот признак пролежал без неё и был мёртв целиком, снаружи
    /// неотличимо от «функции нет». Поэтому проверяем не поведение, а наличие единственной двери.</summary>
    /// <summary>Окно «Баг в прошивке» строится и рисуется.
    ///
    /// Окно, а не страница, и это важно: остальные диалоги сюда не попадают из-за относительного
    /// pack-URI в Icon (разбор такого адреса вне запущенного приложения падает). У этого окна адрес
    /// иконки абсолютный — именно поэтому его и можно проверить здесь, а не только глазами.
    ///
    /// Живым прогоном это окно достаётся тяжело: до жучка на карточке надо сначала иметь в базе
    /// прошивку и доступный сетевой диск, а в чистом профиле нет ни того, ни другого.</summary>
    /// <summary>Карточка прошивки строится и рисуется со всеми пометками.
    ///
    /// Страница поиска в проверке выше рисуется ПУСТОЙ — без найденных прошивок сама карточка не
    /// создаётся ни разу, и ошибка в её разметке до сих пор вылезала только у того, кто что-то нашёл.
    /// Живым прогоном туда тоже не добраться: нужна прошивка в базе и доступный сетевой диск, а в
    /// чистом профиле нет ни того, ни другого.</summary>
    [Fact]
    public void FirmwareCard_BuildsAndRenders()
    {
        Ui.Run(() =>
        {
            var card = new FirmwareCard();
            card.Configure(
                new HierarchyResult
                {
                    Name = "НГР 2.0 КПЧ",
                    VersionRaw = "3.1.0004.0002.20260101_0000",
                    Execution = "3 и более насосов",
                    ConfigName = "2 насоса",
                    Controller = "SMH5",
                    Tags = "пожарка, насосная",
                },
                new FirmwareCardFlags());
            Render(card);

            // Жучок — ЗНАЧОК у номера версии, а не кнопка в ряду действий внизу карточки.
            // Просьба Ильи: «а то он вычурно как кнопка выглядит».
            var bug = card.FindName("BugIcon") as System.Windows.FrameworkElement;
            Assert.NotNull(bug);
            Assert.IsNotType<Button>(bug);

            // Обычная прошивка значком «ОПЦ» не помечена — иначе пометка ничего не значит,
            // и строки с номерами шкафа у неё тоже нет.
            var badge = card.FindName("OpcBadge") as System.Windows.FrameworkElement;
            Assert.NotNull(badge);
            Assert.Equal(Visibility.Collapsed, badge!.Visibility);

            var numbers = card.FindName("OpcNumbersLabel") as System.Windows.FrameworkElement;
            Assert.NotNull(numbers);
            Assert.Equal(Visibility.Collapsed, numbers!.Visibility);

            // У обычной прошивки номер версии остаётся голым — метки там не из чего строить.
            var version = card.FindName("VersionLabel") as TextBlock;
            Assert.NotNull(version);
            Assert.Equal("3.1.0004.0002.20260101_0000", version!.Text);
        });
    }

    /// <summary>Разовая прошивка (ОПЦ) помечена значком в ШАПКЕ карточки.
    ///
    /// Жалоба: «в поисковой выдаче мне не отличить обычную от опц». Строку под именем дочитывают не
    /// всегда, а спутать разовую сборку под конкретный шкаф с прошивкой линейки — это выехать на
    /// объект с чужой прошивкой.</summary>
    [Fact]
    public void FirmwareCard_ОпцПомеченаЗначкомИНомерами()
    {
        Ui.Run(() =>
        {
            var card = new FirmwareCard();
            card.Configure(
                new HierarchyResult
                {
                    Name = "НГР 2.0 КПЧ",
                    VersionRaw = "3.1.0004.0002.20260101_0000",
                    Controller = "SMH5",
                    IsOpc = true,
                    RequestNum = "01312",
                    CabinetSn = "SN-778899",
                },
                new FirmwareCardFlags());
            Render(card);

            var badge = card.FindName("OpcBadge") as System.Windows.FrameworkElement;
            Assert.NotNull(badge);
            Assert.Equal(Visibility.Visible, badge!.Visibility);

            // Значок стоит ПЕРЕД названием, а не у номера версии в правом углу: «опц давай писать
            // в начале карточки, а не в конце у версии, а то не бросается в глаза».
            var name = card.FindName("NameLabel") as TextBlock;
            Assert.NotNull(name);
            Assert.True(Grid.GetColumn(badge) < Grid.GetColumn(name!),
                "значок ОПЦ должен стоять левее названия");

            // Номера — своей строкой и акцентом, а не в общем приглушённом ряду («более явно
            // номер заявки или сн»).
            var numbers = card.FindName("OpcNumbersLabel") as TextBlock;
            Assert.NotNull(numbers);
            Assert.Equal(Visibility.Visible, numbers!.Visibility);
            Assert.Contains("Заявка 01312", numbers.Text);
            Assert.Contains("SN SN-778899", numbers.Text);

            // САМ НОМЕР ВЕРСИИ показывается с метками — и в шапке, и в поле у кнопки «Копировать».
            // «Условно 2.1.0004.0002 отображается, а должно быть 2.1.0004.0002_(47137)». Показывать
            // одно, а копировать другое — прямой обман: вставят не то, что видели.
            var version = card.FindName("VersionLabel") as TextBlock;
            Assert.NotNull(version);
            Assert.Contains("_(01312)", version!.Text);

            var copyField = card.FindName("SoftwareNameLabel") as TextBlock;
            Assert.NotNull(copyField);
            Assert.Contains("_(01312)", copyField!.Text);

            var meta = card.FindName("MetaLabel") as TextBlock;
            Assert.NotNull(meta);
            Assert.DoesNotContain("Заявка", meta!.Text);
        });
    }

    /// <summary>Спектр выбора цвета строится, рисуется и показывает то, что ему задали.
    ///
    /// Просьба: «сделай выбор цвета не только кодом, но и спектром». Арифметика проверена отдельно
    /// (ColorSpectrumTests), здесь — что элемент вообще живой: три слоя градиентов и два маркера в
    /// разметке легко сломать опечаткой в имени ресурса, а компилятор про XAML молчит.</summary>
    [Fact]
    public void ColorSpectrumPicker_BuildsAndRenders()
    {
        Ui.Run(() =>
        {
            var picker = new ColorSpectrumPicker();
            picker.Width = 240;
            picker.Height = 200;
            Render(picker);

            // Цвет, заданный снаружи (поле кода, образец палитры), элемент принимает как свой.
            var blue = (Color)ColorConverter.ConvertFromString(AccentPalette.DefaultHex)!;
            picker.SelectedColor = blue;
            Render(picker);
            Assert.Equal(blue, picker.SelectedColor);

            // Подложка квадрата — ЧИСТЫЙ оттенок выбранного цвета, а не сам цвет: бледность и
            // темноту накладывают градиенты поверх.
            var layer = picker.FindName("HueLayer") as System.Windows.Shapes.Rectangle;
            Assert.NotNull(layer);
            var fill = Assert.IsType<SolidColorBrush>(layer!.Fill);
            var (h, _, _) = ColorSpectrum.ToHsv(blue);
            Assert.Equal(ColorSpectrum.FromHsv(h, 1, 1), fill.Color);
        });
    }

    /// <summary>Окно тикета без вложений не показывает ни раздела «Вложения», ни кнопок к нему.
    ///
    /// Жалоба: «в тикетах зачем отображать „открыть папку вложений" или „открыть вложение", если их
    /// нет». Решение о том, что показывать, проверено отдельно (TicketAttachmentsPanelTests); здесь —
    /// что окно это решение действительно применяет, а не считает его в стол.
    ///
    /// Заодно это первая проверка самого окна: до сих пор оно в разборе разметки не участвовало, и
    /// адрес иконки в нём был относительный — тот самый случай, который уже ронял диалоги.</summary>
    [Fact]
    public void TicketDetailDialog_БезВложенийПрячетРазделЦеликом()
    {
        Ui.Run(() =>
        {
            var ticket = new AntarusPoFinder.Core.Domain.Ticket
            {
                Id = "t1", Type = "fw_bug", Status = "open", Severity = "major",
                Text = "Прошивка не грузится", CreatedBy = "Илья", CreatedAt = "2026-10-08 12:00",
            };

            // root = null: сетевого диска нет вовсе. Кнопок всё равно быть не должно.
            var dialog = new TicketDetailDialog(ticket, root: null);
            Render(dialog);

            var open = dialog.FindName("OpenAttachmentBtn") as FrameworkElement;
            var folder = dialog.FindName("OpenAttachmentsFolderBtn") as FrameworkElement;
            Assert.NotNull(open);
            Assert.NotNull(folder);
            Assert.Equal(Visibility.Collapsed, open!.Visibility);
            Assert.Equal(Visibility.Collapsed, folder!.Visibility);
        });
    }

    /// <summary>Окно правки таблицы параметров строится, и столбец «Назначение» в нём есть.
    ///
    /// Показывается он только в бете (AppIdentity.IsBeta), поэтому проверяется не видимость, а то,
    /// что колонка вообще разобралась и привязка к RowVm.PurposeLabel не промахнулась мимо имени:
    /// промах в пути привязки компилятор не ловит, а на живом прогоне он выглядит как пустая
    /// ячейка, которую легко принять за «ещё не разметили».</summary>
    [Fact]
    public void ParamTableEditorDialog_BuildsAndShowsPurposeColumn()
    {
        Ui.Run(() =>
        {
            var rows = new List<AntarusPoFinder.Core.Domain.ParamTableRow>
            {
                new()
                {
                    Kind = AntarusPoFinder.Core.Domain.ParamRowKind.Param, GroupName = "Двигатель",
                    Code = "P1-02", Title = "Номинальный ток двигателя",
                    Purpose = AntarusPoFinder.Core.Domain.ParamRowPurpose.Service,
                },
            };

            var dialog = new ParamTableEditorDialog(new[] { "Настройка ШУ", "Двигатель" },
                Array.Empty<AntarusPoFinder.Core.Domain.ParamTableColumn>(), "Задание Modbus", rows);
            Render(dialog);

            var column = dialog.FindName("PurposeColumn") as System.Windows.Controls.DataGridColumn;
            Assert.NotNull(column);
            Assert.Equal(AntarusPoFinder.Core.Domain.AppIdentity.IsBeta ? Visibility.Visible : Visibility.Collapsed,
                column!.Visibility);

            var grid = dialog.FindName("RowsGrid") as System.Windows.Controls.DataGrid;
            var vm = Assert.IsType<ParamTableEditorDialog.RowVm>(grid!.Items[0]);
            Assert.Equal("Сервис", vm.PurposeLabel);

            dialog.Close();
        });
    }

    [Fact]
    public void FwBugDialog_BuildsAndRenders()
    {
        Ui.Run(() =>
        {
            var dialog = new FwBugDialog("НГР 2.0 / КПЧ / ATV310 / 1.74.0 (3 насоса)");
            Render(dialog);

            // По умолчанию «серьёзный», а не «критичный»: предлагать самый громкий уровень
            // значит получить его во всех тикетах подряд, и шкала перестанет что-либо означать.
            Assert.Equal(AntarusPoFinder.Core.Domain.FwBugSeverity.Major, dialog.Severity);

            dialog.Close();
        });
    }

    // ── Окно «кому положить эту инструкцию» ───────────────────────────────────
    //
    // Единственное окно в этом файле, и попало оно сюда не случайно: иконка у него задана длинным
    // абсолютным pack-адресом, а не коротким «/Assets/icon.ico», как у остальных диалогов, — значит
    // в тестовом процессе оно строится (почему остальные нет — см. примечание в конце файла).
    // Проверяется прежде всего УМОЛЧАНИЕ: «применить ко всем более ранним» при открытии снято.
    // Замена идёт по общему диску, отменить её нечем, и переписать десяток версий одним кликом
    // должно быть осознанным действием.

    private static List<InstructionTarget> ScopeTargets()
    {
        FwVersionRecord V(int id, int sw) => new()
        {
            Id = id, SwVersion = sw, HwVersion = 1, VersionRaw = $"2.1.0001.{sw:0000}",
        };
        return new List<InstructionTarget>
        {
            new(V(3, 3), "2.1.0001.0003", IsSelf: true, IsEarlier: false, HasInstruction: true),
            new(V(2, 2), "2.1.0001.0002", IsSelf: false, IsEarlier: true, HasInstruction: true),
            new(V(1, 1), "2.1.0001.0001", IsSelf: false, IsEarlier: true, HasInstruction: false),
        };
    }

    private static List<CheckBox> ScopeRows(InstructionScopeDialog dialog) =>
        ((StackPanel)dialog.FindName("ChecksPanel")!).Children.OfType<CheckBox>().ToList();

    [Fact]
    public void InstructionScopeDialog_ПоУмолчаниюОтмеченаТолькоПравимаяВерсия()
    {
        Ui.Run(() =>
        {
            var dialog = new InstructionScopeDialog(ScopeTargets(), @"C:\tmp\инструкция.pdf", "НГР 2.0 / SMH5");
            Render(dialog);

            Assert.False(((CheckBox)dialog.FindName("EarlierCheck")!).IsChecked);

            var rows = ScopeRows(dialog);
            Assert.Equal(3, rows.Count);
            Assert.True(rows[0].IsChecked);
            // Своя версия заблокирована: документ ей кладёт обычное сохранение модерации, и снять её
            // здесь значило бы обещать отмену, которой не будет.
            Assert.False(rows[0].IsEnabled);
            Assert.False(rows[1].IsChecked);
            Assert.False(rows[2].IsChecked);

            dialog.Close();
        });
    }

    [Fact]
    public void InstructionScopeDialog_ГалочкаБолееРаннихРаботаетВОбеСтороны()
    {
        Ui.Run(() =>
        {
            var dialog = new InstructionScopeDialog(ScopeTargets(), @"C:\tmp\инструкция.pdf", "НГР 2.0 / SMH5");
            var earlier = (CheckBox)dialog.FindName("EarlierCheck")!;

            earlier.IsChecked = true;
            Assert.All(ScopeRows(dialog), row => Assert.True(row.IsChecked));

            // Снятая галочка возвращает набор к «только эта» — иначе она работала бы в одну сторону,
            // и снять её было бы нечем, кроме как щёлкать по версиям поштучно.
            earlier.IsChecked = false;
            var rows = ScopeRows(dialog);
            Assert.True(rows[0].IsChecked);
            Assert.False(rows[1].IsChecked);
            Assert.False(rows[2].IsChecked);

            dialog.Close();
        });
    }

    [Fact]
    public void InstructionScopeDialog_ВНаборУходятТолькоЧужиеВерсии()
    {
        Ui.Run(() =>
        {
            var dialog = new InstructionScopeDialog(ScopeTargets(), @"C:\tmp\инструкция.pdf", "НГР 2.0 / SMH5");
            ((CheckBox)dialog.FindName("EarlierCheck")!).IsChecked = true;

            // Через CollectSelection, а не нажатием кнопки: обработчик кнопки задаёт DialogResult, а
            // его можно задавать только у окна, показанного как диалог, — тест упал бы на этой
            // строке, ничего не проверив.
            dialog.CollectSelection();

            // Правимая версия в набор не уходит: её документ кладёт обычное сохранение модерации.
            Assert.Equal(new int?[] { 2, 1 }, dialog.Selected.Select(v => v.Id).ToArray());
        });
    }

    /// <summary>Окно «потерянные инструкции» строится и НЕ лезет на диск при открытии: обход сетевой
    /// шары на каждом показе окна человек читает как зависание, поэтому он запускается кнопкой
    /// «Проверить» (см. OrphanInstructionsDialog).</summary>
    [Fact]
    public void OrphanInstructionsDialog_ОткрываетсяНеТрогаяДиск()
    {
        Ui.Run(() =>
        {
            var dialog = new OrphanInstructionsDialog(Services());
            Render(dialog);

            Assert.Equal("Проверка не запускалась.", ((TextBlock)dialog.FindName("StatusLabel")!).Text);
            Assert.Empty(((StackPanel)dialog.FindName("FindingsPanel")!).Children);

            dialog.Close();
        });
    }

    [Fact]
    public void TheHierarchyTable_HasTheNoInstructionCheckbox()
    {
        Ui.Run(() =>
        {
            var services = Services();
            var view = new SettingsView(services, new MainWindowViewModel(services));

            var column = view.HierarchyGrid.Columns.FirstOrDefault(c => (c.Header as string) == "Инструкции не будет");
            Assert.NotNull(column);
            // Именно шаблон с галочкой: текстовая колонка выглядела бы так же в заголовке, но
            // переключить признак ею нельзя.
            Assert.IsType<DataGridTemplateColumn>(column);
        });
    }

    /// <summary>Список страниц выше покрывает ВСЕ пункты меню. Иначе новая страница появилась бы в
    /// приложении, но не в этом тесте, и «просто открывается» у неё бы никто не проверял.</summary>
    [Fact]
    public void ThePageListHere_CoversEveryNavItem()
    {
        var covered = new HashSet<string>();
        foreach (var row in Pages()) covered.Add((string)row[0]);

        foreach (var (pageId, label, _) in RolesConfig.NavItems)
            Assert.True(covered.Contains(pageId),
                $"страница «{label}» ({pageId}) есть в меню, но не проверяется на отрисовку");
    }

    // ── Почему окон здесь нет ─────────────────────────────────────────────────
    //
    // Окна (StubLayoutWindow, AdStartupLoginDialog, RoleSwitchDialog и прочие диалоги) задают иконку
    // коротким относительным адресом «/Assets/icon.ico» — так написано во ВСЕХ диалогах проекта, и в
    // приложении это работает. Такой адрес WPF разрешает относительно «сборки ресурсов», а ею он
    // назначает входную сборку процесса — в тестах это тестовый хост, и иконка не находится: окно не
    // строится вовсе. Подменить сборку ресурсов нельзя: свойство отдаёт значение по умолчанию при
    // первом же чтении, а присвоение после этого запрещено.
    //
    // Переводить иконки всех диалогов на длинный абсолютный адрес только ради теста — менять продукт
    // под инструмент. Поэтому окна проверяются живым прогоном (scratchpad/live), а здесь остаются
    // страницы: у них атрибута Icon нет, и они строятся честно.
}
