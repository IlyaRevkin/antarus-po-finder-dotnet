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

/// <summary>Страница «Хранилище» — ответ на жалобу: «Хер поймёшь, выгрузилась она
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
    private readonly List<TranslitRow> _translitAll = new();
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
        FilesGrid.ItemsSource = _fileRows;

        // Обзор бакета обновляется сам, пока его вкладка открыта, — кнопки «Обновить» больше нет
        // (13.08.2026). Тик редкий и тихий: в хранилище пишут не каждую секунду, а
        // каждый тик — это запрос по сети, за который платит рабочий канал конторы.
        _filesTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20),
        };
        _filesTimer.Tick += (_, _) => _filesTask = LoadFilesAsync(quiet: true);

        Unloaded += (_, _) => _filesTimer.Stop();
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
        // Вернулись на страницу, оставленную на обзоре бакета, — он тоже обязан быть свежим, а не
        // тем, что показывал полчаса назад.
        if (FilesTab.Visibility == Visibility.Visible) StartFilesWatch();
    }

    // ── Разделы страницы ──────────────────────────────────────────────────────

    /// <summary>Переключение разделов — тем же способом, что и вкладки «Настроек»: активная кнопка
    /// помечается Tag="Active" (NavButton сам подсвечивает её акцентом), содержимое переключается
    /// видимостью. Данные при этом никуда не деваются: и журнал, и список живут в полях страницы,
    /// поэтому переключаться туда-сюда можно посреди прогона.</summary>
    private void Tab_Click(object sender, RoutedEventArgs e) => ShowTab((Button)sender);

    private void ShowTab(Button active)
    {
        foreach (var button in new[] { TabBtnState, TabBtnAccess, TabBtnFiles, TabBtnLog, TabBtnTranslit })
            button.Tag = null;
        active.Tag = "Active";

        StateTab.Visibility = active == TabBtnState ? Visibility.Visible : Visibility.Collapsed;
        AccessTab.Visibility = active == TabBtnAccess ? Visibility.Visible : Visibility.Collapsed;
        FilesTab.Visibility = active == TabBtnFiles ? Visibility.Visible : Visibility.Collapsed;
        LogTab.Visibility = active == TabBtnLog ? Visibility.Visible : Visibility.Collapsed;
        TranslitTab.Visibility = active == TabBtnTranslit ? Visibility.Visible : Visibility.Collapsed;

        // Список файлов запрашивается у хранилища по сети, поэтому сам собой при открытии страницы не
        // грузится — только когда на вкладку правда зашли. Зато с этой минуты он живой: перечитывается
        // при каждом заходе и обновляется по таймеру, пока вкладка открыта.
        if (active == TabBtnFiles) StartFilesWatch();
        else _filesTimer.Stop();

        // Имена для перевода собираются сами при заходе на вкладку — раньше для этого была кнопка, и
        // до её нажатия таблица показывала только уже переопределённые строки, то есть у большинства
        // была пустой.
        if (active == TabBtnTranslit) CollectNames();
    }

    /// <summary>Перечитать обзор бакета и держать его свежим, пока вкладка открыта.</summary>
    private void StartFilesWatch()
    {
        _filesTimer.Start();
        _filesTask = LoadFilesAsync(quiet: _filesEverLoaded);
    }

    /// <summary>Открыть заданный раздел. Нужно тому, кто приводит сюда человека за конкретной
    /// настройкой: кнопка на «Сетевых дисках» обещает «Реквизиты», а страница живёт в кэше и
    /// открылась бы на том разделе, на котором её оставили в прошлый раз.</summary>
    public void ShowSection(string section) => ShowTab(section switch
    {
        HostingSection.Access => TabBtnAccess,
        HostingSection.Files => TabBtnFiles,
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
        public const string Files = "files";
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

        // Обзор бакета доступен и при выключенной выкладке (смотреть и убирать мусор она не мешает),
        // а вот удалять из общего хранилища может только администратор — см. IsAdministrator.
        FilesDeleteBtn.IsEnabled = IsAdministrator;
        if (!IsAdministrator)
            FilesDeleteBtn.ToolTip = "Удалять из общего хранилища может только администратор.";

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
        var words = SearchWords(SearchInput?.Text);
        var wanted = StateFilterCombo?.SelectedIndex switch
        {
            1 => HostingState.Missing,
            2 => HostingState.Published,
            3 => HostingState.NoSource,
            4 => HostingState.Failed,
            5 => HostingState.Unknown,
            _ => (HostingState?)null,
        };
        var onlyShared = OnlySharedCheck?.IsChecked == true;

        _rows.Clear();
        foreach (var item in _items)
        {
            if (wanted is { } state && item.State != state) continue;
            if (onlyShared && !item.Shared) continue;
            if (!Matches(words, item.VersionRaw, item.Where, item.Kind, item.Url, item.Error,
                    item.SourcePath, string.Join(" ", item.SharedWith))) continue;
            _rows.Add(new Row(item));
        }

        var published = _items.Count(i => i.State == HostingState.Published);
        var missing = _items.Count(i => i.State == HostingState.Missing);
        var noSource = _items.Count(i => i.State == HostingState.NoSource);
        var failed = _items.Count(i => i.State == HostingState.Failed);
        var unknown = _items.Count(i => i.State == HostingState.Unknown);
        var shared = _items.Count(i => i.Shared);

        var summary = _items.Count == 0
            ? "Показывать нечего: у версий нет папок инструкций либо не задан путь к диску прошивок."
            : $"Всего {_items.Count}. На хостинге {published}, нет {missing}, нет файла на диске {noSource}, " +
              $"ошибок {failed}, не проверено {unknown}." +
              (shared > 0 ? $" Общих с другим подтипом: {shared}." : "");
        if (_rows.Count != _items.Count)
            summary += $" Отбором показано {_rows.Count}.";
        SummaryText.Text = summary;
    }

    /// <summary>Слова отбора. Их несколько и найтись должны все: «ПЖ SMH5» отбирает пересечение, а не
    /// объединение, — иначе второе слово только расширяло бы выдачу, что человеку в отборе не нужно.</summary>
    private static string[] SearchWords(string? typed) =>
        (typed ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Matches(string[] words, params string?[] fields)
    {
        if (words.Length == 0) return true;
        var haystack = string.Join(" ", fields.Where(f => !string.IsNullOrEmpty(f)));
        return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void Search_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void ResetFilter_Click(object sender, RoutedEventArgs e)
    {
        SearchInput.Text = "";
        StateFilterCombo.SelectedIndex = 0;
        OnlySharedCheck.IsChecked = false;
        ApplyFilter();
    }

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

    /// <summary>«Поправить ссылку вручную…» — сказать, какой файл считать документом этой версии.
    ///
    /// Отдельного поля «ссылка» в базе нет и заводить его нельзя: адрес на
    /// хостинге ПОВТОРЯЕТ путь документа на диске, и он же уходит в QR на наклейке (см.
    /// <see cref="HostingLinkDialog"/>). Поэтому правится не адрес, а документ.
    ///
    /// Главный случай — развести общий документ у прошивки, привязанной к нескольким подтипам шкафа:
    /// пока у «ПЖ / FD» своего руководства нет, оно читается у «ПЖ / 2.0», и здесь ему можно указать
    /// собственное. Правит только администратор — запись уезжает в общий конфиг, то есть меняет ссылку
    /// у всех сразу.</summary>
    private void EditLink_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            AppMessageBox.Show("Выберите строку в списке.", "Хранилище", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IsAdministrator)
        {
            AppMessageBox.Show("Менять документ версии может только администратор.", "Хранилище",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ids = row.Item.VersionIds.Count > 0 ? row.Item.VersionIds : new[] { row.Item.VersionId };
        var targets = ids
            .Select(id => (Id: id, Label: LabelForVersion(id, row.Item.VersionRaw)))
            .ToList();

        var dialog = new HostingLinkDialog(row.Item, targets, _services.Cfg.S3(), _services.Cfg.RootPath())
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true) return;

        foreach (var id in dialog.ChosenIds)
            _services.Db.UpdateFwVersionAttachments(id, instructionsPath: dialog.ChosenPath);

        AppendLog(dialog.ChosenPath.Length == 0
            ? $"Снята ссылка на документ у версий: {string.Join(", ", dialog.ChosenIds)}"
            : $"Документом версий {string.Join(", ", dialog.ChosenIds)} назначен {dialog.ChosenPath}");
        _host.PushCatalogChange("Изменён документ инструкции у версии");
        _host.ShowStatus("Документ версии изменён", category: NotificationCategory.Sync);

        BuildList();
    }

    /// <summary>Подпись записи в списке правки: «тип / подтип / контроллер · версия». Имена берутся из
    /// базы, а не из строки списка: строка схлопнута по общему документу и называет только тот подтип,
    /// в чьей папке документ лежит.</summary>
    private string LabelForVersion(int id, string versionRaw)
    {
        var names = _services.Db.GetFwVersionNames(id);
        var where = names is { } n ? $"{n.GroupName} / {n.SubtypeName} / {n.ControllerName}" : $"версия №{id}";
        return $"{where} · {versionRaw}";
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

    // ── Файлы в хранилище ─────────────────────────────────────────────────────
    // Взаимодействие с файловой системой хранилища прямо во вкладке: посмотреть, что там лежит, и
    // при необходимости удалить что-то вручную — тот же мусор, например.
    //
    // Вкладка «Состояние» отвечает на вопрос «уехало ли то, что должно», и список для неё строится по
    // базе и диску. Здесь всё наоборот: спрашивается у самого бакета, что там лежит. Разница между
    // этими двумя списками — и есть тот самый мусор: объекты, оставшиеся после переименования папки
    // (ключи считаются от имён, а имена правят люди), выкладки с другой машины и ручные опыты. Раньше
    // увидеть их из программы было нельзя вовсе, только через сторонний S3-клиент.

    private readonly ObservableCollection<FileRow> _fileRows = new();
    private List<FileRow> _filesLoaded = new();
    private string _filesPrefix = "";
    private bool _filesEverLoaded;
    private CancellationTokenSource? _filesCancel;
    private readonly System.Windows.Threading.DispatcherTimer _filesTimer;

    /// <summary>Идущий обход бакета — чтобы переход по папкам мог его дождаться, а не начинать
    /// второй поверх первого.</summary>
    private Task _filesTask = Task.CompletedTask;

    /// <summary>Предел на один обход: 50 страниц по 1000 ключей. Не «чтобы не тормозило» — чтобы
    /// ошибка на стороне хостинга (метка продолжения, ведущая на себя же) не крутила запросы вечно.
    /// Упёрлись — говорим об этом прямо в итоговой строке, а не молчим, будто показали всё.</summary>
    private const int MaxFilePages = 50;

    /// <summary>Ключи, которые программа считает своими: то, что она выложила бы сама. Всё остальное
    /// в бакете — «не значится», и именно это человек ищет глазами, когда пришёл убирать мусор.</summary>
    private HashSet<string> KnownKeys() =>
        new(_items.Select(i => i.ObjectKey).Where(k => !string.IsNullOrEmpty(k)), StringComparer.Ordinal);

    /// <summary>Корень обзора — папка внутри бакета из реквизитов. Выше неё не поднимаемся: остальное
    /// в бакете нам не принадлежит (бакет корпоративный, в нём живут и чужие данные).</summary>
    private string RootPrefix()
    {
        var prefix = (_services.Cfg.S3Prefix() ?? "").Trim().Trim('/');
        return prefix.Length == 0 ? "" : prefix + "/";
    }

    /// <summary>Перечитать содержимое текущего адреса в бакете.
    ///
    /// <paramref name="quiet"/> — обновление «само собой» (по таймеру или при возврате на вкладку): в
    /// этом случае таблица не мигает и выделение не пропадает. Раньше список чистился ПЕРЕД запросом,
    /// и на медленной сети вкладка на секунду становилась пустой — терпимо для нажатой кнопки, но не
    /// для обновления, которое человек не заказывал: он в этот момент как раз выбирает строки.</summary>
    private async Task LoadFilesAsync(bool quiet = false)
    {
        var settings = _services.Cfg.S3();
        if (!quiet)
        {
            _fileRows.Clear();
            _filesLoaded = new List<FileRow>();
        }

        if (!settings.HasAddress || !settings.HasCredentials)
        {
            _fileRows.Clear();
            _filesLoaded = new List<FileRow>();
            FilesPathText.Text = "";
            FilesSummaryText.Text = "Хранилище не настроено: нужны адрес, бакет и файл с ключами (вкладка «Реквизиты»).";
            return;
        }
        if (_filesCancel is not null) return;

        var root = RootPrefix();
        if (!_filesPrefix.StartsWith(root, StringComparison.Ordinal)) _filesPrefix = root;

        var flat = FilesFlatCheck.IsChecked == true;
        var known = KnownKeys();
        var rows = new List<FileRow>();
        var truncated = false;

        _filesCancel = new CancellationTokenSource();
        SetFilesBusy(true);
        if (!quiet) FilesSummaryText.Text = "Спрашиваем у хранилища…";
        try
        {
            var client = new S3Client();
            string? token = null;
            var pages = 0;
            do
            {
                var page = await client.ListAsync(settings, _filesPrefix, grouped: !flat, token,
                    ct: _filesCancel.Token);
                if (!page.Ok)
                {
                    FilesSummaryText.Text = $"Не удалось получить список: {page.Error}";
                    AppendLog($"Обзор хранилища ({(_filesPrefix.Length == 0 ? "корень" : _filesPrefix)}): {page.Error}");
                    return;
                }

                foreach (var folder in page.Folders)
                    rows.Add(FileRow.Folder(folder, _filesPrefix, known));
                foreach (var obj in page.Objects)
                {
                    // Объект нулевой длины с именем самой папки — так некоторые клиенты «создают
                    // папку». Строкой он не нужен: это и есть та папка, в которой мы стоим.
                    if (string.Equals(obj.Key, _filesPrefix, StringComparison.Ordinal)) continue;
                    rows.Add(FileRow.File(obj, _filesPrefix, known, S3Client.PublicUrl(settings, obj.Key)));
                }

                token = page.NextToken;
                pages++;
                if (token is not null && pages >= MaxFilePages) { truncated = true; break; }
            }
            while (token is not null);

            _filesEverLoaded = true;
        }
        catch (OperationCanceledException)
        {
            FilesSummaryText.Text = "Обзор остановлен — показано то, что успели получить.";
        }
        finally
        {
            _filesCancel.Dispose();
            _filesCancel = null;
            SetFilesBusy(false);
        }

        _filesLoaded = rows
            .OrderBy(r => r.IsFolder ? 0 : 1)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyFilesFilter(truncated);
    }

    private void ApplyFilesFilter(bool truncated = false)
    {
        var onlyUnknown = FilesOnlyUnknownCheck.IsChecked == true;
        var words = SearchWords(FilesSearchInput?.Text);

        // Что было выделено — то и останется выделенным после тихого обновления по таймеру: строки
        // пересоздаются каждым обходом, и без этого отметки слетали бы сами собой ровно в тот момент,
        // когда человек их расставляет.
        var selected = new HashSet<string>(FilesGrid.SelectedItems.OfType<FileRow>().Select(r => r.Key),
            StringComparer.Ordinal);

        _fileRows.Clear();
        // «..» — подъём на уровень выше строкой в самой таблице двумя точками, как в проводнике
        // (13.08.2026), а не отдельной кнопкой. Отбором она не убирается: строка
        // отбора не должна отрезать путь назад.
        if (_filesPrefix.Length > RootPrefix().Length) _fileRows.Add(FileRow.Up(ParentPrefix()));
        foreach (var row in _filesLoaded)
        {
            if (onlyUnknown && row.Known) continue;
            if (!Matches(words, row.Name, row.Key)) continue;
            _fileRows.Add(row);
        }

        if (selected.Count > 0)
            foreach (var row in _fileRows)
                if (selected.Contains(row.Key))
                    FilesGrid.SelectedItems.Add(row);

        var settings = _services.Cfg.S3();
        var where = _filesPrefix.Length == 0 ? "корень бакета" : _filesPrefix;
        FilesPathText.Text = $"{settings.Bucket} · {where}";

        var folders = _filesLoaded.Count(r => r.IsFolder);
        var files = _filesLoaded.Count(r => !r.IsFolder);
        var unknown = _filesLoaded.Count(r => !r.Known);
        var bytes = _filesLoaded.Where(r => !r.IsFolder).Sum(r => r.Size);

        var parts = new List<string>();
        parts.Add(_filesLoaded.Count == 0
            ? "Здесь пусто."
            : $"Папок {folders}, файлов {files} ({FileRow.Bytes(bytes)}). Не значится у программы: {unknown}.");
        if (onlyUnknown && _fileRows.Count == 0 && _filesLoaded.Count > 0)
            parts.Add("Все показанные объекты программе известны — снимите галочку, чтобы увидеть их.");
        var shown = _fileRows.Count(r => !r.IsUp);
        if (shown != _filesLoaded.Count)
            parts.Add($"Отбором показано {shown}.");
        if (truncated)
            parts.Add($"Показаны первые {MaxFilePages * 1000} объектов — здесь их больше. " +
                      "Зайдите в папку поглубже, чтобы увидеть остальное.");
        FilesSummaryText.Text = string.Join(" ", parts);
    }

    private void SetFilesBusy(bool busy)
    {
        FilesDeleteBtn.IsEnabled = !busy && IsAdministrator;
        FilesUploadBtn.IsEnabled = !busy && IsAdministrator;
        FilesFlatCheck.IsEnabled = !busy;
        FilesStopBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Удаление из общего хранилища — то же самое, что чистка общего диска: это чужая работа,
    /// а не своя машина. Поэтому кнопка администраторская, как и «Чистка диска» в настройках.
    /// Программист, которому страница доступна, смотреть и открывать может, удалять — нет.</summary>
    private bool IsAdministrator => _services.Cfg.CurrentRole() == "administrator";

    private void FilesStop_Click(object sender, RoutedEventArgs e) => _filesCancel?.Cancel();

    private async void FilesFlat_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadFilesAsync();
    }

    private void FilesFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilesFilter();
    }

    /// <summary>Адрес на уровень выше текущего, но не выше корня обзора.</summary>
    private string ParentPrefix()
    {
        var root = RootPrefix();
        if (_filesPrefix.Length <= root.Length) return root;

        var trimmed = _filesPrefix.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var parent = slash < 0 ? "" : trimmed[..(slash + 1)];
        return parent.Length < root.Length ? root : parent;
    }

    /// <summary>Перейти по адресу в бакете (вглубь по папке или вверх по «..»).
    ///
    /// Идущее обновление сначала останавливается и дожидается: с тех пор как список обновляется сам,
    /// клик по папке вполне может прийтись на секунду, когда тихий обход уже начался, — а второй раз
    /// <see cref="LoadFilesAsync"/> не входит и переход просто не срабатывал бы. Дожидаться
    /// обязательно, а не только отменять: иначе прерванный обход дорисовал бы содержимое прежней папки
    /// поверх новой.</summary>
    private async Task GoToPrefixAsync(string prefix)
    {
        _filesPrefix = prefix;
        if (_filesCancel is not null)
        {
            _filesCancel.Cancel();
            try { await _filesTask; } catch (Exception) { /* прерванный обход — обычное дело */ }
        }
        _filesTask = LoadFilesAsync();
        await _filesTask;
    }

    /// <summary>Backspace — на уровень выше, как в проводнике. Тот же смысл, что у строки «..».</summary>
    private async void FilesGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Back) return;
        e.Handled = true;
        await GoToPrefixAsync(ParentPrefix());
    }

    private async void FilesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!DataGridClickGuard.IsOverDataRow(e) || FilesGrid.SelectedItem is not FileRow row) return;
        await OpenRowAsync(row);
    }

    private async void FilesOpen_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileRow row) return;
        await OpenRowAsync(row);
    }

    private async Task OpenRowAsync(FileRow row)
    {
        if (row.IsUp || row.IsFolder)
        {
            await GoToPrefixAsync(row.Key);
            return;
        }
        OpenFileRow(row);
    }

    private void OpenFileRow(FileRow row)
    {
        if (string.IsNullOrEmpty(row.Url)) return;
        try { Process.Start(new ProcessStartInfo(row.Url) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog($"Не удалось открыть ссылку: {ex.Message}"); }
    }

    private void FilesCopyUrl_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard((FilesGrid.SelectedItem as FileRow)?.Url);

    private void FilesCopyKey_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard((FilesGrid.SelectedItem as FileRow)?.Key);

    /// <summary>Удаление отмеченного. Порядок такой же, как у чистки диска: сначала собирается точный
    /// список того, что уйдёт (у папки — все объекты под её адресом, пересчитанные ЗАНОВО, а не по
    /// показанному списку), потом человек его подтверждает, и только потом что-то удаляется.
    ///
    /// Отдельно сказано, сколько среди них тех, что программа считает выложенными: удалить их можно
    /// (файл остаётся на диске и вернётся кнопкой «Выложить недостающее»), но до этой минуты ссылка с
    /// наклейки на них работала, а после — нет.</summary>
    private async void FilesDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAdministrator)
        {
            AppMessageBox.Show("Удалять из хранилища может только администратор.", "Хранилище",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // «..» — не объект хранилища, а способ подняться на уровень выше: удалять по ней нечего.
        var selected = FilesGrid.SelectedItems.OfType<FileRow>().Where(r => !r.IsUp).ToList();
        if (selected.Count == 0)
        {
            AppMessageBox.Show("Выберите строки в списке.", "Хранилище", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_filesCancel is not null) return;

        var settings = _services.Cfg.S3();
        var client = new S3Client();
        var known = KnownKeys();

        _filesCancel = new CancellationTokenSource();
        SetFilesBusy(true);
        List<string> keys;
        try
        {
            keys = await CollectKeysAsync(client, settings, selected, _filesCancel.Token);
        }
        catch (OperationCanceledException)
        {
            keys = new List<string>();
        }
        finally
        {
            _filesCancel.Dispose();
            _filesCancel = null;
            SetFilesBusy(false);
        }

        if (keys.Count == 0)
        {
            FilesSummaryText.Text = "Удалять нечего: под выбранными строками объектов не нашлось.";
            return;
        }

        var knownCount = keys.Count(known.Contains);
        var sample = string.Join("\n• ", keys.Take(8));
        var reply = AppMessageBox.Show(
            $"Удалить из хранилища объектов: {keys.Count}?\n\n• {sample}" +
            (keys.Count > 8 ? $"\n• …и ещё {keys.Count - 8}" : "") + "\n\n" +
            (knownCount > 0
                ? $"Из них {knownCount} программа считает выложенными — ссылки с наклеек на них перестанут открываться, " +
                  "пока их не выложить заново («Выложить недостающее»).\n\n"
                : "") +
            "Удаление безвозвратно: корзины у хранилища нет. Файлы на диске не трогаются.",
            "Удаление из хранилища", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _filesCancel = new CancellationTokenSource();
        SetFilesBusy(true);
        var deleted = 0;
        var failed = 0;
        try
        {
            AppendLog($"— Удаление из хранилища: {keys.Count} объектов —");
            foreach (var key in keys)
            {
                if (_filesCancel.Token.IsCancellationRequested) break;
                var result = await client.DeleteAsync(settings, key, _filesCancel.Token);
                if (result.Ok)
                {
                    deleted++;
                    // Наблюдение «лежит на хостинге» после удаления заведомо ложно: карточка прошивки
                    // и окно QR читают именно его, а не спрашивают сеть на каждую строку выдачи.
                    _services.Db.SaveHostingCheck(key, present: false, "");
                }
                else
                {
                    failed++;
                    AppendLog($"{key}: не удалось удалить — {result.Error}");
                }
                FilesSummaryText.Text = $"Удаляем… {deleted + failed} из {keys.Count}";
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Удаление остановлено вручную. Уже удалённое не вернуть.");
        }
        finally
        {
            _filesCancel.Dispose();
            _filesCancel = null;
            SetFilesBusy(false);
        }

        var summary = $"Удалено {deleted}" + (failed > 0 ? $", не удалось {failed} (подробности в журнале)" : "");
        AppendLog(summary);
        _host.ShowStatus($"Хранилище: {summary}", category: NotificationCategory.Sync);
        await LoadFilesAsync();
        FilesSummaryText.Text = summary + ". " + FilesSummaryText.Text;
    }

    // ── Ручная укладка файла в хранилище ──────────────────────────────────────
    // Возможность подправить руками что угодно — файл или привязки, удалить, загрузить (13.08.2026).
    // Удаление тут было с самого начала, а положить файл обратно было нечем: любая
    // ошибка правилась только через сторонний S3-клиент, которого на рабочей машине нет.
    //
    // Программа НЕ считает такой файл документом какой-либо версии: она узнаёт свои объекты по ключу,
    // а ключ считается от пути на диске (см. HostingSyncService.Plan). Об этом сказано прямо в
    // подтверждении — иначе положенный руками файл выглядел бы как выложенная инструкция.

    private void FilesUpload_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAdministrator)
        {
            AppMessageBox.Show("Класть файлы в общее хранилище может только администратор.", "Хранилище",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать файл для хранилища",
            Multiselect = true,
            Filter = "Все файлы (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true) _ = UploadFilesAsync(dialog.FileNames);
    }

    private void FilesGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsAdministrator && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void FilesGrid_Drop(object sender, DragEventArgs e)
    {
        if (!IsAdministrator) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            _ = UploadFilesAsync(paths);
    }

    private async Task UploadFilesAsync(IReadOnlyList<string> paths)
    {
        var files = paths.Where(p => { try { return File.Exists(p); } catch (Exception) { return false; } }).ToList();
        if (files.Count == 0)
        {
            FilesSummaryText.Text = "Класть в хранилище можно только файлы — папку целиком перетащить нельзя.";
            return;
        }
        if (_filesCancel is not null) return;

        var settings = _services.Cfg.S3();
        var where = _filesPrefix.Length == 0 ? "корень бакета" : _filesPrefix;
        var sample = string.Join("\n• ", files.Select(Path.GetFileName).Take(8));
        var reply = AppMessageBox.Show(
            $"Положить в хранилище файлов: {files.Count}?\n\n• {sample}" +
            (files.Count > 8 ? $"\n• …и ещё {files.Count - 8}" : "") +
            $"\n\nАдрес: {settings.Bucket} · {where}\n\n" +
            "Файл с таким же именем по этому адресу будет заменён. Документом какой-либо версии " +
            "программа его не считает: свои объекты она узнаёт по адресу, который повторяет путь файла " +
            "на диске прошивок.",
            "Загрузка в хранилище", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _filesCancel = new CancellationTokenSource();
        SetFilesBusy(true);
        var client = new S3Client();
        int done = 0, failed = 0;
        try
        {
            AppendLog($"— Загрузка в хранилище: {files.Count} файлов в {where} —");
            foreach (var path in files)
            {
                if (_filesCancel.Token.IsCancellationRequested) break;
                var key = _filesPrefix + Path.GetFileName(path);
                var result = await client.PutFileAsync(settings, key, path, _filesCancel.Token);
                if (result.Ok)
                {
                    done++;
                    // Наблюдение «лежит на хостинге» правится и здесь: если этим файлом закрыли дыру
                    // по правильному адресу, карточка обязана это увидеть, не дожидаясь проверки.
                    _services.Db.SaveHostingCheck(key, present: true, result.Url ?? "");
                }
                else
                {
                    failed++;
                    AppendLog($"{key}: не удалось положить — {result.Error}");
                }
                FilesSummaryText.Text = $"Кладём… {done + failed} из {files.Count}";
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Загрузка остановлена вручную.");
        }
        catch (Exception ex)
        {
            AppendLog($"Загрузка сорвалась: {ex.Message}");
        }
        finally
        {
            _filesCancel.Dispose();
            _filesCancel = null;
            SetFilesBusy(false);
        }

        var summary = $"Положено {done}" + (failed > 0 ? $", не удалось {failed} (подробности в журнале)" : "");
        AppendLog(summary);
        _host.ShowStatus($"Хранилище: {summary}", category: NotificationCategory.Sync);
        await LoadFilesAsync();
        FilesSummaryText.Text = summary + ". " + FilesSummaryText.Text;
    }

    /// <summary>«Чей это документ» — перейти на «Состояние» с отбором по этому объекту. Именно переход,
    /// а не всплывающее окно с ответом: узнав версию, человек тут же хочет с ней что-то сделать —
    /// открыть, выложить заново, поправить ссылку, — и всё это живёт на той вкладке.</summary>
    private void FilesWhose_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileRow row || row.IsUp)
        {
            AppMessageBox.Show("Выберите строку в списке.", "Хранилище", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var owner = _items.FirstOrDefault(i => string.Equals(i.ObjectKey, row.Key, StringComparison.Ordinal));
        if (owner is null && !row.IsFolder)
        {
            AppMessageBox.Show(
                $"Этот объект программе неизвестен — ни одна версия не считает его своим документом.\n\n{row.Key}\n\n" +
                "Обычно так остаются файлы после переименования папок на диске: адрес считается от имён, " +
                "а имена правят люди. Такой объект можно удалить — на диске он не тронут.",
                "Чей это документ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // У папки точной строки нет — отбираем по её адресу: покажет всё, что лежит в этой ветке.
        SearchInput.Text = owner?.Url ?? row.Name;
        StateFilterCombo.SelectedIndex = 0;
        OnlySharedCheck.IsChecked = false;
        ShowTab(TabBtnState);
        ApplyFilter();
        if (_rows.Count > 0)
        {
            ItemsGrid.SelectedItem = _rows[0];
            ItemsGrid.ScrollIntoView(_rows[0]);
        }
    }

    /// <summary>Что именно уйдёт. У файла — он сам, у папки — всё, что лежит под её адресом, спрошенное
    /// у хранилища прямо сейчас: показанный список мог устареть, а «удалить папку» одним запросом S3 не
    /// умеет — папок у него нет.</summary>
    private static async Task<List<string>> CollectKeysAsync(S3Client client, Core.Services.S3Settings settings,
        IReadOnlyList<FileRow> rows, CancellationToken ct)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!row.IsFolder)
            {
                if (seen.Add(row.Key)) keys.Add(row.Key);
                continue;
            }

            string? token = null;
            var pages = 0;
            do
            {
                var page = await client.ListAsync(settings, row.Key, grouped: false, token, ct: ct);
                if (!page.Ok) throw new InvalidOperationException(page.Error);
                foreach (var obj in page.Objects)
                    if (seen.Add(obj.Key)) keys.Add(obj.Key);
                token = page.NextToken;
                pages++;
            }
            while (token is not null && pages < MaxFilePages);
        }

        return keys;
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

    /// <summary>Таблица написаний живёт в двух списках: <c>_translitAll</c> — всё, что есть (он и
    /// сохраняется), <c>_translit</c> — то, что показано после отбора. Разделять обязательно: сохранять
    /// показанное значило бы терять переопределения, отрезанные строкой отбора.</summary>
    private void LoadTranslit()
    {
        var map = _services.Cfg.Translit();
        _translitAll.Clear();
        foreach (var (source, latin) in map.Overrides.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            _translitAll.Add(new TranslitRow(source, latin, manual: true));
        CollectNames();
    }

    /// <summary>Собрать имена, у которых перевод вообще нужен: справочник иерархии (типы, подтипы,
    /// контроллеры) и постоянные папки раскладки. Имена без кириллицы («SMH5», номера версий) в
    /// таблицу не попадают — переводить там нечего, и строки-пустышки только мешали бы найти те, что
    /// действительно стоит проверить глазами.</summary>
    private void CollectNames()
    {
        var known = new HashSet<string>(_translitAll.Select(r => r.Source), StringComparer.OrdinalIgnoreCase);
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

        foreach (var name in names.Where(Transliteration.HasCyrillic).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!known.Add(name)) continue;
            _translitAll.Add(new TranslitRow(name, Transliteration.Auto(name), manual: false));
        }

        UpdateTranslitHint();
        ApplyTranslitFilter();

        var manual = _translitAll.Count(IsManual);
        TranslitHintText.Text = $"Имён с кириллицей: {_translitAll.Count}" +
                                (manual > 0 ? $", из них задано вручную: {manual}." : ". Все переводятся сами.");
    }

    /// <summary>Написание отличается от автоперевода, то есть его задали руками. Считается сравнением, а
    /// не флагом строки: строку могли только что поправить в таблице, и флаг из момента загрузки врал
    /// бы отбору.</summary>
    private static bool IsManual(TranslitRow row) =>
        !string.IsNullOrWhiteSpace(row.Latin)
        && !string.Equals(row.Latin.Trim(), Transliteration.Auto(row.Source), StringComparison.Ordinal);

    private void TranslitFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyTranslitFilter();
    }

    private void ApplyTranslitFilter()
    {
        var words = SearchWords(TranslitSearchInput?.Text);
        var onlyManual = TranslitOnlyManualCheck?.IsChecked == true;

        _translit.Clear();
        foreach (var row in _translitAll.OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase))
        {
            if (onlyManual && !IsManual(row)) continue;
            if (!Matches(words, row.Source, row.Latin)) continue;
            _translit.Add(row);
        }
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
        // Перебирается ПОЛНЫЙ список, а не показанный: строка отбора не должна стирать переопределения,
        // которых в этот момент не видно.
        var pairs = _translitAll
            .Where(IsManual)
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
        foreach (var row in _translitAll)
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

        /// <summary>Другие подтипы, у которых тот же самый документ. Пусто — документ только этой
        /// версии; так у подавляющего большинства строк, и столбец им не мешает.</summary>
        public string SharedWith => string.Join(", ", Item.SharedWith);

        public string SizeLabel => Item.Size is not { } bytes
            ? ""
            : bytes >= 1024 * 1024
                ? $"{bytes / 1024d / 1024d:0.#} МБ"
                : $"{Math.Max(1, bytes / 1024)} КБ";
    }

    /// <summary>Строка обзора бакета: либо «папка» (общий префикс ключей — самих папок у S3 нет),
    /// либо объект. Имя показывается коротким, от текущего адреса, а ключ — целиком: искать глазами
    /// удобно по имени, а решать «точно ли этот» — только по ключу.</summary>
    private sealed class FileRow
    {
        public bool IsFolder { get; private init; }

        /// <summary>Строка «..» — не объект бакета, а подъём на уровень выше (в Key лежит адрес
        /// родителя). Отличать её обязательно: удалять, отбирать и считать её нельзя.</summary>
        public bool IsUp { get; private init; }

        public string Key { get; private init; } = "";
        public string Name { get; private init; } = "";
        public long Size { get; private init; }
        public string Url { get; private init; } = "";
        public bool Known { get; private init; }
        public DateTime? Modified { get; private init; }

        public string KindLabel => IsUp ? "" : IsFolder ? "папка" : "файл";
        public string KnownLabel => IsUp ? "" : Known ? "значится" : "не значится";
        public string SizeLabel => IsFolder || IsUp ? "" : Bytes(Size);
        public string ModifiedLabel => Modified?.ToString("dd.MM.yyyy HH:mm") ?? "";

        /// <summary>Подъём на уровень выше — первой строкой таблицы, как в проводнике.</summary>
        public static FileRow Up(string parentPrefix) => new()
        {
            IsUp = true,
            IsFolder = true,
            Key = parentPrefix,
            Name = "..",
            // «Значится» — чтобы строка не пропадала при отборе «только те, что не значатся»: путь
            // назад отбором отрезать нельзя (ApplyFilesFilter добавляет её до перебора, но и здесь
            // ошибиться не стоит).
            Known = true,
        };

        public static FileRow Folder(string prefix, string parent, IReadOnlySet<string> known) => new()
        {
            IsFolder = true,
            Key = prefix,
            Name = Shorten(prefix, parent),
            // Папка «значится», если программа собирается класть в неё хоть что-то: у самой папки
            // ключа нет, сравнивать нечего.
            Known = known.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)),
        };

        public static FileRow File(S3Client.BucketObject obj, string parent, IReadOnlySet<string> known, string url) => new()
        {
            Key = obj.Key,
            Name = Shorten(obj.Key, parent),
            Size = obj.Size,
            Modified = obj.Modified,
            Url = url,
            Known = known.Contains(obj.Key),
        };

        /// <summary>Имя от текущего адреса. При показе «всё вложенное списком» это путь с папками —
        /// так и надо: иначе одинаковые «instruction.pdf» из разных версий не отличить.</summary>
        private static string Shorten(string key, string parent)
        {
            var name = key.StartsWith(parent, StringComparison.Ordinal) ? key[parent.Length..] : key;
            return name.TrimEnd('/');
        }

        public static string Bytes(long bytes) => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.#} МБ"
            : bytes > 0 ? $"{Math.Max(1, bytes / 1024)} КБ" : "0";
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
