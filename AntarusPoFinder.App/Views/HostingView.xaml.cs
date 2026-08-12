using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Страница «Хранилище» — ответ на дословную жалобу владельца: «Хер поймёшь, выгрузилась она
/// нет, надо добавить механизм чтобы было видно отгружена ли она на внешнее хранилище, добавить
/// кнопки синхронизация, возможность видеть процесс синхронизации если она в фоне, больше
/// возможностей для дебага и модерации».
///
/// До этого выкладка была побочным действием загрузки версии: получилось — хорошо, не получилось —
/// строчка в списке предупреждений, которую никто не читал, и узнать постфактум, лежит документ на
/// хостинге или нет, было неоткуда вовсе.
///
/// Три вкладки закрывают ровно эти три просьбы: «Состояние» — что есть и чего нет, с кнопками
/// выкладки и полосой прогресса; «Журнал» — почему не получилось, целиком, с кнопкой проверки
/// доступа; «Написание в адресах» — справочник транслита, от которого зависит, попадёт ли наклейка с
/// QR в выложенный файл.
///
/// Долгие операции идут в фоновом потоке с отменой: обход сотни версий с запросом на каждую — это
/// минуты, и замирающее окно в этот момент уже проходили (см. «приложение зависает, когда в фоне
/// что-то делает»).</summary>
public partial class HostingView : UserControl
{
    private readonly AppServices _services;
    private readonly MainWindowViewModel _host;

    private readonly ObservableCollection<Row> _rows = new();
    private readonly ObservableCollection<TranslitRow> _translit = new();
    private List<HostingItem> _items = new();
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cancel;

    public HostingView(AppServices services, MainWindowViewModel host)
    {
        _services = services;
        _host = host;
        InitializeComponent();

        ItemsGrid.ItemsSource = _rows;
        TranslitGrid.ItemsSource = _translit;
        RefreshIfActive();
    }

    /// <summary>Страницы живут в кэше между переходами (MainWindowViewModel._pageCache), поэтому
    /// свежие данные страница обязана забирать сама при возврате на неё — иначе останется с тем, что
    /// показывала полчаса назад.</summary>
    public void RefreshIfActive()
    {
        ShowStorageState();
        LoadAccess();
        LoadTranslit();
        BuildList();
    }

    // ── Разделы страницы ──────────────────────────────────────────────────────

    /// <summary>Переключение разделов — тем же способом, что и вкладки «Настроек»: активная кнопка
    /// помечается Tag="Active" (NavButton сам подсвечивает её акцентом), содержимое переключается
    /// видимостью. Данные при этом никуда не деваются: и журнал, и список живут в полях страницы,
    /// поэтому переключаться туда-сюда можно посреди прогона.</summary>
    private void Tab_Click(object sender, RoutedEventArgs e) => ShowTab((Button)sender);

