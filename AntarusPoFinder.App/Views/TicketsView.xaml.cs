using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Тикеты (баг-репорты/предложения) — видна всем ролям, но набор действий зависит от роли:
/// наладчик/программист видят и создают только СВОИ тикеты (см. Ticket.CreatedBy — Windows-логин,
/// у ролей нет персональных учёток), администратор видит все и может менять статус. Синхронизация
/// между машинами — TicketSyncService (event-log на сетевом диске), не связана с ConfigSyncService.</summary>
public partial class TicketsView : UserControl
{
    private class TicketRow
    {
        public required Ticket Ticket { get; init; }
        public string TypeLabel => TicketType.Label(Ticket.Type);

        /// <summary>Критичность словом — пусто у всего, кроме багов прошивок.</summary>
        public string SeverityLabel => FwBugSeverity.Label(Ticket.Severity);
        public int SeverityOrder => FwBugSeverity.SortOrder(Ticket.Severity);

        /// <summary>Подпись прошивки, снятая в момент жалобы (см. FwBugLabel).</summary>
        public string FwLabel => Ticket.FwLabel;

        /// <summary>Цвет строки. Просьба владельца дословно: «Баг прошивок красным должен быть…
        /// предложение зелёным… критичность бага, как красный оранжевый жёлтый, 3 уровня».
        ///
        /// У бага прошивки цвет берётся ПО КРИТИЧНОСТИ, а не по типу: иначе все три уровня выглядели бы
        /// одинаково красными и шкала не значила бы ничего. Закрытый тикет не красится вовсе: красная строка
        /// о решённой беде — ложная тревога, а именно от неё цветовые списки и перестают читать.</summary>
        /// <summary>Правило живёт в Core (TicketBoard.RowBrushKey), а не здесь: его проверяют тесты,
        /// а приватный класс страницы им не виден. Приглушение закрытых — переделка вида, поэтому
        /// только в бете; в рабочей версии закрытый тикет выглядит как прежде.</summary>
        public string RowBrushKey => TicketBoard.RowBrushKey(Ticket, muteClosed: AppIdentity.IsBeta);
        public string Text => Ticket.Text;
        public string StatusLabel => TicketStatus.Label(Ticket.Status);
        /// <summary>По чему столбец «Статус» на самом деле сортируется — см. TicketStatus.SortOrder.</summary>
        public int StatusOrder => TicketStatus.SortOrder(Ticket.Status);
        public string CreatedBy => Ticket.CreatedBy;
        /// <summary>У автоотчёта роль — «system», и RolesConfig её не знает: в столбце «Роль»
        /// стояло бы английское слово. Пишем по-русски, как и все остальные роли.</summary>
        public string CreatedByRoleLabel =>
            string.Equals(Ticket.CreatedByRole, TicketAutoReports.SystemRole, StringComparison.OrdinalIgnoreCase)
                ? "программа"
                : RolesConfig.RoleLabel(Ticket.CreatedByRole);
        public string CreatedAtLabel => DateTime.TryParse(Ticket.CreatedAt, out var dt) ? dt.ToString("dd.MM.yyyy HH:mm") : Ticket.CreatedAt;

        /// <summary>По чему столбец «Создан» сортируется на самом деле.
        ///
        /// Показываем дату по-русски («дд.ММ.гггг чч:мм»), а сортировка по этой же строке идёт
        /// посимвольно — то есть по ДНЮ МЕСЯЦА: 01.09 оказывается выше 30.08, и порядок выглядит
        /// случайным. Именно это и назвали багом сортировки по дате создания. Сортируем по самой
        /// дате; неразобранное значение уезжает в конец (DateTime.MinValue), а не притворяется
        /// сегодняшним.</summary>
        public DateTime CreatedAtSort => DateTime.TryParse(Ticket.CreatedAt, out var dt) ? dt : DateTime.MinValue;
    }

    private readonly AppServices _services;
    private readonly IAppHost _host;
    private readonly List<string> _pendingAttachmentPaths = new();

    /// <summary>Tracks whether the LAST Activate() sync had a failure — same "only notify on the
    /// transition" rule as MainWindowViewModel.PushConfigNow, so a share that stays unreachable for a
    /// while doesn't produce a fresh toast literally every time the operator opens this page, but a
    /// tickets/status change that's stuck NOT reaching other machines is no longer invisible forever
    /// the way it used to be (root cause of the same "молча не синхронизируется" bug class as the
    /// config auto-push and app auto-update had — see PushConfigNow/AppUpdateService.TakeLastUpdateError).</summary>
    private bool _syncLastFailed;

