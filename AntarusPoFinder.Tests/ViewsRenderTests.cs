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
