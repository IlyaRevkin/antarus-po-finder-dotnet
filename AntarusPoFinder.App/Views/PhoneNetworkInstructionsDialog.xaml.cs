using System.Windows;
using System.Windows.Documents;
using AntarusPoFinder.App.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>How-to for connecting the actual Осмотр folder as a network path on a phone (SMB), so
/// photos saved there count as "uploaded" immediately with no separate copy/upload step — as
/// opposed to the QR/HTTP uploader (PhotoUploadServer), which still needs an explicit "choose
/// files → отправить" tap each time. Reflects the real setup: the inspection folder is just a
/// subfolder of the same network drive (root_path) every PC already maps as a share, so if that
/// share is reachable from the phone's Wi-Fi, mounting it directly works without any extra
/// server-side support — nothing in this app enables/disables that, it's plain Windows file sharing.</summary>
public partial class PhoneNetworkInstructionsDialog : Window
{
    public PhoneNetworkInstructionsDialog(string inspectionFolder)
    {
        InitializeComponent();
        BuildBody(inspectionFolder);
    }

    private void BuildBody(string inspectionFolder)
    {
        var unc = NetworkPathHelper.TryResolveUnc(inspectionFolder);

        void AddHeading(string text)
        {
            var run = new Run(text) { FontWeight = FontWeights.SemiBold };
            BodyText.Inlines.Add(new LineBreak());
            BodyText.Inlines.Add(new LineBreak());
            BodyText.Inlines.Add(run);
            BodyText.Inlines.Add(new LineBreak());
        }
        void AddLine(string text) => BodyText.Inlines.Add(new Run(text + "\n"));

        BodyText.Inlines.Add(new Run(
            "Приём фото по QR-коду (кнопка рядом) требует каждый раз открывать ссылку и нажимать «Выбрать файлы» → " +
            "«Отправить». Если вместо этого подключить папку осмотра как сетевую папку прямо в файловом менеджере " +
            "телефона, любое сохранённое туда фото сразу оказывается в папке осмотра — без отдельного шага загрузки."));

        AddHeading("Адрес папки");
        if (unc is not null)
        {
            AddLine($"Сетевой путь этой папки: {unc}");
            AddLine("(тот же путь, что и «Открыть папку» выше — просто в сетевом, а не буквенном виде диска)");
        }
        else
        {
            AddLine("Не удалось автоматически определить сетевой адрес — вероятно, папка осмотра не на общем " +
                    "сетевом диске этого ПК, а локальная. Для постоянного подключения с телефона папка должна быть " +
                    "на сетевом диске (том же, где хранится ПО) — уточните адрес у администратора (то, что у него " +
                    "показывается вместо буквы диска Z:, например \\\\сервер\\Software\\...).");
        }

        AddHeading("Android");
        AddLine("1. Установите файловый менеджер с поддержкой SMB/сети, если встроенного «Файлы» недостаточно — " +
                "например CX File Explorer или Solid Explorer.");
        AddLine("2. В разделе «Сеть» / «Network storage» выберите «Добавить сетевую папку» / «Add server» (SMB/CIFS).");
        AddLine("3. Введите адрес папки (см. выше) и, если запросит — логин/пароль пользователя Windows этого ПК " +
                "(или гостевой доступ, если администратор его настроил).");
        AddLine("4. Сохранённое подключение появится как обычная папка — фото, снятые камерой и перемещённые/" +
                "сохранённые туда, сразу видны в списке файлов на компьютере.");

        AddHeading("iPhone / iPad");
        AddLine("1. Откройте приложение «Файлы» → вкладка «Обзор» → «...» (три точки) → «Подключиться к серверу».");
        AddLine("2. Введите smb:// и адрес папки (см. выше), например smb://сервер/Software/... .");
        AddLine("3. Войдите с логином/паролем пользователя Windows этого ПК (или как гость, если разрешено).");
        AddLine("4. Подключённая папка появится в «Обзор» → «Места» — фото можно сохранять туда прямо из галереи " +
                "через «Поделиться» → «Сохранить в Файлы».");

        AddHeading("Важно");
        AddLine("Телефон должен быть в той же Wi-Fi сети, что и этот компьютер (см. подсказку рядом с QR-кодом). " +
                "Если подключиться не получается — сеть/фаервол могут не пропускать SMB между Wi-Fi и проводной " +
                "сетью; в этом случае используйте обычную загрузку по QR-коду.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