    /// <param name="fwBugsOnly">Страница открыта как САМОСТОЯТЕЛЬНАЯ «Баги прошивок» — отдельный
    /// пункт меню в секции «ДЛЯ ПРОГРАММИСТА» (бета 2.0, просьба «все же раздели вкладку тикетов
    /// программиста и тикетов по приложению»). Тогда пилюли сверху не нужны: переключать не на что,
    /// тикеты по программе живут своей страницей.</param>
    public TicketsView(AppServices services, IAppHost host, bool fwBugsOnly = false)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _fwBugsOnly = fwBugsOnly;
        _fwBugsTab = fwBugsOnly;
        if (fwBugsOnly)
        {
            TabsRow.Visibility = Visibility.Collapsed;
            // Заголовок обязан совпадать с пунктом меню, по которому сюда пришли: страница «Баги
            // прошивок», открывающаяся словом «Тикеты», читается как «не туда попал».
            PageTitle.Text = "Баги прошивок";
        }

        // «Баг прошивки» здесь НЕ предлагается намеренно: такой тикет осмыслен только вместе со
        // ссылкой на конкретную прошивку, а её может подставить только карточка выдачи (жучок). Заведённый
        // отсюда, он был бы багом неизвестно чего — и попал бы программисту без самого главного.
        foreach (var (id, label) in TicketType.All.Where(t => t.Id != TicketType.FwBug))
            TicketTypeCombo.Items.Add(new TicketTypeOption(id, label));
        TicketTypeCombo.SelectedIndex = 0;

