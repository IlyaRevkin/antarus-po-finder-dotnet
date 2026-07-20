using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Navigation;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.App.Views;

namespace AntarusPoFinder.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainWindowViewModel _vm;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    /// <summary>Escape hatch for code that must guarantee the process actually exits — the self-update
    /// restart flow (AppUpdateService.InstallAndRestartAsync) calls Application.Current.Shutdown()
    /// and then waits for THIS process to die before moving the staged exe into place; if the
    /// "закрытие сворачивает в трей" setting is on, Window_Closing would otherwise cancel that
    /// shutdown (WPF aborts Application.Shutdown() entirely when any window cancels its Closing) and
    /// the update would hang forever waiting for a process that never exits. Set right before calling
    /// Shutdown(), never reset — the process is going down either way once that happens.</summary>
    public static bool ForceRealExit { get; set; }

    private static readonly Dictionary<string, (string Title, string Body)> NavStepText = new()
    {
        ["inspection"] = ("Осмотр", "Здесь сохраняются фото и сканы при осмотре оборудования — можно загрузить с телефона по QR-коду."),
        ["newversions"] = ("Модерация тегов", "Новые прошивки без тегов ждут здесь для разметки (прошивка всё равно отображается при поиске)."),
        ["upload"] = ("Загрузка ПО", "Здесь загружаются новые версии прошивок в общий архив."),
        ["params"] = ("Параметры ПЧ/УПП", "Файлы параметров преобразователей частоты и приводов."),
        ["network"] = ("Сетевые диски", "Путь к диску и интервал синхронизации — настраивается на каждом компьютере отдельно."),
        ["tickets"] = ("Тикеты", "Баг-репорты и предложения — наладчик и программист видят только свои, администратор видит и закрывает все."),
    };

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _vm = new MainWindowViewModel(services);
        DataContext = _vm;

        ContentRendered += MainWindow_ContentRendered;
        InitTrayIcon();
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    // ── Системный трей ───────────────────────────────────────────────────────
    // See ConfigService.CloseAction: "close" (default) leaves the X button/taskbar-minimize behaving
    // exactly as before this feature existed; "tray" hides the window instead of exiting/minimizing
    // to the taskbar. The NotifyIcon itself is created once up front but stays invisible until the
    // window is actually hidden to it — nothing to click on otherwise.

    private void InitTrayIcon()
    {
        try
        {
            var exePath = System.Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (_, _) => RestoreFromTray());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) =>
            {
                ForceRealExit = true;
                Close();
            });

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "Antarus ПО Finder",
                ContextMenuStrip = menu,
                Visible = false,
            };
            _trayIcon.MouseClick += (_, e) =>
            {
                // Click (left) and DoubleClick both restore — matches Проводник-style tray icons
                // where either a single or double click is enough, no need to hit it twice.
                if (e.Button == System.Windows.Forms.MouseButtons.Left) RestoreFromTray();
            };
        }
        catch { /* best effort — if the exe's icon can't be extracted, tray support silently degrades to "always close" */ }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ForceRealExit && _trayIcon is not null && _services.Cfg.CloseAction() == "tray")
        {
            e.Cancel = true;
            Hide();
            _trayIcon.Visible = true;
            return;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    /// <summary>Optional but natural extension of "сворачивать в трей": the taskbar's own minimize
    /// button behaves the same way once that setting is on, not just the X button.</summary>
    private void MainWindow_StateChanged(object? sender, System.EventArgs e)
    {
        if (WindowState != WindowState.Minimized || _trayIcon is null) return;
        if (_services.Cfg.CloseAction() != "tray") return;
        Hide();
        _trayIcon.Visible = true;
    }

    private void MainWindow_ContentRendered(object? sender, System.EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        if (!_services.Cfg.OnboardingShown())
            ShowOnboarding(markAsShown: true);
    }

    private void ShowOnboarding_Click(object sender, RoutedEventArgs e) => ShowOnboarding(markAsShown: false);

    /// <summary>Steps that dive into a page (Поиск/Загрузка ПО/Настройки) call _vm.Navigate + this
    /// window's own UpdateLayout inside their resolver — Navigate() only swaps ContentControl.
    /// Content, the actual measure/arrange for the new page's controls happens on the next layout
    /// pass, so TranslatePoint/RenderSize would read stale (0,0) values without forcing it here.</summary>
    private FrameworkElement? NavigateAndResolve(string pageId, System.Func<FrameworkElement?> resolve)
    {
        ((IAppHost)_vm).Navigate(pageId);
        UpdateLayout();
        return resolve();
    }

    private void ShowOnboarding(bool markAsShown)
    {
        var originalPageId = _vm.CurrentPageId;

        var steps = new List<OnboardingStep>
        {
            new(() => SidebarBorder, "Antarus ПО Finder",
                "Короткая обучалка по разделам программы. «Далее» — следующий шаг, «Пропустить всё» — закрыть сразу."),
        };

        // Настройки/Сетевые диски/Тикеты/смена темы all live inside the collapsible "ДОПОЛНИТЕЛЬНО"
        // strip now, collapsed by default (Visibility="Collapsed" — no layout position at all, so a
        // step targeting a control inside it would have nothing valid to highlight). Force it open
        // for the whole tour, restore whatever state it was actually in once the tour finishes.
        //
        // Setting MoreSectionToggle.IsChecked alone isn't enough: MoreSectionContent's Visibility is
        // driven by a Binding to that IsChecked value, and when going false→true→(restored to
        // false)→true again across repeated tour runs, the binding's own update sometimes lands
        // AFTER this method's UpdateLayout() call — that race is exactly what left "Сетевые диски"
        // highlighted as a 0-height sliver (the container existed, at its still-Collapsed layout
        // slot). SetCurrentValue forces MoreSectionContent visible immediately without tearing out
        // the Binding — restoring IsChecked below still works correctly afterward either way: if the
        // section was open before, this is already the right end state; if it was closed, setting
        // IsChecked back to false raises a real property-changed notification, and the Binding
        // reasserts Collapsed on top of this override once that fires.
        var wasMoreExpanded = MoreSectionToggle.IsChecked == true;
        MoreSectionToggle.IsChecked = true;
        MoreSectionContent.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        UpdateLayout();

        foreach (var navItem in _vm.NavItems.Where(n => n.IsVisible))
        {
            var pageId = navItem.PageId;

            if (pageId == "search")
            {
                // No separate "Поиск" nav-container intro step here — it only duplicated the nav
                // label and the step below, while highlighting the wrong element (sidebar button,
                // not the actual search box). Straight into the real search box.
                steps.Add(new OnboardingStep(
                    () => NavigateAndResolve("search", () => (_vm.CurrentPageContent as SearchView)?.OnboardingTarget("input")),
                    "Поиск",
                    "Просто «Найти» или Enter для поиска."));
                steps.Add(new OnboardingStep(
                    () => (_vm.CurrentPageContent as SearchView)?.OnboardingTarget("mode"),
                    "Режим поиска",
                    "Переключатель между прошивками, файлами параметров и электросхемами — отображает результат только выбранного раздела."));
                continue;
            }

            if (!NavStepText.TryGetValue(pageId, out var text)) continue;
            // "Сетевые диски"/"Тикеты" (IsCompact) render in CompactNavItemsControl, everything else
            // in NavItemsControl — but NavItems is bound (unfiltered) to BOTH controls, so a compact
            // item still gets a real (just Visibility="Collapsed") container generated in
            // NavItemsControl too. Checking NavItemsControl first (as this used to) found THAT
            // collapsed container before ever reaching CompactNavItemsControl — non-null, so the
            // "?? "fallback below never ran, and the tour highlighted a zero-size collapsed element
            // wherever it happened to flow (right under the last visible item above it) instead of
            // the real button. Route by IsCompact instead of chaining "??" across both.
            var container = (navItem.IsCompact
                ? CompactNavItemsControl.ItemContainerGenerator.ContainerFromItem(navItem)
                : NavItemsControl.ItemContainerGenerator.ContainerFromItem(navItem)) as FrameworkElement;
            if (container is null) continue;
            steps.Add(new OnboardingStep(() => container, text.Title, text.Body));

            if (pageId == "upload")
            {
                steps.Add(new OnboardingStep(
                    () => NavigateAndResolve("upload", () => (_vm.CurrentPageContent as UploadView)?.OnboardingTarget("dropzone")),
                    "Файл прошивки",
                    "Перетащите файл или папку сюда, либо нажмите для выбора — для .psl/.dpj/.kpr/.kpj контроллер подставляется автоматически."));
                steps.Add(new OnboardingStep(
                    () => (_vm.CurrentPageContent as UploadView)?.OnboardingTarget("opc"),
                    "ОПЦ заявка / SN",
                    "Для нестандартных шкафов — два независимых чекбокса: № заявки и SN шкафа. Можно включить один, другой или оба сразу."));
                steps.Add(new OnboardingStep(
                    () => (_vm.CurrentPageContent as UploadView)?.OnboardingTarget("reserve"),
                    "Резервация номера версии",
                    "Если номер нужно вписать в прошивку ДО сборки — зарезервируйте его заранее. У резервации есть срок действия (настраивается в Настройки → Резервация номеров)."));
            }
        }

        steps.Add(new OnboardingStep(() => MoreSectionToggle, "Дополнительно",
            "Настройки, Сетевые диски, Тикеты, смена темы и обучение нужны реже, чем остальные разделы — убраны сюда и свёрнуты по умолчанию. Разворачивается кликом по этой строке."));
        steps.Add(new OnboardingStep(() => SettingsNavButton, "Настройки",
            "Иерархия шкафов, теги, резервация номеров и модерация тегов и прошивок — видно только администратору."));
        if (_vm.SettingsVisible)
        {
            steps.Add(new OnboardingStep(
                () => NavigateAndResolve("settings", () => (_vm.CurrentPageContent as SettingsView)?.OnboardingTarget("tabbar")),
                "Вкладки настроек",
                "Общие, Иерархия, Прошивки, Модерация, Резервация номеров, Теги, Быстрый доступ — у каждой вкладки свой набор действий."));
        }
        steps.Add(new OnboardingStep(() => NotificationsNavButton, "Уведомления",
            "Всё, что показывалось в статус-баре или баннером сверху, остаётся здесь — даже после того как баннер сам скрылся через несколько секунд."));
        steps.Add(new OnboardingStep(() => ThemeToggleControl, "Тема оформления",
            "Переключение между светлой и тёмной темой."));
        steps.Add(new OnboardingStep(() => RoleSwitchNavButton, "Роль",
            "Наладчик, программист или администратор — от роли зависит, что видно в меню. Значок ⟳ рядом с названием роли открывает смену роли."));

        var overlay = new OnboardingOverlay(steps);
        overlay.Finished += (_, _) =>
        {
            OnboardingHost.Content = null;
            MoreSectionToggle.IsChecked = wasMoreExpanded;
            if (markAsShown) _services.Cfg.SetOnboardingShown(true);
            // The tour hops between pages to show real controls — land back where the user actually
            // was, not wherever the last step happened to leave the ContentControl.
            ((IAppHost)_vm).Navigate(originalPageId);
        };
        OnboardingHost.Content = overlay;
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _services.Db.Dispose();
        base.OnClosed(e);
    }

    private void RoleSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RoleSwitchDialog(_services.Cfg, _vm.CurrentRole) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedRole is not null)
        {
            _vm.SwitchRole(dlg.SelectedRole);
        }
    }

    private void GitHubLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
