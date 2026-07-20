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

    private static readonly Dictionary<string, (string Title, string Body)> NavStepText = new()
    {
        ["inspection"] = ("Осмотр", "Здесь сохраняются фото и сканы при осмотре оборудования — можно загрузить с телефона по QR-коду."),
        ["newversions"] = ("Модерация тегов", "Новые прошивки без тегов ждут здесь для разметки (прошивка всё равно отображается при поиске)."),
        ["upload"] = ("Загрузка ПО", "Здесь загружаются новые версии прошивок в общий архив."),
        ["params"] = ("Параметры ПЧ/УПП", "Файлы параметров преобразователей частоты и приводов."),
        ["network"] = ("Сетевые диски", "Путь к диску и интервал синхронизации — настраивается на каждом компьютере отдельно."),
    };

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _vm = new MainWindowViewModel(services);
        DataContext = _vm;

        ContentRendered += MainWindow_ContentRendered;
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
            if (NavItemsControl.ItemContainerGenerator.ContainerFromItem(navItem) is not FrameworkElement container) continue;
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
        steps.Add(new OnboardingStep(() => RoleSwitchNavButton, "Роль",
            "Наладчик, программист или администратор — от роли зависит, что видно в меню."));
        steps.Add(new OnboardingStep(() => ThemeToggleControl, "Тема оформления",
            "Переключение между светлой и тёмной темой."));

        var overlay = new OnboardingOverlay(steps);
        overlay.Finished += (_, _) =>
        {
            OnboardingHost.Content = null;
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