        ApplyTab();
        ApplyBetaLayout();
        Loaded += (_, _) => Activate();
    }

    /// <summary>Бета: страница — это СПИСОК, а не ряд кнопок под ним. Тикет Ильи дословно:
    /// «в окне тикетов сделать скрол таблицы тикетов а не окна, чтобы кнопки подробнее обновить
    /// выгрузить были видны сразу. а лучше еще убрать кнопку подробнее так как двойным кликом можно
    /// открыть тикет. убрать кнопки выгрузить и обновить. сделать это автоматическими».
    ///
    /// Что делается:
    /// • «Подробнее» убрана — тикет открывается двойным щелчком, а с клавиатуры Enter'ом по
    ///   выделенной строке (см. TicketsGrid_PreviewKeyDown). Без замены с клавиатуры удаление
    ///   кнопки отрезало бы от тикетов тех, кто работает без мыши.
    /// • «Обновить» убрана — список и так перечитывается при каждом открытии страницы (Activate:
    ///   FlushOutbox + PullNewEvents + обмен с хранилищем) и по приходу изменений извне
    ///   (MainWindowViewModel зовёт RefreshIfActive/ReloadRows). Кнопка повторяла то, что и так
    ///   происходит само.
    /// • «Выгрузить…» ПЕРЕЕХАЛА в «Ещё», а не исчезла: автовыгрузка (TicketAutoDrop) возит только
    ///   сами записи тикетов, вложения наружу не едут вовсе — они бывают по сотне мегабайт, а
    ///   страница приёма ограничена по размеру. Архив «тикеты вместе с вложениями» собрать больше
    ///   нечем, поэтому молча выкинуть кнопку нельзя; но и в первом ряду ей не место — нужна она
    ///   считанные разы в месяц.
    /// • Оставшиеся действия («Взять в работу», «Закрыть», «Переоткрыть») уезжают НАД таблицей:
    ///   таблица занимает всё оставшееся место и прокручивается сама, а ряд под ней в невысоком
    ///   окне уходил за нижний край — о чём жалоба и была.
    ///
    /// Только бета: рабочая версия обязана остаться прежней (образец — SearchView.HideParamFilesMode).</summary>
    private void ApplyBetaLayout()
    {
        if (!AppIdentity.IsBeta) return;

        DetailBtn.Visibility = Visibility.Collapsed;
        RefreshBtn.Visibility = Visibility.Collapsed;
        ExportBtn.Visibility = Visibility.Collapsed;   // живёт в «Ещё», см. More_Click
        MoreBtn.Visibility = Visibility.Visible;

        // Ряд действий переносится из последней строки Grid'а в полосу над таблицей. Именно
        // переносится, а не дублируется: видимостью кнопок в нём управляет UpdateActionButtons, и
        // второй экземпляр пришлось бы держать в согласии с первым.
        if (ActionsRow.Parent is Grid grid)
        {
            grid.Children.Remove(ActionsRow);
            ActionsRow.VerticalAlignment = VerticalAlignment.Center;
            TopActionsHost.Children.Insert(0, ActionsRow);
        }

        // Подсказка про вложения звала «кнопкой Выгрузить…» — теперь она в «Ещё», и звать надо
        // туда: совет, ведущий к кнопке, которой на виду нет, хуже отсутствия совета.
        AttachmentsNoteText.Text =
            "Тикет и его статус синхронизируются между машинами и с хранилищем на хостинге сами. " +
            "Вложения остаются на сетевом диске конторы — чтобы их увидел тот, кто вне сети, отправьте архив: «Ещё» → «Выгрузить…».";
    }

    /// <summary>Редкие действия страницы под одной пилюлей — как «Ещё» во вкладках Настроек
    /// (SettingsView.MoreTabs_Click).</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        var menu = new ContextMenu
        {
            PlacementTarget = MoreBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var export = new MenuItem
        {
            Header = "Выгрузить…",
            ToolTip = "Сложить тикеты вместе с вложениями в один архив — унести флешкой, отправить почтой или загрузить " +
                      "на страницу приёма. Сами тикеты уезжают и без этого, автоматически; вложения — нет.",
            IsEnabled = ExportBtn.IsEnabled,
        };
        export.Click += (_, _) => ExportTickets_Click(ExportBtn, new RoutedEventArgs());
        menu.Items.Add(export);
        menu.IsOpen = true;
    }

    private record TicketTypeOption(string Id, string Label);

    public void RefreshIfActive()
    {
        if (IsLoaded) Activate();
    }

    /// <summary>Какая вкладка открыта. Не запоминается между запусками намеренно: «Тикеты» открывают,
    /// чтобы пожаловаться на программу, а баги прошивок смотрят осознанно.</summary>
    private bool _fwBugsTab;

    /// <summary>Страница открыта отдельным пунктом меню и показывает ТОЛЬКО баги прошивок:
    /// переключаться ей не на что.</summary>
    private readonly bool _fwBugsOnly;

    private bool IsAdmin => _services.Cfg.CurrentRole() == "administrator";

    private void ApplyScopeHint()
    {
        ScopeHintText.Text = _fwBugsTab
            ? _services.Cfg.CurrentRole() switch
            {
                TicketVisibility.Naladchik =>
                    "Баги, найденные в самих прошивках. Видны все — чтобы ехать на объект, зная, что за прошивкой числится. Закрыть можно свой — если завели ошибочно. Заводятся жучком на карточке поиска.",
                _ =>
                    "Баги, найденные в самих прошивках. Видны все, чьи бы они ни были: можно взять в работу, закрыть и переоткрыть. Заводятся жучком на карточке поиска.",
            }
            : _services.Cfg.CurrentRole() switch
            {
                TicketVisibility.Administrator => "Жалобы и предложения по самой программе. Видны все, можно менять статус.",
                _ => "Жалобы и предложения по самой программе. Видны только созданные вами на любом из компьютеров (по имени пользователя Windows).",
            };
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        var wantFwBugs = ReferenceEquals(sender, TabBtnFwBugs);
        if (wantFwBugs == _fwBugsTab) return;
        _fwBugsTab = wantFwBugs;
        ApplyTab();
        // Подсказка живёт здесь же, а не только в Activate: иначе на вкладке багов продолжало
        // висеть «баг-репорты и предложения по программе» — ровно то, что там НЕ показывается.
        ApplyScopeHint();
        ReloadGrid();
    }

    /// <summary>Что меняется при переключении вкладки.
    ///
    /// На вкладке багов нет формы «Новый тикет»: баг прошивки заводится только жучком на
    /// карточке выдачи, иначе он окажется багом неизвестно чего. И нет галки автоотчётов о сбоях —
    /// это падения самой программы, к прошивкам они отношения не имеют.
    ///
    /// Столбцы тоже разные: критичность и прошивка есть только у багов, а тип на их вкладке
    /// излишен: там всё и так одного типа.</summary>
    private void ApplyTab()
    {
        TabBtnApp.Tag = _fwBugsTab ? null : "Active";
        TabBtnFwBugs.Tag = _fwBugsTab ? "Active" : null;

        NewTicketCard.Visibility = _fwBugsTab ? Visibility.Collapsed : Visibility.Visible;
        ShowAutoReportsCheck.Visibility = _fwBugsTab ? Visibility.Collapsed : Visibility.Visible;

        SeverityColumn.Visibility = _fwBugsTab ? Visibility.Visible : Visibility.Collapsed;
        FwColumn.Visibility = _fwBugsTab ? Visibility.Visible : Visibility.Collapsed;
        TypeColumn.Visibility = _fwBugsTab ? Visibility.Collapsed : Visibility.Visible;

        // «Текст» занимает всю оставшуюся ширину, но ширину-звёздочку приходится задавать ЗАНОВО
        // после каждой смены видимости столбцов: DataGrid не пересчитывает долю сам, и после
        // переключения вкладки текст сжимался в полоску шириной в одну букву, а справа зияла пустота.
        TextColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }



    private void Activate()
    {
ApplyScopeHint();

        var root = _services.Cfg.RootPath();
        if (!string.IsNullOrEmpty(root) && System.IO.Directory.Exists(root))
        {
            try
            {
                TicketSyncService.FlushOutbox(_services, root, out var flushFailed);
                TicketSyncService.PullNewEvents(_services, root, out var pullFailed);
                var failedThisPass = flushFailed + pullFailed;
                if (failedThisPass > 0)
                {
                    if (!_syncLastFailed)
                    {
                        _syncLastFailed = true;
                        _host.ShowStatus($"Синхронизация тикетов: не удалось обработать файлов: {failedThisPass} — повторится при следующем открытии страницы",
                            8000, NotificationCategory.Sync);
                    }
                }
                else if (_syncLastFailed)
                {
                    _syncLastFailed = false;
                    _host.ShowStatus("Синхронизация тикетов восстановлена", 6000, NotificationCategory.Sync);
                }
            }
            catch { /* best effort — local tickets still show, sync retried next time the page opens */ }
        }

        ReloadGrid();
        // Показали актуальный список (в т.ч. только что подтянутые с диска тикеты) — просим шелл
        // сдвинуть watermark «просмотрено» и погасить бейдж на пункте меню.
        _host.OnTicketsViewed();

        // Вторая половина синхронизации — обмен с хранилищем на хостинге (TicketStorageSync). Он
        // ходит в сеть, поэтому не задерживает показ списка: страница уже нарисована местными
        // тикетами, а приехавшее из бакета досыпается в неё, когда придёт. Не await'им намеренно —
        // Activate зовут из Loaded и из «Обновить», обоим нельзя вставать на время похода в сеть.
        _ = SyncWithStorageAsync();
    }

    /// <summary>Сходить в хранилище и показать то, что пришло. Замок и обработка ошибок — в
    /// оболочке (MainWindowViewModel.SyncTicketsWithStorageAsync): обмен идёт ещё и фоном, и два
    /// прохода наперегонки выкладывали бы каждый своё состояние.</summary>
    private async Task SyncWithStorageAsync()
    {
        try
        {
            await _host.SyncTicketsWithStorageAsync(force: true);
        }
        catch { /* обмен сам сообщает о своих бедах; страница из-за них не должна падать */ }

        if (!IsLoaded) return; // страницу успели закрыть, пока ходили в сеть
        ReloadRows();
        _host.OnTicketsViewed();
    }

    /// <summary>Перечитать строки списка БЕЗ синхронизации. Нужно фоновому обмену: он сам только
    /// что сходил в сеть, и полный Activate() запустил бы с открытой страницы второй проход.</summary>
    public void ReloadRows()
    {
        if (!IsLoaded) return;
        ReloadGrid();
    }

    /// <summary>Список: сперва отбор по роли (свои/все), затем автоотчёты о сбоях —
    /// см. TicketAutoReports. Порядок именно такой: число у галки должно считаться по тому, что
    /// человек в принципе может увидеть, иначе наладчику обещали бы чужие спрятанные отчёты.</summary>
    private void ReloadGrid()
    {
        var all = _services.Db.GetTickets();
        // Отбор живёт в Core (TicketVisibility), а не здесь: правило «программист видит все баги
        // прошивок» — правило доступа, и проверять его глазами по живому экрану — худший из способов.
        var visibleToMe = TicketVisibility.Visible(all, _services.Cfg.CurrentRole(), _services.CurrentUserName);

        // Число НЕЗАКРЫТЫХ багов прямо на вкладке. Без него отдельная вкладка стала бы местом,
        // куда надо специально заглядывать, — то есть тем же самым, чем были баги в общей куче: незаметными.
        // Закрытые не считаются: число должно отвечать на вопрос «сколько ещё чинить», а не «сколько было всего».
        var openFwBugs = visibleToMe.Count(t => t.Type == TicketType.FwBug && t.Status != TicketStatus.Closed);
        TabFwBugsText.Text = openFwBugs > 0 ? $"Баги прошивок ({openFwBugs})" : "Баги прошивок";

        // То же число для второй вкладки. Только в бете: на рабочей версии надпись на вкладке —
        // привычка, а привычки меняются бетой (см. SearchView.PlaceSearchHowChecks).
        if (AppIdentity.IsBeta)
        {
            // Автоотчёты в это число не входят: это падения программы, а не чья-то просьба, и
            // человек на них не отвечает. Иначе вкладка показывала бы «24», а в списке лежало бы 17.
            var openApp = TicketBoard.ActiveCount(
                visibleToMe.Where(t => t.Type != TicketType.FwBug && !TicketAutoReports.IsAutoReport(t)));
            TabAppText.Text = openApp > 0 ? $"Программа ({openApp})" : "Программа";
        }
        // Вкладка режет ПОСЛЕ прав доступа, а не вместо них: права — это вопрос «что человеку
        // вообще положено видеть», а вкладка — всего лишь «что он сейчас смотрит».
        var mine = visibleToMe.Where(t => (t.Type == TicketType.FwBug) == _fwBugsTab).ToList();

        var autoCount = TicketAutoReports.Count(mine);
        ShowAutoReportsCheck.Content = autoCount > 0
            ? $"Показывать автоматические отчёты о сбоях ({autoCount})"
            : "Автоматических отчётов о сбоях нет";
        ShowAutoReportsCheck.IsEnabled = autoCount > 0;

        // Закрытые: в бете их прячет галка (по умолчанию снята), в рабочей версии список остаётся
        // прежним — целиком. Отбор и порядок считает ядро (TicketBoard), чтобы правило проверялось
        // тестом, а не глазами по живому экрану.
        var showClosed = !AppIdentity.IsBeta || ShowClosedCheck.IsChecked == true;
        var closedCount = TicketBoard.ClosedCount(TicketAutoReports.Visible(mine, ShowAutoReportsCheck.IsChecked == true));
        ShowClosedCheck.Content = closedCount > 0 ? $"Показывать закрытые ({closedCount})" : "Закрытых тикетов нет";
        ShowClosedCheck.IsEnabled = closedCount > 0;
        ShowClosedCheck.Visibility = AppIdentity.IsBeta ? Visibility.Visible : Visibility.Collapsed;

        var visible = TicketBoard.Visible(mine, ShowAutoReportsCheck.IsChecked == true, showClosed);
        // Порядок по умолчанию — «за что браться», а не «когда заводили» (TicketBoard.Order).
        // Выбранную человеком сортировку это не трогает: она восстанавливается ниже.
        if (AppIdentity.IsBeta) visible = TicketBoard.Order(visible);

        // Порядок сортировки запоминается и восстанавливается вокруг подмены источника.
        // Новый ItemsSource обнуляет и стрелку в заголовке, и сам порядок — и тикет, открытый из
        // отсортированного по статусу списка, после закрытия окна оказывался «в середине, между
        // закрытыми». Раньше это не было заметно, потому что список не перечитывался после
        // открытия тикета; перечитывать его понадобилось из-за переписки — реплика меняет время
        // правки, а по нему строится порядок.
        var sort = TicketsGrid.Items.SortDescriptions.ToList();
        var sortedColumns = TicketsGrid.Columns
            .Where(c => c.SortDirection is not null)
            .Select(c => (Column: c, Direction: c.SortDirection))
            .ToList();

        TicketsGrid.ItemsSource = visible.Select(t => new TicketRow { Ticket = t }).ToList();

        if (sort.Count > 0)
        {
            TicketsGrid.Items.SortDescriptions.Clear();
            foreach (var d in sort) TicketsGrid.Items.SortDescriptions.Add(d);
            // Стрелку в заголовке WPF сам не вернёт: она живёт на колонке, а не в описании
            // сортировки. Без этого порядок правильный, а столбец выглядит несортированным.
            foreach (var (column, direction) in sortedColumns) column.SortDirection = direction;
            TicketsGrid.Items.Refresh();
        }

        UpdateActionButtons();
    }

    private void ShowAutoReports_Changed(object sender, RoutedEventArgs e)
    {
        // Загружается страница — обработчик галки срабатывает из разметки, ДО того как построено
        // всё остальное. На этом сегодня целиком упала страница настроек, так что проверка одна и
        // та же везде: пока окно не собрано, обработчику трогать нечего.
        if (!IsInitialized) return;
        ReloadGrid();
    }

    private void TicketsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionButtons();

    private void UpdateActionButtons()
    {
        var selected = (TicketsGrid.SelectedItem as TicketRow)?.Ticket;
        var canModerate = selected is not null &&
            TicketVisibility.CanModerate(selected, _services.Cfg.CurrentRole(), _services.CurrentUserName);

        TakeInProgressBtn.Visibility = canModerate && selected!.Status != TicketStatus.InProgress ? Visibility.Visible : Visibility.Collapsed;
        CloseTicketBtn.Visibility = canModerate && selected!.Status != TicketStatus.Closed ? Visibility.Visible : Visibility.Collapsed;
        ReopenTicketBtn.Visibility = canModerate && selected!.Status == TicketStatus.Closed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateTicket_Click(object sender, RoutedEventArgs e)
    {
        var text = TicketTextInput.Text.Trim();
        if (text.Length == 0)
        {
            AppMessageBox.Show("Введите текст тикета.", "Тикеты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var type = (TicketTypeCombo.SelectedItem as TicketTypeOption)?.Id ?? TicketType.Other;

        // Заведение тикета (строка в базе + событие в очередь + попытка отправки) живёт одним
        // методом на всех заводчиков — см. TicketSyncService.CreateTicket: тикеты создаёт ещё и
        // «Проверка компьютера», и разъехавшиеся копии этой последовательности рано или поздно
        // разошлись бы в мелочах.
        var ticket = TicketSyncService.CreateTicket(_services, type, text);

        CopyPendingAttachments(ticket.Id);

        TicketTextInput.Clear();
        _host.ShowStatus("Тикет создан", category: NotificationCategory.General);
        ReloadGrid();
        // И сразу в хранилище — иначе тикет, заведённый вне офисной сети (сетевого диска нет),
        // ждал бы ближайшего фонового тика, а его причина ждала бы вместе с ним.
        _ = SyncWithStorageAsync();
    }

    // ── Attachments (staged before creation, then copied straight onto the shared drive —
    //    see TicketSyncService.AttachmentsDir for why these aren't tracked in the DB/event log) ──

    private void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Прикрепить файлы к тикету", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        _pendingAttachmentPaths.AddRange(dlg.FileNames);
        UpdateAttachmentsSummary();
    }

    private void ClearAttachments_Click(object sender, RoutedEventArgs e)
    {
        _pendingAttachmentPaths.Clear();
        UpdateAttachmentsSummary();
    }

    private void UpdateAttachmentsSummary()
    {
        if (_pendingAttachmentPaths.Count == 0)
        {
            AttachmentsSummaryText.Text = "";
            ClearAttachmentsBtn.Visibility = Visibility.Collapsed;
            return;
        }
        AttachmentsSummaryText.Text = "Прикреплено: " + string.Join(", ", _pendingAttachmentPaths.Select(Path.GetFileName));
        ClearAttachmentsBtn.Visibility = Visibility.Visible;
    }

    /// <summary>Copies whatever was staged via "Прикрепить файлы…" onto the shared drive under this
    /// new ticket's id, right after the ticket itself is created. If the share isn't reachable right
    /// now, the ticket is still created (attachments just don't block it) — the operator is told so
    /// explicitly rather than the files silently vanishing.</summary>
    private void CopyPendingAttachments(string ticketId)
    {
        if (_pendingAttachmentPaths.Count == 0) return;

        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show(
                "Тикет создан, но вложения не сохранены — сетевой диск недоступен. Прикрепите их позже, пересоздав тикет, когда диск будет доступен.",
                "Вложения", MessageBoxButton.OK, MessageBoxImage.Warning);
            _pendingAttachmentPaths.Clear();
            UpdateAttachmentsSummary();
            return;
        }

        var dir = TicketSyncService.AttachmentsDir(root, ticketId);
        Directory.CreateDirectory(dir);
        var failed = new List<string>();
        foreach (var src in _pendingAttachmentPaths)
        {
            try { File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), overwrite: true); }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(src)}: {ex.Message}"); }
        }
        if (failed.Count > 0)
            AppMessageBox.Show($"Не удалось приложить:\n{string.Join("\n", failed)}", "Вложения", MessageBoxButton.OK, MessageBoxImage.Warning);

        _pendingAttachmentPaths.Clear();
        UpdateAttachmentsSummary();
    }

    // ── Выгрузка в архив ─────────────────────────────────────────────────────

    /// <summary>Складывает тикеты в один файл, который можно унести с рабочей машины. Тикеты живут
    /// на сетевом диске конторы, и тот, кто их чинит, до него не достаёт — до этой кнопки текст
    /// приходилось пересказывать своими словами, а скриншоты пересобирать руками.</summary>
    private async void ExportTickets_Click(object sender, RoutedEventArgs e)
    {
        var visible = (TicketsGrid.ItemsSource as IEnumerable<TicketRow>)?.Select(r => r.Ticket).ToList() ?? new List<Ticket>();
        var active = visible.Where(t => t.Status != TicketStatus.Closed).ToList();
        var selected = (TicketsGrid.SelectedItem as TicketRow)?.Ticket;

        if (visible.Count == 0)
        {
            AppMessageBox.Show("Выгружать нечего — тикетов в списке нет.", "Выгрузка тикетов",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var root = _services.Cfg.RootPath();
        var shareAvailable = !string.IsNullOrEmpty(root) && Directory.Exists(root);

        // Хранилище настроено (адрес, бакет, ключи) — тогда доступна отправка прямо туда; без него
        // остаётся прежний путь «сохранить файлом».
        var s3 = _services.Cfg.S3();
        var options = new TicketExportDialog(visible.Count, active.Count, selected is not null, shareAvailable,
            s3.CanPublish)
        { Owner = Window.GetWindow(this) };
        if (options.ShowDialog() != true) return;

        var (tickets, scopeLabel) = options.SelectedScope switch
        {
            TicketExportDialog.Scope.Selected => (new List<Ticket> { selected! }, "один выбранный тикет"),
            TicketExportDialog.Scope.AllVisible => (visible, IsAdmin ? "все тикеты" : "тикеты этого пользователя"),
            _ => (active, "открытые и в работе"),
        };
        if (tickets.Count == 0)
        {
            AppMessageBox.Show("По этому отбору тикетов не нашлось.", "Выгрузка тикетов",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var at = DateTime.Now;
        var toStorage = options.SelectedDestination == TicketExportDialog.Destination.Storage;
        string path;
        if (toStorage)
        {
            // Архив собирается во временную папку: в хранилище уезжает содержимое, а на рабочей
            // машине после отправки оставаться нечему.
            path = Path.Combine(Path.GetTempPath(), TicketExportService.SuggestedFileName(at));
        }
        else
        {
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Куда сохранить выгрузку тикетов",
                FileName = TicketExportService.SuggestedFileName(at),
                Filter = "Архив (*.zip)|*.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (save.ShowDialog() != true) return;
            path = save.FileName;
        }

        var meta = new TicketExportService.Meta(
            AppUpdateService.CurrentVersionText, Environment.MachineName, _services.CurrentUserName,
            RolesConfig.RoleLabel(_services.Cfg.CurrentRole()), scopeLabel, at);
        var withAttachments = options.WithAttachments && shareAvailable;

        // Вложения читаются с сетевой шары — на десятке скриншотов это заметные секунды, а окно всё
        // это время не должно висеть замороженным.
        ExportBtn.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => TicketExportService.Write(
                path, meta, tickets,
                withAttachments ? id => TicketSyncService.AttachmentsDir(root, id) : null));

            var summary = $"Выгружено тикетов: {result.Tickets}" +
                          (result.Attachments > 0 ? $", вложений: {result.Attachments}" : "");
            if (result.Warnings.Count > 0)
                summary += "\n\nНе попало в архив:\n" + string.Join("\n", result.Warnings);

            if (toStorage)
            {
                await SendArchiveToStorageAsync(s3, path, at, result, summary);
                return;
            }

            summary += $"\nФайл: {path} ({TicketExportService.SizeLabel(result.Bytes)})";
            _host.ShowStatus($"Тикеты выгружены: {path}", category: NotificationCategory.General);
            if (AppMessageBox.Show(summary + "\n\nПоказать файл в папке?", "Выгрузка тикетов",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
                catch { /* проводник не открылся — путь к файлу человеку уже показан выше */ }
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось выгрузить: {ex.Message}", "Выгрузка тикетов",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ExportBtn.IsEnabled = true;
            // Временный архив нужен был только чтобы его отправить. Не удалился — не беда, папку
            // временных файлов Windows чистит сама.
            if (toStorage)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Кладёт собранный архив в бакет и показывает, куда именно он лёг.
    ///
    /// Ключ содержит случайный хвост (см. <see cref="TicketExportService.StorageObjectName"/>):
    /// бакет отдаётся наружу по публичному веб-адресу, а тикеты — это внутренняя переписка о
    /// поломках со скриншотами рабочих экранов, и предсказуемый адрес открыл бы кто угодно.</summary>
    private async System.Threading.Tasks.Task SendArchiveToStorageAsync(S3Settings s3, string path,
        DateTime at, TicketExportService.Result result, string summary)
    {
        var key = TicketExportService.StorageKey(s3, at, TicketExportService.NewToken());
        var sent = await new S3Client().PutFileAsync(s3, key, path);

        if (!sent.Ok)
        {
            AppMessageBox.Show($"{summary}\n\nВ хранилище не отправлено: {sent.Error}\n\n" +
                               "Архив можно собрать заново кнопкой «Сохранить…» и передать иначе.",
                "Выгрузка тикетов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _host.ShowStatus($"Тикеты отправлены в хранилище: {key}", category: NotificationCategory.General);
        AppMessageBox.Show(
            summary + $"\nРазмер: {TicketExportService.SizeLabel(result.Bytes)}" +
            $"\n\nЛежит в хранилище: {key}\n\n" +
            "Адрес неугадываемый — в имени случайный хвост, но бакет общий, так что ссылку не публиковать.",
            "Выгрузка тикетов", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Detail view (full text + attachments) ────────────────────────────────

    private void ShowDetail_Click(object sender, RoutedEventArgs e) => OpenDetailForSelected();
    private void TicketsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenDetailForSelected();

    /// <summary>Enter на выделенной строке открывает тикет — замена кнопке «Подробнее», которой в
    /// бете больше нет. Иначе единственным входом в тикет остался бы двойной щелчок, то есть мышь:
    /// список, по которому ходят стрелками, оказался бы без входа вовсе.
    ///
    /// Preview — намеренно: собственная обработка Enter в DataGrid переводит выделение на строку
    /// ниже, и до нас событие дошло бы уже съеденным. В рабочей версии поведение прежнее — там
    /// кнопка на месте, а менять привычное нажатие без нужды незачем.</summary>
    private void TicketsGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!IsInitialized) return;
        if (!AppIdentity.IsBeta) return;
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (TicketsGrid.SelectedItem is not TicketRow) return;

        e.Handled = true;
        OpenDetailForSelected();
    }

    private void OpenDetailForSelected()
    {
        if (TicketsGrid.SelectedItem is not TicketRow row) return;
        // Службы передаются ради обсуждения: реплики читаются и пишутся в базу, а после закрытия
        // окна список перечитывается — реплика меняет и время правки тикета, а по нему строится
        // порядок и решается, отдавать ли тикет в хранилище.
        new TicketDetailDialog(row.Ticket, _services.Cfg.RootPath(), _services) { Owner = Window.GetWindow(this) }.ShowDialog();
        ReloadGrid();
        _ = SyncWithStorageAsync();
    }

    private void SetStatus_Click(object sender, RoutedEventArgs e)
    {
        if (TicketsGrid.SelectedItem is not TicketRow row) return;
        if ((sender as Button)?.Tag is not string newStatus) return;

        // Право на смену статуса спрашивается у ОДНОГО правила — TicketVisibility.CanModerate, того
        // же, по которому кнопки показываются (UpdateActionButtons). Здесь стояло «только
        // администратор», и из-за расхождения программист и наладчик видели живые кнопки «Взять в
        // работу» / «Закрыть» на багах прошивок, а нажатие не делало НИЧЕГО и ничего не говорило:
        // тикет оставался открытым, человек считал, что отметил его. Молчаливый отказ — худший из
        // возможных: он неотличим от сломанной синхронизации.
        if (!TicketVisibility.CanModerate(row.Ticket, _services.Cfg.CurrentRole(), _services.CurrentUserName)) return;

        var at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        _services.Db.ApplyTicketStatusIfNewer(row.Ticket.Id, newStatus, at);
        // Запись в очередь автовыгрузки идёт по УЖЕ НОВОМУ состоянию: сам row.Ticket в памяти
        // остался со старым статусом, и отправить его значило бы отправить то, что только что
        // перестало быть правдой.
        row.Ticket.Status = newStatus;
        row.Ticket.UpdatedAt = at;
        TicketSyncService.NoteForAutoDrop(_services, row.Ticket, TicketAutoDrop.Event.Status);

        var (filename, payload) = TicketSyncService.BuildStatusEvent(row.Ticket.Id, newStatus, _services.CurrentUserName, _services.Cfg.CurrentRole(), at);
        _services.Db.EnqueueTicketOutbox(filename, payload);
        TryFlush();

        _host.ShowStatus($"Статус тикета: {TicketStatus.Label(newStatus)}", category: NotificationCategory.General);
        ReloadGrid();
        // Смена статуса — это ровно то, что должно доехать до остальных быстро: и до коллег в
        // конторе (событие на сетевом диске, TryFlush выше), и до того, кто чинит, — а он видит
        // только хранилище.
        _ = SyncWithStorageAsync();
    }

    private void TryFlush()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) return;
        try { TicketSyncService.FlushOutbox(_services, root); }
        catch { /* stays queued — retried on next Activate() */ }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Activate();
}