    private void ShowTab(Button active)
    {
        foreach (var button in new[] { TabBtnState, TabBtnAccess, TabBtnLog, TabBtnTranslit })
            button.Tag = null;
        active.Tag = "Active";

        StateTab.Visibility = active == TabBtnState ? Visibility.Visible : Visibility.Collapsed;
        AccessTab.Visibility = active == TabBtnAccess ? Visibility.Visible : Visibility.Collapsed;
        LogTab.Visibility = active == TabBtnLog ? Visibility.Visible : Visibility.Collapsed;
        TranslitTab.Visibility = active == TabBtnTranslit ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Открыть заданный раздел. Нужно тому, кто приводит сюда человека за конкретной
    /// настройкой: кнопка на «Сетевых дисках» обещает «Реквизиты», а страница живёт в кэше и
    /// открылась бы на том разделе, на котором её оставили в прошлый раз.</summary>
    public void ShowSection(string section) => ShowTab(section switch
    {
        HostingSection.Access => TabBtnAccess,
        HostingSection.Log => TabBtnLog,
        HostingSection.Translit => TabBtnTranslit,
        _ => TabBtnState,
    });

    /// <summary>Имена разделов для перехода со стороны — строкой, потому что зовущий (страница
    /// «Сетевые диски») знает только IAppHost, а не саму страницу.</summary>
    public static class HostingSection
    {
        public const string State = "state";
        public const string Access = "access";
        public const string Log = "log";
        public const string Translit = "translit";
    }

    // ── Состояние хранилища ───────────────────────────────────────────────────

    private void ShowStorageState()
    {
        var s = _services.Cfg.S3();

        StorageStateText.Text = !s.HasAddress
            ? "Адрес хранилища не задан — выкладывать некуда. Вкладка «Реквизиты» рядом."
            : !s.HasCredentials
                ? "Ключи доступа не загружены — перетащите файл с ключами на вкладке «Реквизиты». До этого выкладка не делается."
                : !s.Enabled
                    ? "Выкладка выключена галочкой на вкладке «Реквизиты». Сами реквизиты на месте."
                    : "Выкладка настроена и включена.";

        var limit = _services.Cfg.HostingMaxFileMb();
        var mode = _services.Cfg.HostingSizeLimitHard() ? "не выкладываются" : "выкладываются с предупреждением";
        // Пустой веб-адрес — не редкость, и строка «адрес для ссылок » с пустотой на конце читается
        // как поломка вёрстки, а не как «не задан».
        var webUrl = string.IsNullOrWhiteSpace(s.WebUrl) ? "не задан (вкладка «Реквизиты»)" : s.WebUrl;
        StorageAddressText.Text =
            $"{s.Endpoint} · бакет {s.Bucket} · регион {s.Region} · адрес для ссылок: {webUrl}\n" +
            $"Предел размера файла {limit} МБ, файлы сверх предела {mode}. " +
            $"Переопределений написания в адресах: {s.Translit.Count}.";

        // Поля предела заполняются здесь же, но без срабатывания обработчиков: иначе открытие
        // страницы выглядело бы для синхронизации как правка настройки.
        _fillingLimit = true;
        try
        {
            MaxSizeInput.Text = limit.ToString();
            LimitModeCombo.SelectedIndex = _services.Cfg.HostingSizeLimitHard() ? 0 : 1;
        }
        finally { _fillingLimit = false; }

        var ready = s.CanPublish;
        CheckBtn.IsEnabled = ready;
        PublishMissingBtn.IsEnabled = ready;
        PublishSelectedBtn.IsEnabled = ready;
        RepublishAllBtn.IsEnabled = ready;
    }

    /// <summary>Пока поля заполняются из настроек, их события не должны считаться правкой человека.</summary>
    private bool _fillingLimit;

    /// <summary>Сохранение предела и режима. Настройка ОБЩАЯ — уезжает на все машины, поэтому правка
    /// объявляется накопителю синхронизации, как и любое другое изменение справочников: иначе на
    /// соседней машине предел остался бы прежним, а человек считал бы, что поменял его для всех.</summary>
    private void HostingLimit_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingLimit) return;

        var before = (_services.Cfg.HostingMaxFileMb(), _services.Cfg.HostingSizeLimitHard());

        if (int.TryParse(MaxSizeInput.Text.Trim(), out var mb) && mb > 0)
            _services.Cfg.SetHostingMaxFileMb(mb);
        _services.Cfg.SetHostingSizeLimitHard(LimitModeCombo.SelectedIndex != 1);

        var after = (_services.Cfg.HostingMaxFileMb(), _services.Cfg.HostingSizeLimitHard());
        if (before == after)
        {
            // Ничего не поменялось (поле потеряло фокус без правки, ввели тот же предел) — молчим.
            ShowStorageState();
            return;
        }

