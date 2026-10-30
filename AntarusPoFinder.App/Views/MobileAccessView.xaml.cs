using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;
using QRCoder;

namespace AntarusPoFinder.App.Views;

/// <summary>ВКЛЮЧИТЬ МОБИЛЬНЫЙ ДОСТУП И ПОКАЗАТЬ АДРЕС. Страница только бета-сборки (см.
/// RolesConfig.BetaNavItems) — в рабочей программе её нет вовсе.
///
/// Разбор того, почему страницу раздаёт сам Finder по локальной сети, а не хостинг, и какие решения
/// за Ильёй, — в docs/mobile-access-plan.md. Разметка телефона — MobileSite (Core), транспорт —
/// MobileAccessServer.
///
/// <b>База читается ЭТИМ потоком.</b> Сервер зовёт отрисовку из своего потока, а SQLite-соединение у
/// Database одно и без блокировок, поэтому вызов тут же перебрасывается в Dispatcher. Запросов с
/// телефона единицы, и цена переброски нулевая — а вот гонка за одним соединением стоила бы порчи
/// базы.
///
/// <b>Сервер переживает уход со страницы.</b> Он привязан не к вкладке (как приём фото в «Осмотре»),
/// а к галке в профиле: человек включил доступ один раз, и телефон достучится, пока открыта
/// программа. Иначе «зайти с телефона» означало бы «сначала найди и открой нужную вкладку на
/// компьютере», то есть ровно ту возню, от которой уходили.</summary>
public partial class MobileAccessView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;

    /// <summary>Один на всё приложение: страница кэшируется, но даже если бы пересоздавалась, второй
    /// слушатель на том же порту просто не поднялся бы.</summary>
    private static MobileAccessServer? _server;

    public MobileAccessView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        EnabledCheck.IsChecked = _services.Cfg.MobileAccessEnabled();
        Apply(_services.Cfg.MobileAccessEnabled());
    }

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        // Пока страница ещё не загрузилась, Checked прилетает от присваивания в Load() — сохранять
        // в этот момент нечего, и состояние уже и так применяется там же.
        if (!IsLoaded) return;

        var on = EnabledCheck.IsChecked == true;
        _services.Cfg.SetMobileAccessEnabled(on);
        Apply(on);
    }

    private void Apply(bool on)
    {
        if (!on)
        {
            _server?.Dispose();
            _server = null;
            UrlBox.Text = "";
            QrImage.Source = null;
            CopyButton.IsEnabled = false;
            ResetButton.IsEnabled = false;
            StateText.Text = "Выключено — программа сеть не слушает.";
            return;
        }

        CopyButton.IsEnabled = true;
        ResetButton.IsEnabled = true;

        try
        {
            _server ??= new MobileAccessServer(
                _services.Cfg.MobileServerPort(),
                _services.Cfg.MobileAccessToken(),
                Render);
        }
        catch (System.Net.Sockets.SocketException)
        {
            UrlBox.Text = "";
            QrImage.Source = null;
            StateText.Text = $"Порт {_services.Cfg.MobileServerPort()} занят — измените «mobile_server_port» в настройках.";
            return;
        }

        UrlBox.Text = _server.Url;
        StateText.Text = _server.Url.StartsWith("http://127.0.0.1", System.StringComparison.Ordinal)
            ? "Сети нет — с телефона этот адрес не откроется."
            : "Откройте адрес в браузере телефона и добавьте в закладки — дальше код сканировать не нужно.";
        ShowQr(_server.Url);
    }

    /// <summary>Мост между потоком сервера и базой. Исключение здесь нельзя выпускать наружу: оно
    /// ушло бы в поток сокета, где его никто не ловит, и уронило бы приложение целиком из-за одного
    /// кривого запроса с телефона.</summary>
    private MobileSite.Page Render(string path, string query)
    {
        try
        {
            return Dispatcher.Invoke(() => MobileSite.Render(_services.Db, path, query, "/" + _server!.Token));
        }
        catch (System.Exception ex)
        {
            return new MobileSite.Page(500,
                $"<!doctype html><meta charset=\"utf-8\"><p>Ошибка: {MobileSite.Esc(ex.Message)}</p>");
        }
    }

    private void ShowQr(string url)
    {
        var qr = new QRCodeGenerator().CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var bytes = new PngByteQRCode(qr).GetGraphic(16);
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        QrImage.Source = bmp;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) =>
        ClipboardSafe.TrySetTextWithNote(UrlBox.Text, this, _host, "Адрес скопирован");

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Выдать новый адрес? Прежние закладки на телефонах перестанут работать.",
                "Мобильный доступ", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _services.Cfg.ResetMobileAccessToken();
        // Токен зашит в уже поднятый сервер, поэтому его недостаточно перечитать — слушателя надо
        // поднять заново, иначе старая ссылка продолжила бы работать.
        _server?.Dispose();
        _server = null;
        Apply(EnabledCheck.IsChecked == true);
        _host.ShowStatus("Адрес мобильного доступа заменён");
    }
}
