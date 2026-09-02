using System.Globalization;
using System.Linq;
using System.Windows;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Редактор макетов страниц-заглушек — трёх сразу (см. <see cref="StubKind"/>).
///
/// Заглушка — это то, что реально увидит заказчик, наведя телефон на наклейку: наклейку клеят на
/// шкаф заранее, ссылка постоянная, и всё это время по ней открывается ровно эта страница. Значит её
/// текст и вид — вопрос оформления, а не разработки, и менять слово в ней не должно означать выпуск
/// релиза.
///
/// Предпросмотр рисуется ТЕМ ЖЕ кодом, что и настоящий файл (<see cref="InstructionStubWriter.Draw"/>) —
/// на этом уже обжигались с наклейкой («что 97, что 90 ставлю, верх обрезается»): отдельный
/// «примерно такой же» предпросмотр расходится с печатью, и подгонять приходится вслепую.
///
/// Сохранение — отдельной кнопкой: макет общий для всех машин, и случайная правка не должна уезжать
/// коллегам просто потому, что кто-то открыл окно посмотреть. Сохраняются все три сразу: человек
/// переключает страницы в одном окне, и требовать нажатия «Сохранить» на каждой значило бы терять
/// правки при переключении.</summary>
public partial class StubLayoutWindow : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;

    /// <summary>Правки всех трёх страниц, пока их не сохранили. Переключение страницы складывает сюда
    /// текущую форму и достаёт следующую — иначе подгонка одной терялась бы при взгляде на другую.</summary>
    private StubLayoutSet _edited;

    private StubKind _kind = StubKind.InDevelopment;

    /// <summary>Пока поля заполняются из настроек, их TextChanged не должен перерисовывать страницу и
    /// уж тем более читать полузаполненную форму.</summary>
    private bool _filling;

    private sealed record KindChoice(StubKind Kind, string Title)
    {
        public override string ToString() => Title;
    }

    public StubLayoutWindow(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _edited = services.Cfg.StubLayouts();

        PlaceholderHint.Text =
            $"В любую строку можно вставить {StubLayout.VersionPlaceholder} — подставится номер версии " +
            $"(там, где версия неизвестна, подстановка просто исчезнет) и {StubLayout.ServicePlaceholder} — " +
            "подставится общий блок контактов сервиса.";

        _filling = true;
        KindCombo.ItemsSource = StubKinds.All.Select(k => new KindChoice(k, k.Label())).ToList();
        KindCombo.SelectedIndex = 0;
        _filling = false;

        Fill(_edited.For(_kind));
        Redraw();
    }

    /// <summary>Чем эта страница отличается от двух других — одной строкой рядом с выбором. Без неё
    /// три почти одинаковых макета в одном окне неразличимы, а перепутать их дорого: страница
    /// «инструкции не будет» на шкафу, где инструкция готовится, — это отменённое ожидание.</summary>
    private static string HintFor(StubKind kind) => kind switch
    {
        StubKind.NotPlanned =>
            "Одна на всех, лежит в корне диска. Ведут на неё шкафы, у которых в справочнике отмечено «инструкции не будет».",
        StubKind.ServiceNote =>
            "Кладётся РЯДОМ с готовой инструкцией, а не вместо неё.",
        _ =>
            "Лежит вместо инструкции, пока её не дописали, и уходит сама, как только документ появился.",
    };

    private void Fill(StubLayout layout)
    {
        _filling = true;
        try
        {
            TitleInput.Text = layout.Title;
            HintInput.Text = layout.Hint;
            ContactsInput.Text = layout.Contacts;
            FooterInput.Text = layout.Footer;
            TitleSizeInput.Text = Percent(layout.TitleSize);
            HintSizeInput.Text = Percent(layout.HintSize);
            ContactsSizeInput.Text = Percent(layout.ContactsSize);
            FooterSizeInput.Text = Percent(layout.FooterSize);
            FrameCheck.IsChecked = layout.ShowFrame;
            ToneInput.Text = layout.MutedTone.ToString(CultureInfo.InvariantCulture);
            ServiceContactsInput.Text = _edited.ServiceContacts;
            KindHint.Text = HintFor(layout.Kind);
            if (string.IsNullOrWhiteSpace(SampleVersionInput.Text)) SampleVersionInput.Text = "1.0.0005.0001";
        }
        finally { _filling = false; }
    }

    /// <summary>Доли ширины листа показываются процентами: «6» понятнее, чем «0,06», а ошибиться на
    /// порядок сложнее.</summary>
    private static string Percent(double share) => (share * 100).ToString("0.##", CultureInfo.CurrentCulture);

    private StubLayout Current()
    {
        var previous = _edited.For(_kind);
        return new StubLayout
        {
            Kind = _kind,
            Title = TitleInput.Text,
            Hint = HintInput.Text,
            Contacts = ContactsInput.Text,
            Footer = FooterInput.Text,
            TitleSize = Share(TitleSizeInput.Text, previous.TitleSize),
            HintSize = Share(HintSizeInput.Text, previous.HintSize),
            ContactsSize = Share(ContactsSizeInput.Text, previous.ContactsSize),
            FooterSize = Share(FooterSizeInput.Text, previous.FooterSize),
            ShowFrame = FrameCheck.IsChecked == true,
            MutedTone = Int(ToneInput.Text, previous.MutedTone),
        }.Sane();
    }

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
        Remember();
        Redraw();
    }

    /// <summary>Сложить текущую форму в набор. Зовётся на каждой правке и перед переключением
    /// страницы — так набор всегда содержит то, что человек видит.</summary>
    private void Remember() =>
        _edited = _edited.With(_kind, Current()) with { ServiceContacts = ServiceContactsInput.Text };

    private void Kind_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_filling || KindCombo.SelectedItem is not KindChoice choice) return;
        Remember();
        _kind = choice.Kind;
        Fill(_edited.For(_kind));
        Redraw();
    }

    private void Redraw()
    {
        try
        {
            PreviewImage.Source = InstructionStubWriter.Draw(Current(), SampleVersionInput.Text, _edited.Contacts);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Не нарисовалось: {ex.Message}";
        }
    }

    /// <summary>Сбрасывается ТОЛЬКО показанная страница, а не все три: человек подгонял одну и хочет
    /// вернуть одну, а не потерять заодно две соседние.</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _edited = _edited.With(_kind, StubLayout.DefaultFor(_kind));
        Fill(_edited.For(_kind));
        Redraw();
        StatusText.Text = "Показан вид по умолчанию — чтобы он применился, нажмите «Сохранить».";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Remember();
        _services.Cfg.SetStubLayouts(_edited);
        _host.ShowStatus("Макеты заглушек сохранены. Они общие: страницы на всех машинах будут такими.",
            category: Core.Domain.NotificationCategory.General);

        // Раньше здесь стояло «уже созданные заглушки не перерисовываются» — и это было правдой,
        // из-за которой правка макета никуда не доезжала. Теперь у каждой заглушки в метке стоит
        // отпечаток макета (см. InstructionStub), устаревшие перерисовываются сами при обходе диска,
        // а «Перезалить всё» на «Хранилище» перерисовывает их прямо перед отправкой в бакет.
        StatusText.Text = "Сохранено. Лежащие заглушки перерисуются по новому макету — " +
                          "сразу это делает «Хранилище» → «Перезалить всё».";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