        _host.PushCatalogChange($"Предел размера файла на хостинге: {after.Item1} МБ, " +
                                (after.Item2 ? "сверх предела не выкладывать" : "сверх предела предупреждать"));
        LimitSavedText.Text = "Сохранено";
        ShowStorageState();
        BuildList();
    }

    private void HostingLimit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        HostingLimit_Changed(sender, e);
        // Снимаем фокус, чтобы Enter вёл себя как «применил и закончил», а не оставлял курсор в поле.
        System.Windows.Input.Keyboard.ClearFocus();
    }

    // ── Реквизиты ─────────────────────────────────────────────────────────────
    // Переехали сюда со страницы «Сетевые диски»: адрес хранилища и ключи к нему — это и есть
    // хранилище, а на «Сетевых дисках» они лежали рядом с путями к сетевым папкам, к которым
    // отношения не имеют. Сохранение — по уходу фокуса и по Enter, отдельной кнопки «Сохранить» на
    // страницах приложения нет нигде.

    private void LoadAccess()
    {
        S3EndpointInput.Text = _services.Cfg.S3Endpoint();
        S3BucketInput.Text = _services.Cfg.S3Bucket();
        S3RegionInput.Text = _services.Cfg.S3Region();
        S3PrefixInput.Text = _services.Cfg.S3Prefix();
        S3PublishCheck.IsChecked = _services.Cfg.S3Publish();
        WebUrlInput.Text = _services.Cfg.InstructionBaseUrl();
        RefreshS3Keys();
        RefreshS3Status();
        RefreshWebUrlHint();
    }

    private void S3Field_LostFocus(object sender, RoutedEventArgs e) => SaveS3Fields();

    private void S3Field_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SaveS3Fields();
    }

    private void SaveS3Fields()
    {
        var changed =
            Save(S3EndpointInput.Text, _services.Cfg.S3Endpoint(), _services.Cfg.SetS3Endpoint) |
            Save(S3BucketInput.Text, _services.Cfg.S3Bucket(), _services.Cfg.SetS3Bucket) |
            Save(S3RegionInput.Text, _services.Cfg.S3Region(), _services.Cfg.SetS3Region) |
            Save(S3PrefixInput.Text, _services.Cfg.S3Prefix(), _services.Cfg.SetS3Prefix);

        if (!changed) return;

        // Адрес, бакет и регион пустыми не бывают: стёртое поле возвращает предустановленный адрес
        // компании (см. ConfigService.PresetKeys). Поля перечитываются, чтобы в них было видно то,
        // что реально сохранено, а не пустота, которой в настройках уже нет.
        var restored = S3EndpointInput.Text.Trim().Length == 0 || S3BucketInput.Text.Trim().Length == 0
                       || S3RegionInput.Text.Trim().Length == 0;
        S3EndpointInput.Text = _services.Cfg.S3Endpoint();
        S3BucketInput.Text = _services.Cfg.S3Bucket();
        S3RegionInput.Text = _services.Cfg.S3Region();

        _host.ShowStatus(restored
            ? "Реквизиты хранилища сохранены; пустые поля вернулись к предустановленным"
            : "Реквизиты хранилища сохранены", category: NotificationCategory.Sync);
        RefreshS3Status();
        // Ключи объектов считаются от адреса и папки в бакете — список после правки заведомо устарел.
        ShowStorageState();
        BuildList();

        static bool Save(string typed, string current, Action<string> set)
        {
            var value = typed.Trim();
            if (string.Equals(value, current, StringComparison.Ordinal)) return false;
            set(value);
            return true;
        }
    }

    // ── Ключи доступа: файлом, а не руками ────────────────────────────────────
    // Ключи выдаёт хостинг готовым файлом (просьба Ивана Герасимова от 06.08.2026 — «чтобы просто
    // файл туда перенести»). Полей ввода здесь нет намеренно: сорок случайных символов,
    // перепечатанные руками, дают опечатку, которая молчит до первой выкладки, — а файл либо
    // разбирается, либо честно говорит, что в нём не то.

    private void S3Secrets_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => PickS3SecretsFile();

    private void S3SecretsBrowse_Click(object sender, RoutedEventArgs e) => PickS3SecretsFile();

    private void S3Secrets_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void S3Secrets_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            ApplyS3SecretsFile(paths[0]);
    }

    private void PickS3SecretsFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать файл с ключами доступа к хранилищу",
            // «Все файлы» первым фильтром: имя и расширение файла с ключами задаёт хостинг, и файл
            // без расширения (так их отдаёт часть панелей) не должен оказаться невидимым в диалоге.
            Filter = "Все файлы (*.*)|*.*|Файлы с ключами (*.txt;*.csv;*.json;*.env)|*.txt;*.csv;*.json;*.env",
        };
        if (dlg.ShowDialog() == true) ApplyS3SecretsFile(dlg.FileName);
    }

    /// <summary>Читает и применяет файл с ключами. Всё, что может пойти не так (папку перетащили,
    /// файл занят, внутри не то), заканчивается понятной строкой в состоянии карточки, а не
    /// исключением: это настройка, которую делает не программист.</summary>
    private void ApplyS3SecretsFile(string path)
    {
        string content;
        try
        {
            if (Directory.Exists(path))
            {
                S3Status.Text = "Это папка. Нужен сам файл с ключами, который прислал хостинг.";
                return;
            }
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                S3Status.Text = "Файл не найден.";
                return;
            }
            if (info.Length > S3SecretsFile.MaxReasonableBytes)
            {
                S3Status.Text = "Файл слишком большой для файла с ключами — похоже, это не он.";
                return;
            }
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            S3Status.Text = $"Не удалось прочитать файл: {ex.Message}";
            return;
        }

        var parsed = S3SecretsFile.Parse(content);
        if (!parsed.Ok)
        {
            S3Status.Text = parsed.Error!;
            return;
        }

        _services.Cfg.SetS3AccessKey(parsed.AccessKey);
        _services.Cfg.SetS3SecretKey(parsed.SecretKey);

        // Адрес/бакет/регион в файле бывают не всегда, но если есть — они точнее того, что стоит по
        // умолчанию: файл выдаёт тот же, кто выдал ключи. Пустые значения не затирают заполненные.
        var extras = new List<string>();
        Apply(parsed.Endpoint, _services.Cfg.S3Endpoint(), _services.Cfg.SetS3Endpoint, "адрес хранилища");
        Apply(parsed.Bucket, _services.Cfg.S3Bucket(), _services.Cfg.SetS3Bucket, "бакет");
        Apply(parsed.Region, _services.Cfg.S3Region(), _services.Cfg.SetS3Region, "регион");

        S3EndpointInput.Text = _services.Cfg.S3Endpoint();
        S3BucketInput.Text = _services.Cfg.S3Bucket();
        S3RegionInput.Text = _services.Cfg.S3Region();

        RefreshS3Keys();
        RefreshS3Status();
        ShowStorageState();
        _host.ShowStatus("Ключи доступа к хранилищу сохранены", category: NotificationCategory.Sync);

        if (extras.Count > 0)
            S3Status.Text += $" Из файла также взято: {string.Join(", ", extras)}.";
        if (parsed.OrderGuessed)
            S3Status.Text += " В файле не было подписей, какой ключ какой, — порядок определён по " +
                             "виду ключей. Если проверка доступа не пройдёт, ключи в файле шли наоборот.";

        void Apply(string value, string current, Action<string> set, string what)
        {
            if (value.Length == 0 || string.Equals(value, current, StringComparison.Ordinal)) return;
            set(value);
            extras.Add(what);
        }
    }

    private void S3SecretsClear_Click(object sender, RoutedEventArgs e)
    {
        if (!_services.Cfg.S3().HasCredentials) return;
        _services.Cfg.SetS3AccessKey("");
        _services.Cfg.SetS3SecretKey("");
        RefreshS3Keys();
        RefreshS3Status();
        ShowStorageState();
        _host.ShowStatus("Ключи доступа к хранилищу убраны", category: NotificationCategory.Sync);
    }

    /// <summary>Что написано в зоне перетаскивания. Access Key ID показывается целиком — он не
    /// секрет и по нему видно, те ли ключи стоят; секретный ключ не показывается никогда, включая
    /// «первые символы»: по ним ключ не опознать, а утечь через плечо они вполне могут.</summary>
    private void RefreshS3Keys()
    {
        var s3 = _services.Cfg.S3();
        S3SecretsClearButton.IsEnabled = s3.HasCredentials;

        S3SecretsLabel.Text = s3.HasCredentials
            ? $"Ключи загружены\nAccess Key ID: {s3.AccessKey}\nСекретный ключ сохранён.\n" +
              "Чтобы заменить — перетащите сюда новый файл"
            : "Перетащите сюда файл с ключами доступа,\nкоторый прислал хостинг,\nили нажмите, чтобы выбрать его";
    }

    private void S3Publish_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetS3Publish(S3PublishCheck.IsChecked == true);
        RefreshS3Status();
        ShowStorageState();
    }

    /// <summary>Строка под кнопкой: в каком состоянии выкладка ПРЯМО СЕЙЧАС. Главный случай, ради
    /// которого она есть, — «адрес есть, ключей нет»: это не поломка, а ожидание файла с ключами, и
    /// человек должен видеть именно это, а не «ничего не настроено».</summary>
    private void RefreshS3Status()
    {
        var s3 = _services.Cfg.S3();
        S3CheckButton.IsEnabled = s3.HasAddress && s3.HasCredentials;

        if (!s3.HasAddress)
        {
            S3Status.Text = "Не настроено — укажите адрес хранилища и бакет.";
            return;
        }
        if (!s3.HasCredentials)
        {
            S3Status.Text = "Осталось загрузить файл с ключами доступа — до этого инструкции на хостинг не выкладываются.";
            return;
        }
        if (string.IsNullOrWhiteSpace(s3.WebUrl))
        {
            S3Status.Text = "Ключи есть, но не задан веб-адрес диска инструкций (ниже) — без него в QR-код нечего положить.";
            return;
        }
        S3Status.Text = s3.Enabled
            ? "Настроено — копия инструкции уходит на хостинг при загрузке версии."
            : "Реквизиты заполнены, но выкладка выключена галочкой выше.";
    }

    private async void S3Check_Click(object sender, RoutedEventArgs e)
    {
        SaveS3Fields();

        var s3 = _services.Cfg.S3();
        S3CheckButton.IsEnabled = false;
        S3Status.Text = "Проверяем…";
        try
        {
            var result = await new S3Client().CheckAsync(s3);
            S3Status.Text = result.Ok
                ? "Доступ есть — хранилище отвечает, ключи подходят. Право на запись проверится первой выложенной инструкцией."
                : $"Не получилось: {result.Error}";
            AppendLog(result.Ok ? $"Доступ есть: {result.Url}" : $"Доступа нет: {result.Error}");
        }
        finally
        {
            S3CheckButton.IsEnabled = true;
        }
    }

    // ── Веб-адрес диска инструкций ────────────────────────────────────────────
    // Та же настройка, что и в Настройки → Печать (instruction_base_url), намеренно показанная в двух
    // местах: без неё ссылке на хостинг неоткуда взяться, и она же решает, что зашивать в QR.

    private void WebUrl_LostFocus(object sender, RoutedEventArgs e) => SaveWebUrl();

    private void WebUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SaveWebUrl();
    }

    private void SaveWebUrl()
    {
        var url = (WebUrlInput.Text ?? "").Trim().TrimEnd('/');
        if (string.Equals(url, _services.Cfg.InstructionBaseUrl(), StringComparison.Ordinal)) return;

        _services.Cfg.SetInstructionBaseUrl(url);
        WebUrlInput.Text = _services.Cfg.InstructionBaseUrl();
        _host.ShowStatus(url.Length == 0
            ? $"Поле очищено — вернулся предустановленный адрес: {_services.Cfg.InstructionBaseUrl()}"
            : $"Веб-адрес диска инструкций сохранён: {url}", category: NotificationCategory.Sync);

        RefreshWebUrlHint();
        RefreshS3Status();
        ShowStorageState();
        BuildList();
    }

    // Пустым адрес больше не бывает (стёртое поле возвращает предустановку), поэтому подсказка «не
    // задан — в QR пойдёт сетевой путь» ушла: рассказывать про состояние, в которое настройка уже не
    // приходит, значит врать.
    private void RefreshWebUrlHint() =>
        WebUrlHint.Text = "Эта же настройка показана в Настройки → Печать. Пустым адрес не остаётся: "
                          + "стёртое поле вернёт предустановленный адрес компании.";

    private void OpenWebUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = (WebUrlInput.Text ?? "").Trim();
        if (url.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { WebUrlHint.Text = $"Не удалось открыть: {ex.Message}"; }
    }

    // ── Список ────────────────────────────────────────────────────────────────

    private void BuildList()
    {
        var settings = _services.Cfg.S3();
        var root = _services.Cfg.RootPath();

        // Построение списка в сеть не ходит — только база и диск (см. HostingSyncService.Plan).
        _items = new HostingSyncService(_services.Db).Plan(settings, root).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var onlyProblems = OnlyProblemsCheck.IsChecked == true;
        _rows.Clear();
        foreach (var item in _items)
        {
            if (onlyProblems && item.State == HostingState.Published) continue;
            _rows.Add(new Row(item));
        }

        var published = _items.Count(i => i.State == HostingState.Published);
        var missing = _items.Count(i => i.State == HostingState.Missing);
        var noSource = _items.Count(i => i.State == HostingState.NoSource);
        var failed = _items.Count(i => i.State == HostingState.Failed);
        var unknown = _items.Count(i => i.State == HostingState.Unknown);

        SummaryText.Text = _items.Count == 0
            ? "Показывать нечего: у версий нет папок инструкций либо не задан путь к диску прошивок."
            : $"Всего {_items.Count}. На хостинге {published}, нет {missing}, нет файла на диске {noSource}, " +
              $"ошибок {failed}, не проверено {unknown}.";
    }

    private void OnlyProblems_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    // ── Долгие операции ───────────────────────────────────────────────────────

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Проверка на хостинге", async (progress, ct) =>
        {
            var service = new HostingSyncService(_services.Db);
            var checkedItems = await service.CheckAsync(_services.Cfg.S3(), _items, progress, ct);
            _items = checkedItems.ToList();
            RememberChecks();

            foreach (var item in _items.Where(i => i.State == HostingState.Failed))
                AppendLog($"{item.VersionRaw}: не удалось проверить — {item.Error}");

            return $"Проверено {_items.Count}: на хостинге {_items.Count(i => i.State == HostingState.Published)}, " +
                   $"нет {_items.Count(i => i.State == HostingState.Missing)}.";
        });
    }

    private async void PublishMissing_Click(object sender, RoutedEventArgs e) =>
        await PublishAsync(_items, onlyMissing: true, "Выкладка недостающего");

    private async void PublishSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemsGrid.SelectedItems.OfType<Row>().Select(r => r.Item).ToList();
        if (selected.Count == 0)
        {
            AppMessageBox.Show("Выберите строки в списке.", "Хранилище", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await PublishAsync(selected, onlyMissing: false, "Выкладка выбранного");
    }

    private async void RepublishAll_Click(object sender, RoutedEventArgs e)
    {
        var reply = AppMessageBox.Show(
            $"Выложить заново все {_items.Count} документов, включая уже лежащие на хостинге?\n\n" +
            "Нужно, когда документы правили на диске, а на хостинге осталась прошлая редакция. " +
            "На большом диске это займёт заметное время — прогон можно остановить, ничего не сломав.",
            "Перезалить всё", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        await PublishAsync(_items, onlyMissing: false, "Перезаливка всего");
    }

    private async Task PublishAsync(IReadOnlyList<HostingItem> items, bool onlyMissing, string title)
    {
        var root = _services.Cfg.RootPath();
        var settings = _services.Cfg.S3();

        await RunAsync(title, async (progress, ct) =>
        {
            var service = new HostingSyncService(_services.Db, client: null, new Services.DocxToPdfConverter.Adapter());
            var result = await Task.Run(() => service.Publish(settings, root, items, onlyMissing, progress, ct), ct);

            foreach (var message in result.Messages) AppendLog(message);

            // После выкладки состояние строк заведомо изменилось — перепроверяем правдой, а не
            // догадкой «раз отправили, значит лежит»: ровно этой догадкой страница и была бы
            // бесполезна.
            var rechecked = await service.CheckAsync(settings, _items, progress, ct);
            _items = rechecked.ToList();
            RememberChecks();

            return $"Выложено {result.Published}, пропущено {result.Skipped}, не удалось {result.Failed}.";
        });
    }

    /// <summary>Сохранить наблюдения, чтобы карточка прошивки и окно QR могли показать «на хостинге»
    /// сразу, без запроса к сети на каждую строку выдачи (см. Database.HostingChecks). Строки, по
    /// которым проверка не удалась, в кэш НЕ пишутся: «не смогли спросить» — это не «нет».</summary>
    private void RememberChecks()
    {
        foreach (var item in _items)
        {
            if (item.State is not (HostingState.Published or HostingState.Missing)) continue;
            _services.Db.SaveHostingCheck(item.ObjectKey, item.State == HostingState.Published, item.Url);
        }
    }

    /// <summary>Общая обвязка длинной операции: кнопки гаснут, появляется прогресс и «Остановить»,
    /// по завершении список перерисовывается, а итог уходит и в строку внизу, и в журнал.
    /// Отмена — это штатный исход, а не ошибка: прогон на сотни файлов человек имеет право
    /// прекратить, и ничего при этом не ломается (уже выложенное остаётся выложенным).</summary>
    private async Task RunAsync(string title, Func<IProgress<HostingProgress>, CancellationToken, Task<string>> work)
    {
        if (_cancel is not null) return;

        _cancel = new CancellationTokenSource();
        SetBusy(true);
        AppendLog($"— {title} —");

        var progress = new Progress<HostingProgress>(p =>
        {
            Progress.Value = p.Percent;
            ProgressText.Text = $"{p.Done} из {p.Total} · {p.What}";
        });

        try
        {
            var summary = await work(progress, _cancel.Token);
            AppendLog(summary);
            _host.ShowStatus($"{title}: {summary}", category: NotificationCategory.Sync);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Прогон остановлен вручную. Уже выложенное осталось на хостинге.");
            _host.ShowStatus($"{title}: остановлено", category: NotificationCategory.Sync);
        }
        catch (Exception ex)
        {
            AppendLog($"Сорвалось: {ex.Message}");
            AppMessageBox.Show($"{title} не удалась: {ex.Message}", "Хранилище",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            SetBusy(false);
            ApplyFilter();
        }
    }

    private void SetBusy(bool busy)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = busy ? ProgressText.Text : "";
        CheckBtn.IsEnabled = !busy;
        PublishMissingBtn.IsEnabled = !busy;
        PublishSelectedBtn.IsEnabled = !busy;
        RepublishAllBtn.IsEnabled = !busy;
        if (!busy) ShowStorageState();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();

    // ── Действия по строке ────────────────────────────────────────────────────

    private Row? Selected => ItemsGrid.SelectedItem as Row;

    private void ItemsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl_Click(sender, e);

    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || string.IsNullOrEmpty(row.Item.Url)) return;
        try { Process.Start(new ProcessStartInfo(row.Item.Url) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog($"Не удалось открыть ссылку: {ex.Message}"); }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected?.Item.Url);

    private void CopyKey_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected?.Item.ObjectKey);

    private void ShowOnDisk_Click(object sender, RoutedEventArgs e)
    {
        var path = Selected?.Item.SourcePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog($"Не удалось показать файл: {ex.Message}"); }
    }

    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _host.ShowStatus("Скопировано", category: NotificationCategory.General);
        }
        catch (Exception ex) { AppendLog($"Буфер обмена занят: {ex.Message}"); }
    }

    // ── Журнал ────────────────────────────────────────────────────────────────

    private void AppendLog(string line)
    {
        _log.AppendLine($"{DateTime.Now:HH:mm:ss}  {line}");
        LogBox.Text = _log.ToString();
        LogBox.ScrollToEnd();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopyToClipboard(_log.ToString());

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        LogBox.Text = "";
    }

    private async void CheckAccess_Click(object sender, RoutedEventArgs e)
    {
        var settings = _services.Cfg.S3();
        AppendLog("Проверка доступа к хранилищу…");
        var result = await new S3Client().CheckAsync(settings);
        AppendLog(result.Ok
            ? $"Доступ есть: {result.Url}"
            : $"Доступа нет: {result.Error}");
    }

    // ── Написание в адресах ───────────────────────────────────────────────────

    private void LoadTranslit()
    {
        var map = _services.Cfg.Translit();
        _translit.Clear();
        foreach (var (source, latin) in map.Overrides.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            _translit.Add(new TranslitRow(source, latin, manual: true));
        UpdateTranslitHint();
    }

    /// <summary>Собрать имена, у которых перевод вообще нужен: справочник иерархии (типы, подтипы,
    /// контроллеры) и постоянные папки раскладки. Имена без кириллицы («SMH5», номера версий) в
    /// таблицу не попадают — переводить там нечего, и строки-пустышки только мешали бы найти те, что
    /// действительно стоит проверить глазами.</summary>
    private void CollectNames_Click(object sender, RoutedEventArgs e)
    {
        var known = new HashSet<string>(_translit.Select(r => r.Source), StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        foreach (var g in _services.Db.GetAllEquipmentGroups()) names.Add(g.Name);
        foreach (var s in _services.Db.GetAllEquipmentSubtypes())
        {
            if (s.Name != "—") names.Add(s.Name);
            if (!string.IsNullOrWhiteSpace(s.FolderName)) names.Add(s.FolderName);
        }
        foreach (var c in _services.Db.GetAllControllerModels()) names.Add(c.Name);
        names.Add(VersionLayout.FirmwareFolderName);
        names.AddRange(VersionLayout.SlotFolderNames);
        names.Add(HierarchyFolders.Opc);
        names.Add(HierarchyFolders.Passports);

        var added = 0;
        foreach (var name in names.Where(Transliteration.HasCyrillic).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!known.Add(name)) continue;
            _translit.Add(new TranslitRow(name, Transliteration.Auto(name), manual: false));
            added++;
        }

        UpdateTranslitHint();
        TranslitHintText.Text = added > 0
            ? $"Добавлено имён: {added}. Поправьте нужные и нажмите «Сохранить»."
            : "Новых имён с кириллицей не нашлось — все уже в таблице.";
    }

    private void TranslitGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(UpdateTranslitHint));

    private void ResetRow_Click(object sender, RoutedEventArgs e)
    {
        if (TranslitGrid.SelectedItem is not TranslitRow row) return;
        row.Latin = Transliteration.Auto(row.Source);
        row.Origin = "автоперевод";
        TranslitGrid.Items.Refresh();
        UpdateTranslitHint();
    }

    private void SaveTranslit_Click(object sender, RoutedEventArgs e)
    {
        // В справочник уходят ТОЛЬКО отличия от автоперевода. Записывать совпадающие значило бы
        // намертво зафиксировать сегодняшнюю таблицу звучаний: поправь её потом — и старые записи
        // молча продолжили бы переопределять новое поведение.
        var pairs = _translit
            .Where(r => !string.IsNullOrWhiteSpace(r.Latin))
            .Where(r => !string.Equals(r.Latin.Trim(), Transliteration.Auto(r.Source), StringComparison.Ordinal))
            .Select(r => new KeyValuePair<string, string>(r.Source, r.Latin.Trim()));

        var map = TranslitMap.FromPairs(pairs);
        _services.Cfg.SetTranslit(map);
        _host.PushCatalogChange("Изменено написание имён в адресах на хостинге");

        LoadTranslit();
        ShowStorageState();
        BuildList();
        TranslitHintText.Text = $"Сохранено. Переопределений: {map.Count}.";
    }

    private void UpdateTranslitHint()
    {
        foreach (var row in _translit)
        {
            var auto = Transliteration.Auto(row.Source);
            row.Origin = string.Equals(row.Latin?.Trim(), auto, StringComparison.Ordinal) ? "автоперевод" : "задано вручную";
            row.Example = $"…/{(string.IsNullOrWhiteSpace(row.Latin) ? auto : row.Latin.Trim())}/…";
        }
        TranslitGrid.Items.Refresh();
    }

    // ── Строки таблиц ─────────────────────────────────────────────────────────

    /// <summary>Обёртка над <see cref="HostingItem"/> для DataGrid: сама модель — неизменяемая запись
    /// из ядра, а таблице нужны готовые к показу подписи.</summary>
    private sealed class Row
    {
        public Row(HostingItem item) => Item = item;

        public HostingItem Item { get; }
        public string StateLabel => Item.StateLabel;
        public string VersionRaw => Item.VersionRaw;
        public string Where => Item.Where;
        public string Kind => Item.Kind;
        public string Url => Item.Url;
        public string? Error => Item.Error;

        public string SizeLabel => Item.Size is not { } bytes
            ? ""
            : bytes >= 1024 * 1024
                ? $"{bytes / 1024d / 1024d:0.#} МБ"
                : $"{Math.Max(1, bytes / 1024)} КБ";
    }

    private sealed class TranslitRow
    {
        public TranslitRow(string source, string latin, bool manual)
        {
            Source = source;
            Latin = latin;
            Origin = manual ? "задано вручную" : "автоперевод";
            Example = $"…/{latin}/…";
        }

        public string Source { get; }
        public string Latin { get; set; }
        public string Origin { get; set; }
        public string Example { get; set; }
    }
}
