using System;
using System.Globalization;
using System.Windows;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Редактор макета страницы-заглушки «Инструкция в разработке».
///
/// Заглушка — это то, что реально увидит заказчик, наведя телефон на наклейку до того, как
/// инструкцию допишут: наклейку клеят на шкаф заранее, ссылка постоянная, и всё это время по ней
/// открывается ровно эта страница. Значит её текст и вид — вопрос оформления, а не разработки, и
/// менять слово в ней не должно означать выпуск релиза.
///
/// Предпросмотр рисуется ТЕМ ЖЕ кодом, что и настоящий файл (<see cref="InstructionStubWriter.Draw"/>) —
/// на этом уже обжигались с наклейкой («что 97, что 90 ставлю, верх обрезается»): отдельный
/// «примерно такой же» предпросмотр расходится с печатью, и подгонять приходится вслепую.
///
/// Сохранение — отдельной кнопкой: макет общий для всех машин, и случайная правка не должна уезжать
/// коллегам просто потому, что кто-то открыл окно посмотреть.</summary>
public partial class StubLayoutWindow : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private readonly StubLayout _initial;

    /// <summary>Пока поля заполняются из настроек, их TextChanged не должен перерисовывать страницу и
    /// уж тем более читать полузаполненную форму.</summary>
    private bool _filling;

    public StubLayoutWindow(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _initial = services.Cfg.StubLayout();

        PlaceholderHint.Text =
            $"В любую из трёх строк можно вставить {StubLayout.VersionPlaceholder} — подставится номер версии. " +
            "Там, где версия неизвестна (общая папка контроллера), подстановка просто исчезнет.";

        Fill(_initial);
        Redraw();
    }

    private void Fill(StubLayout layout)
    {
        _filling = true;
        try
        {
            TitleInput.Text = layout.Title;
            HintInput.Text = layout.Hint;
            FooterInput.Text = layout.Footer;
            TitleSizeInput.Text = Percent(layout.TitleSize);
            HintSizeInput.Text = Percent(layout.HintSize);
            FooterSizeInput.Text = Percent(layout.FooterSize);
            FrameCheck.IsChecked = layout.ShowFrame;
            ToneInput.Text = layout.MutedTone.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(SampleVersionInput.Text)) SampleVersionInput.Text = "1.0.0005.0001";
        }
        finally { _filling = false; }
    }

    /// <summary>Доли ширины листа показываются процентами: «6» понятнее, чем «0,06», а ошибиться на
    /// порядок сложнее.</summary>
    private static string Percent(double share) => (share * 100).ToString("0.##", CultureInfo.CurrentCulture);

    private StubLayout Current() => new StubLayout
    {
        Title = TitleInput.Text,
        Hint = HintInput.Text,
        Footer = FooterInput.Text,
        TitleSize = Share(TitleSizeInput.Text, _initial.TitleSize),
        HintSize = Share(HintSizeInput.Text, _initial.HintSize),
        FooterSize = Share(FooterSizeInput.Text, _initial.FooterSize),
        ShowFrame = FrameCheck.IsChecked == true,
        MutedTone = Int(ToneInput.Text, _initial.MutedTone),
    }.Sane();

    /// <summary>Недописанное число («0,» посреди набора) не должно ни ронять предпросмотр, ни
    /// схлопывать поле — берём прежнее значение и ждём, пока человек допишет.</summary>
    private static double Share(string text, double fallback) =>
        double.TryParse(text?.Replace('.', ','), NumberStyles.Float, CultureInfo.CurrentCulture, out var percent)
            ? percent / 100
            : fallback;

    private static int Int(string text, int fallback) =>
        int.TryParse(text, out var value) ? value : fallback;

    private void Layout_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        Redraw();
    }

    private void Redraw()
    {
        try
        {
            PreviewImage.Source = InstructionStubWriter.Draw(Current(), SampleVersionInput.Text);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Не нарисовалось: {ex.Message}";
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Fill(StubLayout.Default);
        Redraw();
        StatusText.Text = "Показан вид по умолчанию — чтобы он применился, нажмите «Сохранить».";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var layout = Current();
        _services.Cfg.SetStubLayout(layout);
        _host.ShowStatus("Макет заглушки сохранён. Он общий: новые заглушки на всех машинах будут такими.",
            category: Core.Domain.NotificationCategory.General);

        StatusText.Text = "Сохранено. Уже созданные заглушки не перерисовываются — " +
                          "перезалить их можно на странице «Хранилище» кнопкой «Перезалить всё».";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
