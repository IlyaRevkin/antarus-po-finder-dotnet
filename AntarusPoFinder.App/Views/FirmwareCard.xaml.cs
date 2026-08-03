using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Что именно есть у этой конкретной записи — считает SearchView (он один знает про диск,
/// локальный кэш и БД), карточка только рисует. Отдельным типом, а не десятком bool-аргументов:
/// параметров стало столько, что позиционный вызов перестал читаться.</summary>
public sealed record FirmwareCardFlags
{
    /// <summary>Локально лежит ИМЕННО эта версия.</summary>
    public bool HasLocal { get; init; }

    /// <summary>Локально лежит хоть какая-то версия этой прошивки (тогда речь про обновление, а не
    /// про первую загрузку — влияет только на текст статуса).</summary>
    public bool HasAnyLocal { get; init; }

    public bool HasParams { get; init; }
    public bool HasHmi { get; init; }

    /// <summary>Есть РЕАЛЬНЫЙ файл карты ВВ / инструкции / карты Modbus — проверено обходом диска, а не
    /// просто заполненным путём в БД (тот мог указывать на версию, где файла уже нет — отсюда была
    /// жалоба «кнопка есть, а открывать нечего»). Считается в фоне (SearchView.ScanVersionFolder): до
    /// конца обхода все три false, пункты появляются в «Ещё» по мере готовности. Открывается всегда
    /// самый свежий файл в общей папке документа, а не путь конкретной версии.</summary>
    public bool HasIoMap { get; init; }
    public bool HasInstructions { get; init; }
    public bool HasModbus { get; init; }

    /// <summary>У инструкции есть исходный документ Word (.docx/.doc) — можно предложить «Редактировать
    /// инструкцию». Считается тем же обходом, что и HasInstructions (см. SearchView.ResolveInstruction).</summary>
    public bool HasInstructionDocx { get; init; }

    /// <summary>Инструкцию есть чем печатать — готовый PDF рядом ЛИБО docx, из которого PDF соберётся по
    /// первому требованию (см. DocxToPdfConverter). От этого зависят пункты «…для печати (PDF)» и «Печать».</summary>
    public bool HasInstructionPrintable { get; init; }

    /// <summary>Расширение файла, который реально откроет «Открыть прошивку ПЛК» — считает
    /// PlcOpenResolver при обходе диска, тот же резолвер и открывает. Пишется на кнопке в скобках для
    /// ЛЮБОГО проекта, не только .psl/.lfs. null — обход ещё не дошёл до этой карточки, откроется
    /// папка либо файл без расширения: тогда кнопка без скобок, пустые скобки хуже.</summary>
    public string? PlcOpenExtension { get; init; }

    /// <summary>То же самое для кнопки «Открыть HMI проект» — расширение файла панели, который реально
    /// откроется (считает HmiOpenResolver при обходе диска). null — обход не дошёл, панели нет, или
    /// откроется папка проекта: тогда кнопка без расширения.</summary>
    public string? HmiOpenExtension { get; init; }

    /// <summary>Доступен Automation-компонент Segnetics Loader. Кнопка загрузки остаётся видимой и
    /// при его отсутствии, чтобы по нажатию показать оператору точную причину.</summary>
    public bool LoaderConnected { get; init; }

    /// <summary>Нашёлся .lfs / .psl.</summary>
    public bool HasLfs { get; init; }
    public bool HasPsl { get; init; }

    /// <summary>Бывают ли у этой версии .psl/.lfs вообще (SegneticsProject.IsRelevant). У шкафа на
    /// KINCO их не бывает, и «LFS —» там означало бы потерянный файл вместо «не про эту версию».</summary>
    public bool IsSegnetics { get; init; }

    /// <summary>Обход диска (LFS/PSL/HMI/карта) ещё идёт — карточка уже нарисована, но про файлы
    /// рядом с версией пока ничего не известно. Нужен, чтобы «нет LFS» не показывалось секунду как
    /// факт, пока сетевую папку ещё читают (см. SearchView.ScanDiskFlagsAsync).</summary>
    public bool DiskScanPending { get; init; }

    public bool CanEditTags { get; init; }

    /// <summary>Включена ли автосинхронизация локальной копии (Настройки → Общие). От неё зависит
    /// только начальный текст статуса — пункт ручной синхронизации в меню есть всегда.</summary>
    public bool AutoSync { get; init; }

    /// <summary>Папку версии на диске не нашли (и локальной копии нет). Раньше такую карточку молча
    /// УБИРАЛИ с экрана («найдено 0, скрыто отсутствующих на диске»), из-за чего реальная прошивка
    /// «пропадала» из выдачи, стоило переименовать hw/папку на диске или назвать её нестандартно —
    /// любой промах определения присутствия молча прятал живой результат. Теперь карточку не прячем,
    /// а помечаем: спрятать реальную прошивку хуже, чем показать её с честным предупреждением, что
    /// файлов для открытия по сохранённому пути может не быть. Считается фоновым обходом
    /// (SearchView.ScanDiskFlagsAsync), поэтому до конца обхода всегда false.</summary>
    public bool DiskMissing { get; init; }

    /// <summary>Правки этой прошивки (теги/описание/типы пуска) ещё лежат в накопителе синхры и не
    /// уехали на общий диск — коллеги их пока не видят. Машинно-локальный признак: истинен только на
    /// той машине, где правку сделали, и только пока «Отправить всё» не унесло накопитель на диск
    /// (см. Database.GetPendingSubjectKeys, SearchView заполняет по FwVersionId). Ровно ответ на
    /// вопрос оператора «а этот тег уже синхронизирован или нет».</summary>
    public bool TagsPending { get; init; }
}

/// <summary>One search-result card. Кнопок стало слишком много для одного ряда, поэтому основными
/// остались только те, ради которых карточку открывают (открыть ПЛК/HMI, параметры, инструкции,
/// загрузка в контроллер), а остальное убрано в меню «Ещё» — см. Configure.</summary>
public partial class FirmwareCard : UserControl
{
    public HierarchyResult Result { get; private set; } = null!;

    public event EventHandler? OpenFolderRequested;
    /// <summary>Открыть папку версии ИМЕННО на сетевом диске (не локальную копию) — чтобы наладчик мог
    /// вручную почистить лишние файлы (напр. несколько .lfs в одной папке), которые правит модерация.</summary>
    public event EventHandler? OpenServerFolderRequested;
    /// <summary>Открыть прошивку ПЛК / HMI-проект. Какой именно файл открывается — решает SearchView
    /// (подсказка исполняемого файла у записи, отдельная папка HMI-проекта, старый детект по
    /// расширениям); карточка про эти варианты не знает и рисует по одной кнопке на каждый.</summary>
    public event EventHandler? OpenPlcRequested;
    public event EventHandler? OpenHmiRequested;
    public event EventHandler? OpenLfsRequested;
    public event EventHandler? OpenPslRequested;
    /// <summary>Ручная синхронизация локальной копии с диском — раньше была основной кнопкой
    /// «Синхронизировать»/«Обновить», теперь запасной вариант в меню (обычно копия подтягивается
    /// сама, см. SearchView.AutoSyncMissing).</summary>
    public event EventHandler? DownloadRequested;
    public event EventHandler? LoaderRequested;
    public event EventHandler? MapRequested;
    public event EventHandler? ModbusMapRequested;
    public event EventHandler? ParamsRequested;
    /// <summary>Открыть инструкцию как есть — запасной вариант для легаси-файла, у которого нет ни docx,
    /// ни pdf (открывается самый свежий файл папки).</summary>
    public event EventHandler? InstructionsRequested;
    public event EventHandler? OpenInstructionFolderRequested;
    /// <summary>Открыть исходный docx для правки. После сохранения PDF пересоберётся при следующем
    /// открытии «для печати»/«Печать» — см. SearchView.EnsureInstructionPdfAsync.</summary>
    public event EventHandler? EditInstructionRequested;
    /// <summary>Открыть PDF инструкции (собрать из docx, если тот правили).</summary>
    public event EventHandler? OpenInstructionPdfRequested;
    /// <summary>Отправить PDF инструкции на принтер по умолчанию.</summary>
    public event EventHandler? PrintInstructionRequested;
    public event EventHandler? HistoryRequested;
    public event EventHandler? CopyNameRequested;
    public event EventHandler? TagsEditRequested;

    /// <summary>Сколько самых коротких тегов показывать на карточке до сворачивания в «показать все».
    /// Небольшое число: смысл — уместить теги в одну-две строки, а не занимать полкарточки списком
    /// названий шкафов, которым подходит одна прошивка.</summary>
    private const int TagsCollapseAfter = 3;

    /// <summary>Полный набор тегов текущей записи — для окна «показать все теги» (TagsView показывает
    /// свёрнутый ряд).</summary>
    private IReadOnlyList<string> _allTags = Array.Empty<string>();

    public FirmwareCard()
    {
        InitializeComponent();
        MorePopup.Opened += MorePopup_Opened;
        MorePopup.Closed += MorePopup_Closed;
        // Подписка один раз в конструкторе, а не в Configure: Configure зовётся несколько раз на одну
        // карточку (первая отрисовка + досмотр диска), и подписка там копила бы обработчики.
        TagsView.ShowAllRequested += (_, _) => ShowAllTags();
    }

    private void ShowAllTags()
    {
        if (_allTags.Count == 0) return;
        var list = string.Join("\n", _allTags.Select(t => "•  " + t));
        AppMessageBox.Show(list, $"Теги — {Result.Name} {Result.VersionRaw}".Trim(),
            MessageBoxButton.OK, MessageBoxImage.None);
    }

    public void Configure(HierarchyResult result, FirmwareCardFlags flags)
    {
        Result = result;

        NameLabel.Text = result.Name;
        VersionLabel.Text = result.VersionRaw;
        VersionLabel.ToolTip =
            "Формат версии: eq_prefix.sub_prefix.hw.sw.ГГГГММДД_ЧЧММ\n" +
            ".PSL — исходный проект, .LFS — скомпилированный файл";

        var metaParts = new List<string>();
        if (!string.IsNullOrEmpty(result.Controller)) metaParts.Add($"Контроллер: {result.Controller}");
        if (!string.IsNullOrEmpty(result.EquipmentType)) metaParts.Add(result.EquipmentType);
        if (!string.IsNullOrEmpty(result.WorkType)) metaParts.Add(result.WorkType);
        if (result.UploadDate is not null) metaParts.Add(result.UploadDate.Value.ToString("dd.MM.yyyy"));
        // «По такому же запросу эту версию уже ставили N раз» — то, из-за чего она стоит выше
        // остальных (см. Database.FwUsage.cs). Без этой строки подъём выглядел бы необъяснимым.
        if (result.UsageCount > 0)
            metaParts.Add(result.UsageCount == 1
                ? "по этому запросу выбирали 1 раз"
                : $"по этому запросу выбирали {result.UsageCount} раз");
        MetaLabel.Text = string.Join("  ·  ", metaParts);

        // Read-only display here — editing tags (and description/launch types together) happens
        // through the single "Теги" button below, not inline, to avoid two competing tag editors
        // on the same card.
        var tags = TagString.Parse(result.Tags);
        _allTags = tags;
        // Свёрнуто: у прошивки, подходящей десятку шкафов, все теги-названия занимали полкарточки.
        // Показываем несколько самых коротких, остальное — за «показать все теги (N)» (см. ShowAllTags).
        TagsView.Configure(tags, null, readOnly: true, collapseAfter: TagsCollapseAfter);
        TagsView.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // «Синхронизирован ли этот тег» — правки этой прошивки ещё в накопителе, на диск не ушли
        // (машинно-локально, только у того, кто правил). Скруглённые стрелки + акцент — та же
        // семантика «ждёт отправки», что у пилюли синхры вверху.
        TagsPendingLabel.Visibility = flags.TagsPending ? Visibility.Visible : Visibility.Collapsed;
        if (flags.TagsPending)
            TagsPendingLabel.Text = "⟳ Ваши правки этой прошивки ещё не на диске — коллеги их пока не видят. «Отправить всё» вверху.";

        SoftwareNameLabel.Text = $"{result.Name} {result.VersionRaw}".Trim();

        ShowFilesLine(result, flags);

        ActionsPanel.Children.Clear();
        MorePanel.Children.Clear();

        // ── Основной ряд ───────────────────────────────────────────────────
        // Первой кнопкой — либо «Загрузить в ПЛК», либо «Открыть прошивку ПЛК», но не обе сразу:
        //   • загрузка показывается, когда у версии есть LFS или PSL; доступность Automation
        //     проверяется при нажатии, а PSL при необходимости собирает сам Loader;
        //   • во всех остальных случаях — «Открыть прошивку ПЛК (.ext)»: расширение файла, который
        //     реально откроется, пишется прямо на кнопке, и не только для .psl/.lfs, а для любого
        //     проекта — считает его тот же PlcOpenResolver, который потом и открывает.
        // Дальше — HMI-проект и параметры отдельными кнопками, если есть. Всё прочее (второй файл
        // пары, открыть проект при заливке, папка, документация, история) — в «Ещё».
        var openExt = flags.PlcOpenExtension;
        var showLoad = flags.HasLfs || flags.HasPsl;

        if (showLoad)
        {
            var loadBtn = MakeActionButton("Загрузить в ПЛК",
                (_, _) => LoaderRequested?.Invoke(this, EventArgs.Empty));
            loadBtn.ToolTip = flags.LoaderConnected
                ? "Загрузить найденный LFS или собрать и загрузить PSL через Segnetics Loader"
                : "Segnetics Loader Automation не найден; при нажатии будет показана причина";
            ActionsPanel.Children.Add(loadBtn);
        }
        else
        {
            var plcBtn = MakeActionButton(
                openExt is null ? "Открыть прошивку ПЛК" : $"Открыть прошивку ПЛК ({openExt})",
                (_, _) => OpenPlcRequested?.Invoke(this, EventArgs.Empty));
            plcBtn.ToolTip = PrimaryOpenTooltip(result, flags, openExt);
            ActionsPanel.Children.Add(plcBtn);
        }

        if (flags.HasHmi)
        {
            // Панель может быть унаследована от прошлой версии программы ПЛК (её обновляли, HMI —
            // нет, см. FirmwareUploadService). Тогда честнее сразу сказать, от какой именно версии
            // проект, а не делать вид, что он собран вместе с этой.
            var hmiFrom = HmiSourceVersion(result);
            var hmiBtn = MakeActionButton($"Открыть HMI проект{HmiButtonSuffix(flags.HmiOpenExtension, hmiFrom)}",
                (_, _) => OpenHmiRequested?.Invoke(this, EventArgs.Empty));
            var hmiTips = new List<string>();
            if (hmiFrom is not null) hmiTips.Add($"HMI-проект от версии {hmiFrom} — в этой версии панель не обновляли");
            if (!string.IsNullOrEmpty(result.HmiExecutableHint)) hmiTips.Add($"Исполняемый файл: {result.HmiExecutableHint}");
            if (hmiTips.Count > 0) hmiBtn.ToolTip = string.Join("\n", hmiTips);
            ActionsPanel.Children.Add(hmiBtn);
        }

        if (flags.HasParams)
            ActionsPanel.Children.Add(MakeActionButton("Параметры", (_, _) => ParamsRequested?.Invoke(this, EventArgs.Empty)));

        // ── Меню «Ещё»: всё остальное, по разделам ────────────────────────
        AddMenuHeader("Файлы версии");
        AddMenuItem("Открыть папку с файлами", () => OpenFolderRequested?.Invoke(this, EventArgs.Empty));
        // Папка на сетевом диске, а не локальная копия — чтобы вручную почистить лишние файлы (напр.
        // несколько .lfs пожарных шкафов в одной папке). Пункт есть, только когда путь на диске
        // записан: у прошивки без папки на сервере открывать нечего.
        if (!string.IsNullOrEmpty(result.FirmwareDir))
            AddMenuItem("Открыть папку на сервере", () => OpenServerFolderRequested?.Invoke(this, EventArgs.Empty),
                "Папка версии на сетевом диске (не локальная копия) — чтобы вручную поправить или удалить лишние файлы");
        // Когда основная кнопка — «Загрузить», «открыть прошивку» остаётся доступной здесь.
        if (showLoad)
            AddMenuItem(openExt is null ? "Открыть прошивку ПЛК" : $"Открыть прошивку ПЛК ({openExt})",
                () => OpenPlcRequested?.Invoke(this, EventArgs.Empty),
                "Открыть проект/файл для просмотра, без заливки в контроллер");
        // Второй файл пары Segnetics — тот, что не открывается основной кнопкой. Пункт добавляется,
        // только если файл реально есть (у KINCO и т.п. .lfs/.psl не бывает вовсе).
        if (flags.IsSegnetics)
        {
            if (flags.HasPsl && openExt != ".psl")
                AddMenuItem("Открыть проект (PSL)", () => OpenPslRequested?.Invoke(this, EventArgs.Empty),
                    "Исходный проект SMLogix — открывают, когда нужно править");
            if (flags.HasLfs && openExt != ".lfs")
                AddMenuItem("Открыть прошивку (LFS)", () => OpenLfsRequested?.Invoke(this, EventArgs.Empty),
                    "Скомпилированный файл, который заливается в контроллер");
        }
        // Запасного пункта «Загрузить в ПЛК» в меню нет: при наличии LFS/PSL это основная кнопка.
        AddMenuItem("Обновить локальную копию с диска", () => DownloadRequested?.Invoke(this, EventArgs.Empty),
            "Скопировать версию с сетевого диска заново — если автосинхронизация выключена или не удалась");

        // Пункт есть, только когда РЕАЛЬНО есть что открыть (флаг посчитан обходом диска, а не по
        // заполненному пути в БД — раньше кнопка «Карта in/out» висела и при пустой папке, отсюда была
        // жалоба «зачем она, файла же нет»). Клик всегда открывает самый свежий файл документа, а не
        // путь конкретной версии (см. SearchView.OpenMap/OpenInstructions/OpenModbusMap). Раздел целиком
        // пропускается, если показывать нечего — иначе «ДОКУМЕНТАЦИЯ» висела бы пустым заголовком.
        if (flags.HasIoMap || flags.HasModbus || flags.HasInstructions)
        {
            AddMenuHeader("Документация");
            if (flags.HasIoMap)
                AddMenuItem("Карта in/out", () => MapRequested?.Invoke(this, EventArgs.Empty),
                    "Открывается самый свежий файл карты ВВ");
            if (flags.HasModbus)
                AddMenuItem("Карта modbus", () => ModbusMapRequested?.Invoke(this, EventArgs.Empty),
                    "Открывается самый свежий файл карты Modbus");
            if (flags.HasInstructions)
                AddInstructionItems(flags);
        }

        AddMenuHeader("Версия");
        AddMenuItem("История версий", () => HistoryRequested?.Invoke(this, EventArgs.Empty));
        // То же имя, что у страницы сайдбара и её кнопки: обе точки открывают ОДНО окно
        // (EditFirmwareDialog), и два разных названия у одного окна оператора только путали.
        if (flags.CanEditTags)
            AddMenuItem("Модерация прошивки", () => TagsEditRequested?.Invoke(this, EventArgs.Empty),
                "Описание, теги, типы пуска, подтипы шкафов, доп. файлы — то же окно, что в разделе «Модерация прошивок»");

        var moreBtn = MakeActionButton("Ещё ▾", (_, _) => ToggleMore());
        moreBtn.ToolTip = "Файлы версии (папка, LFS, PSL), документация, история, модерация";
        ActionsPanel.Children.Add(moreBtn);
        MorePopup.PlacementTarget = moreBtn;

        // Только при первой отрисовке: карточка перерисовывается второй раз, когда досчитается
        // обход диска (SearchView.ScanDiskFlagsAsync), и затирать этим уже показанный ход
        // автосинхронизации («Синхронизация с диском…», «✓ Локальная копия обновлена») нельзя.
        if (!_syncStatusShown)
        {
            _syncStatusShown = true;
            ShowInitialSyncStatus(flags);
        }
    }

    private bool _syncStatusShown;

    /// <summary>Пере-показать строку статуса локальной копии по обновлённым флагам. Нужно, когда обход
    /// диска уже ПОСЛЕ первой отрисовки выяснил, что версии на диске нет (DiskMissing): первая отрисовка
    /// показала «синхронизируем…», а обычный пересчёт в Configure заблокирован (_syncStatusShown), иначе
    /// он затирал бы ход идущей автосинхры у других карточек. Такую карточку в автосинхру не берут (тянуть
    /// нечего), поэтому пере-показ безопасен — идущего статуса тут нет.</summary>
    public void RefreshSyncStatus(FirmwareCardFlags flags) => ShowInitialSyncStatus(flags);

    /// <summary>Хвост подписи кнопки панели: расширение того, что откроется, и от какой версии взят
    /// проект, если он унаследован. Оба факта важны, поэтому в одних скобках через запятую —
    /// «Открыть HMI проект (.dpj, от 2.1.041)», а не двумя парами скобок подряд. Пустых скобок не
    /// бывает: нечего сказать — суффикса нет вовсе.</summary>
    private static string HmiButtonSuffix(string? ext, string? fromVersion) => (ext, fromVersion) switch
    {
        (null, null) => "",
        (not null, null) => $" ({ext})",
        (null, not null) => $" (от {fromVersion})",
        _ => $" ({ext}, от {fromVersion})",
    };

    private static string? PrimaryOpenTooltip(HierarchyResult result, FirmwareCardFlags flags, string? openExt)
    {
        if (openExt == ".psl")
            return "Исходный проект SMLogix (.psl)" + (flags.HasLfs ? ". Скомпилированный .lfs — в «Ещё»" : "");
        if (openExt == ".lfs")
            return "Скомпилированный файл .lfs — открывается лоадером";
        return !string.IsNullOrEmpty(result.ExecutableHint) ? $"Исполняемый файл: {result.ExecutableHint}" : null;
    }

    /// <summary>Номер версии, к которой был приложен HMI-проект, если это НЕ текущая версия. Папка
    /// проекта называется «{номер версии}_hmi» (см. FirmwareAttachmentsService.CopyHmiProject) —
    /// отдельного поля «от какой версии панель» в базе нет и не нужно, имя папки это и есть.
    /// null — панель от этой же версии либо путь непонятного вида.</summary>
    internal static string? HmiSourceVersion(HierarchyResult result)
    {
        if (string.IsNullOrEmpty(result.HmiPath)) return null;
        var folder = System.IO.Path.GetFileName(result.HmiPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(folder)) return null;

        const string suffix = "_hmi";
        if (!folder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var version = folder[..^suffix.Length];
        if (version.Length == 0 || string.Equals(version, result.VersionRaw, StringComparison.OrdinalIgnoreCase))
            return null;

        // Панель отличается от текущей версии ТОЛЬКО аппаратной цифрой (eq/sub/sw/дата совпадают) —
        // это остаток hw-переписывания: программа та же самая, сменился только код железа, а папку
        // панели «{старый hw}_hmi» переименовать не успели (правка hw не доиграла, диск был офлайн,
        // либо старую версию удалили руками до того, как переименование доехало синхроном). Показывать
        // «HMI от {старый hw}» тут вводит в заблуждение — панель ровно этой прошивки, ничего не
        // «унаследовано». Поэтому такой случай гасим (карточка покажет просто «HMI ✓»), независимо от
        // того, отработало ли когда-нибудь переименование. Настоящее наследование от другой сборки
        // (иной sw/тип/дата) по-прежнему честно помечаем «от версии X».
        if (DiffersOnlyInHw(version, result.VersionRaw)) return null;

        return version;
    }

    /// <summary>true, если две строки версий парсятся и отличаются РОВНО одной аппаратной цифрой
    /// (eq_prefix, sub_prefix, sw_version и суффикс даты/времени совпадают, hw_version — нет). Именно
    /// такую пару даёт hw-переписывание одной и той же прошивки (напр. 2.4.044.0005 → 2.4.1321.0005).</summary>
    private static bool DiffersOnlyInHw(string panelVersion, string currentVersion)
    {
        var p = FwVersionNumber.Parse(panelVersion);
        var c = FwVersionNumber.Parse(currentVersion);
        return p is not null && c is not null
            && p.EqPrefix == c.EqPrefix
            && p.SubPrefix == c.SubPrefix
            && p.SwVersion == c.SwVersion
            && string.Equals(p.DtStr, c.DtStr, StringComparison.Ordinal)
            && p.HwVersion != c.HwVersion;
    }

    /// <summary>Строка «что лежит рядом с версией». У Segnetics LFS/PSL показываются с явным «нет» —
    /// именно про них был вопрос «есть он или нет»; остальное перечисляется, только когда есть.</summary>
    private void ShowFilesLine(HierarchyResult result, FirmwareCardFlags flags)
    {
        FilesLabel.ToolTip = flags.IsSegnetics
            ? ".LFS — скомпилированный файл, его заливают в контроллер лоадером.\n" +
              ".PSL — исходный проект SMLogix, его открывают для правки."
            : null;

        if (flags.DiskScanPending)
        {
            FilesLabel.Visibility = Visibility.Visible;
            FilesLabel.Text = "Файлы: проверяем папку версии…";
            return;
        }

        var parts = new List<string>();
        // «LFS —»/«PSL —» — только там, где эти файлы бывают: у KINCO-шкафа их отсутствие не новость,
        // а прочерк выглядел бы как потерянный файл (см. SegneticsProject).
        if (flags.IsSegnetics)
        {
            parts.Add(flags.HasLfs ? "LFS ✓" : "LFS —");
            parts.Add(flags.HasPsl ? "PSL ✓" : "PSL —");
        }
        if (flags.HasHmi) parts.Add(HmiSourceVersion(result) is { } from ? $"HMI ✓ (от {from})" : "HMI ✓");
        if (flags.HasParams) parts.Add("параметры ✓");
        if (flags.HasIoMap) parts.Add("карта ВВ ✓");
        if (flags.HasModbus) parts.Add("карта modbus ✓");
        if (flags.HasInstructions) parts.Add("инструкция ✓");
        // Ни одного файла-спутника — строка не нужна вовсе, пустое «Файлы:» только занимает место.
        FilesLabel.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesLabel.Text = "Файлы: " + string.Join(" · ", parts);
    }

    /// <summary>Инструкция в меню «Ещё» — набор действий вместо одного «Инструкции»: папка, правка
    /// исходного docx, PDF для печати и сразу «Печать». PDF собирается из docx по требованию и
    /// пересобирается, если docx правили (см. SearchView). Пункты правки/печати показываются, только
    /// когда для них реально есть файл; у легаси-инструкции без docx/pdf остаётся простое «Открыть».</summary>
    private void AddInstructionItems(FirmwareCardFlags flags)
    {
        AddMenuItem("Открыть папку с инструкцией", () => OpenInstructionFolderRequested?.Invoke(this, EventArgs.Empty),
            "Папка инструкции: исходный документ (docx) и PDF для печати");
        if (flags.HasInstructionDocx)
            AddMenuItem("Редактировать инструкцию (docx)", () => EditInstructionRequested?.Invoke(this, EventArgs.Empty),
                "Открыть исходный документ Word для правки. После сохранения PDF для печати обновится сам");
        if (flags.HasInstructionPrintable)
        {
            AddMenuItem("Открыть инструкцию для печати (PDF)", () => OpenInstructionPdfRequested?.Invoke(this, EventArgs.Empty),
                "Открыть PDF (пересоберётся из docx, если тот правили)");
            AddMenuItem("Печать инструкции", () => PrintInstructionRequested?.Invoke(this, EventArgs.Empty),
                "Отправить PDF инструкции на принтер по умолчанию");
        }
        // Легаси-файл иного формата (не docx и не pdf) — печатать/править нечем, но открыть можно.
        if (!flags.HasInstructionDocx && !flags.HasInstructionPrintable)
            AddMenuItem("Открыть инструкцию", () => InstructionsRequested?.Invoke(this, EventArgs.Empty),
                "Открывается самый свежий файл инструкции");
    }

    // ── Статус локальной копии ────────────────────────────────────────────

    private void ShowInitialSyncStatus(FirmwareCardFlags flags)
    {
        // Версии на диске по сохранённому пути нет и локальной копии тоже — раньше карточку прятали,
        // теперь показываем с явной пометкой (см. FirmwareCardFlags.DiskMissing). Приоритетнее
        // «есть локальная копия»: если файлов нет ни там, ни там — важно сказать именно это.
        if (flags.DiskMissing && !flags.HasLocal)
        {
            SetSyncStatus("⚠ На диске по сохранённому пути не найдена — возможно, папку переименовали или прошивку удалили. Файлов для открытия может не быть.",
                "WarningBrush");
            return;
        }
        if (flags.HasLocal)
        {
            SetSyncStatus(null);
            return;
        }
        if (flags.AutoSync)
        {
            SetSyncStatus(flags.HasAnyLocal
                ? "Локальная копия устарела — обновляем…"
                : "Локальной копии нет — синхронизируем…");
            return;
        }
        SetSyncStatus("Локальной копии нет. Автосинхронизация выключена — «Ещё» → «Обновить локальную копию с диска».",
            "WarningBrush");
    }

    /// <summary>text = null — скрыть строку статуса. brushKey — ключ темы (никаких hex-цветов).</summary>
    public void SetSyncStatus(string? text, string brushKey = "TextMutedBrush")
    {
        if (string.IsNullOrEmpty(text))
        {
            SyncStatusLabel.Visibility = Visibility.Collapsed;
            return;
        }
        SyncStatusLabel.Text = text;
        SyncStatusLabel.SetResourceReference(ForegroundProperty, brushKey);
        SyncStatusLabel.Visibility = Visibility.Visible;
    }

    // ── Кнопки/меню ───────────────────────────────────────────────────────

    private Button MakeActionButton(string text, RoutedEventHandler onClick)
    {
        var btn = new Button
        {
            Content = text,
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 0, 8, 8),
        };
        btn.Click += onClick;
        return btn;
    }

    private void AddMenuHeader(string text)
    {
        var header = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Style = (Style)FindResource("MutedText"),
            Margin = new Thickness(2, MorePanel.Children.Count == 0 ? 0 : 8, 0, 4),
            FontSize = 10,
        };
        MorePanel.Children.Add(header);
    }

    /// <summary>Пункт добавляется, только пока для него действительно есть что показать (см. вызовы в
    /// Configure) — недоступных серых пунктов с объяснением «почему нельзя» больше нет, поэтому и
    /// enabled-параметр здесь не нужен.</summary>
    private void AddMenuItem(string text, Action action, string? tooltip = null)
    {
        var btn = new Button
        {
            Content = text,
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = tooltip,
        };
        btn.Click += (_, _) =>
        {
            MorePopup.IsOpen = false;
            action();
        };
        MorePanel.Children.Add(btn);
    }

    /// <summary>Открытый Popup со StaysOpen="False" держит захват мыши и закрывается сам по нажатию
    /// мимо — а нажатие по самой кнопке «Ещё ▾» для него тоже «мимо». Порядок, снятый живьём:
    /// popup съедает нажатие и закрывается МОЛЧА (событие Closed при этом не приходит вообще, и
    /// PreviewMouseLeftButtonDown до кнопки тоже не доезжает), а через ~4 мс кнопке приходит Click —
    /// который видит IsOpen=false и открывает меню заново. Отсюда жалоба «нажимаю — ничего, тыкаю
    /// несколько раз»: закрыть меню той же кнопкой, которой открыл, было невозможно.
    ///
    /// Поэтому опорная точка — PreviewMouseDownOutsideCapturedElement: единственное событие, которое
    /// про это нажатие вообще приходит, и приходит гарантированно ДО Click (одно и то же нажатие).
    /// Click сразу после него — тот самый закрывающий клик, открывать по нему нечего.</summary>
    private DateTime _moreDismissedAt = DateTime.MinValue;

    private void MorePopup_Opened(object? sender, EventArgs e)
    {
        // Снять перед добавлением: авто-закрытие Closed не поднимает, так что штатной точки для
        // отписки нет, и без этого обработчик копился бы с каждым открытием.
        System.Windows.Input.Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(MorePopup, MoreDismissedByClickOutside);
        System.Windows.Input.Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(MorePopup, MoreDismissedByClickOutside);
    }

    private void MorePopup_Closed(object? sender, EventArgs e) =>
        System.Windows.Input.Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(MorePopup, MoreDismissedByClickOutside);

    private void MoreDismissedByClickOutside(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _moreDismissedAt = DateTime.Now;
        System.Windows.Input.Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(MorePopup, MoreDismissedByClickOutside);
    }

    private void ToggleMore()
    {
        // Порог заведомо больше зазора между закрытием и Click (единицы мс) и заведомо меньше
        // осмысленного «закрыл и сразу передумал».
        var dismissedByThisClick = !MorePopup.IsOpen
            && (DateTime.Now - _moreDismissedAt).TotalMilliseconds < 250;
        if (dismissedByThisClick) return;
        MorePopup.IsOpen = !MorePopup.IsOpen;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        FlashCopyFeedback();
        CopyNameRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Header_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        FlashCopyFeedback();
        CopyNameRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Visible confirmation right where the operator clicked — a status-bar message alone
    /// (the only feedback before this) is easy to miss, especially clicking from a long results list.</summary>
    private void FlashCopyFeedback()
    {
        var original = CopyButton.Content;
        CopyButton.Content = "✓ Скопировано";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButton.Content = original;
        };
        timer.Start();
    }
}
