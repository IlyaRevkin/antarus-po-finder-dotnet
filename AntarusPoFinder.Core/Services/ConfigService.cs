using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

public class QuickApp
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

/// <summary>Typed settings access backed by the SQLite settings table. Mirrors config_service.py.</summary>
public class ConfigService
{
    public static readonly string AppData =
        Environment.GetEnvironmentVariable("ANTARUS_TEST_APPDATA") is { Length: > 0 } testDir
            ? testDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntarusPOFinder");
    public static readonly string DbPath = Path.Combine(AppData, "po_finder.db");
    public static readonly string LocalFw = Path.Combine(AppData, "firmware");
    public static readonly string LocalTemplates = Path.Combine(AppData, "templates");

    /// <summary>Рабочие области лоадера (см. AntarusPoFinder.Core.Loader.LoaderWorkspace) — сборка и
    /// загрузка идут ЛОКАЛЬНО здесь, а не на сетевом диске: приложение не клиент-серверное.</summary>
    public static readonly string LocalLoader = Path.Combine(AppData, "loader");

    /// <summary>Тип пуска — fixed set. Первые четыре пришли из Python-версии (LAUNCH_TYPES); пятый
    /// (<see cref="LaunchTypeNone"/>) добавлен потому, что часть шкафов вообще не имеет типа пуска, а
    /// поле обязательное — раньше в таком случае приходилось ставить заведомо неверную галочку.
    /// Хранится в launch_types (JSON-массив) ровно так же, как остальные четыре — отдельного
    /// значения/флага в схеме нет, чтобы не плодить второй способ выразить одно и то же.</summary>
    public static readonly string[] LaunchTypes = ["УПП", "ПП", "ПЧ", "КПЧ", LaunchTypeNone];

    /// <summary>«Тип пуска отсутствует» — взаимоисключающий с остальными четырьмя (см.
    /// AntarusPoFinder.App.Views.LaunchTypeChecks: при его выборе остальные снимаются и блокируются).</summary>
    public const string LaunchTypeNone = "Отсутствует";

    // ── Предустановленные адреса хранилища ───────────────────────────────────────────────────
    // Вынесены в константы, а не только в Defaults ниже: этими же значениями разовая миграция
    // (Database.SeedHostingAddressesOnce) заполняет уже установленные базы, чтобы адрес физически
    // лежал в settings и уезжал в общий конфиг, а не оставался невидимой подстановкой из кода.

    /// <summary>Адрес S3-хранилища Timeweb, выданный компании (Иван Герасимов, 05.08.2026).</summary>
    public const string DefaultS3Endpoint = "https://s3.twcstorage.ru";

    public const string DefaultS3Bucket = "amperus";

    public const string DefaultS3Region = "ru-1";

    /// <summary>Публичный адрес того же бакета — по нему инструкция открывается с телефона по QR.</summary>
    public const string DefaultInstructionBaseUrl = "https://fs.elitacompany.ru";

    /// <summary>Ключи, у которых ПУСТОЕ сохранённое значение означает «не настроено», а не «настроено
    /// пустым»: адреса хранилища и инструкций одни и те же на всю компанию, и правильных пустых
    /// значений у них не бывает. Из-за этого пустая строка раньше была ловушкой в трёх местах сразу:
    /// <list type="bullet">
    /// <item>адрес, ни разу не сохранённый руками, в settings не лежал вовсе — значит и в общий
    /// конфиг не уезжал, и на соседней машине поле оставалось пустым («не синхронизируется»);</item>
    /// <item>стоило один раз стереть поле — пустая строка сохранялась настоящим значением и глушила
    /// предустановку навсегда (умолчание подставляется только при ОТСУТСТВИИ строки);</item>
    /// <item>та же пустая строка уезжала в общий конфиг и затирала адрес у всех остальных
    /// (см. ConfigSyncService.ShouldApplySetting — оттуда она теперь и не применяется).</item>
    /// </list>
    /// s3_prefix здесь СОЗНАТЕЛЬНО отсутствует: пустой префикс — законное и штатное значение
    /// («раскладка бакета совпадает с раскладкой диска»).</summary>
    public static readonly HashSet<string> PresetKeys = new(StringComparer.Ordinal)
    {
        "s3_endpoint", "s3_bucket", "s3_region", "instruction_base_url",
    };

    /// <summary>Предустановки для <see cref="PresetKeys"/> — то, чем разовая миграция заполняет
    /// пустые/отсутствующие строки в уже существующих базах.</summary>
    public static IEnumerable<KeyValuePair<string, string>> PresetDefaults =>
        PresetKeys.Select(k => new KeyValuePair<string, string>(k, Defaults[k]));

    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["root_path"] = @"Z:\Software\Antarus Finder",
        ["second_disk_path"] = "",
        // ── Хранилище на хостинге (S3) ───────────────────────────────────────────────────────
        // Реквизиты от Ивана Герасимова (05.08.2026). Адрес/бакет/регион заполнены сразу, ключей на
        // тот момент ещё не было — присланный файл secrets перетаскивается в Настройки → Сетевые
        // диски, и до тех пор выкладка молча не делается (см. S3Settings.CanPublish). Регион у
        // Timeweb — ru-1.
        ["s3_endpoint"] = DefaultS3Endpoint,
        ["s3_bucket"] = DefaultS3Bucket,
        ["s3_region"] = DefaultS3Region,
        // Префикс внутри бакета. Пусто — раскладка бакета совпадает с раскладкой диска один в один;
        // выданный Иваном Path «/amperus» совпадает с именем бакета и отдельным префиксом НЕ
        // является (иначе ключи вышли бы вида amperus/amperus/…).
        ["s3_prefix"] = "",
        // Ключи доступа. Пусто = не выданы. Хранятся как пароли: не уезжают в общий конфиг
        // (ConfigSyncService.SkipSettingsKeys) и лежат зашифрованными — это доступ на ЗАПИСЬ во весь
        // бакет, а не настройка.
        ["s3_access_key"] = "",
        ["s3_secret_key"] = "",
        // Выключатель выкладки отдельно от ключей: приостановить выкладку, не стирая реквизиты.
        // Включён по умолчанию — без ключей он всё равно ничего не делает, зато в день, когда ключи
        // впишут, не придётся вспоминать про вторую галочку.
        ["s3_publish"] = "true",
        // Справочник написаний для адресов на хостинге (см. TranslitMap). Пусто = только
        // автоматический перевод; человек дописывает сюда лишь те имена, где принятое в компании
        // написание отличается от машинного. Настройка ОБЩАЯ и синхронизируется: ссылку под QR
        // печатает одна машина, а файл выкладывает другая, и разойдись у них перевод — наклейка
        // поведёт в пустоту.
        ["translit_map"] = "",
        // Предел размера файла, уходящего на хостинг, и что делать с превышением. Ограничение
        // касается ТОЛЬКО хостинга: на диск сколь угодно большой проект ПЛК класть можно, а тянуть
        // его по ссылке с телефона в цеху — нет. Общие настройки: политика хранения, а не свойство
        // машины.
        // Вид страницы-заглушки «Инструкция в разработке». Её видит заказчик, наведя телефон на
        // наклейку до того, как инструкцию допишут, — то есть это оформление, а не техническая
        // затычка. Общая настройка: заглушки на хостинг кладут разные машины, выглядеть они обязаны
        // одинаково. Пусто — вид по умолчанию (см. StubLayout.Default).
        ["stub_layout"] = "",
        ["hosting_max_file_mb"] = "20",
        ["hosting_size_limit_hard"] = "true",
        // «Диск перестроен под новую раскладку» (docs/hierarchy-rework-plan.md, этап 4): у версии
        // есть свои «Прошивка» и четыре папки документов. Ставится САМА, когда человек выполнил
        // перестройку диска (DiskMigrationDialog), и синхронизируется — это свойство ОБЩЕГО ДИСКА, а
        // не машины: перестройку запускают один раз, а класть файлы по-новому должны сразу все,
        // иначе одна машина будет разбирать папку версии обратно в плоскую.
        ["disk_layout_v2"] = "false",
        ["inspection_folder"] = "",
        // Значения ниже — фолбэк ТОЛЬКО для Get("admin_password")/Get("programmer_password"), если
        // строки settings ещё нет вовсе (крайне маловероятно после Database.SeedDefaultAdminPasswordHash,
        // который заводит хешированный "12345" уже при создании/открытии БД — см. её doc-комментарий).
        // Не хеш, потому что PasswordHasher (Core.Infrastructure) сюда не тянется как константа — но
        // VerifyAdminPassword/VerifyProgrammerPassword ниже сравнивают именно хешем, поэтому в реальности
        // этот текстовый фолбэк никогда не участвует в сравнении пароля, только в отображении/логах.
        ["admin_password"] = "12345",
        ["programmer_password"] = "",
        ["current_role"] = "naladchik",
        ["theme"] = "light",
        ["keep_archives"] = "false",
        ["image_server_port"] = "9876",
        // Домен предприятия по умолчанию — Elita: единственный домен, в котором приложение реально
        // работает, поэтому оператору на первом входе достаточно логина и пароля, а поле домена
        // спрятано в «Дополнительные параметры» (AdStartupLoginDialog). Если строка домена уже
        // сохранена (кто-то вписал свой), фолбэк не перебивает — как и у всех остальных ключей.
        ["ad_domain"] = "Elita",
        ["ad_group_administrator"] = "",
        ["ad_group_programmer"] = "",
        ["ad_group_naladchik"] = "",
        // «Оба» по умолчанию: сначала LDAP (прямой бинд к контроллеру домена), и только если домен
        // недоступен по сети — HTTP-запрос к ad_http_url ниже как запасной путь (см.
        // CombinedAdCredentialValidator/AdCredentialValidatorFactory). Наладчику за пределами
        // офисной сети LDAP не достучится, а внутренний веб-сервер по HTTPS — достучится.
        ["ad_auth_mode"] = "both",
        // Адрес внутреннего веб-сервера для способа №2 (HTTP-проверка пароля, см.
        // HttpAdCredentialValidator). Предустановлен рабочий адрес диска предприятия; администратор
        // может сменить его в Настройки → Общие или в «Доп. параметрах» окна входа, если IT поменяет
        // формат. Синхронизируемый ключ политики входа (его НЕТ в ConfigSyncService.SkipSettingsKeys):
        // адрес один на всё предприятие, администратор задаёт его один раз, и он доезжает до всех.
        // Правка в «Доп. параметрах» окна входа остаётся локальной страховкой на случай, когда домен
        // временно отвечает по другому адресу только у этой машины.
        ["ad_http_url"] = "https://disk.antarus.su/cloud",
        ["sync_interval_min"] = "5",
        ["quick_apps"] = "[]",
        ["app_update_path"] = "",
        // Общая (синхронизируемая) папка обновлений — администратор задаёт её один раз, и она уезжает
        // на все машины вместе с остальным общим конфигом. Пусто по умолчанию, поэтому появление
        // ключа ничего не меняет для существующих установок. Как именно она разворачивается на
        // конкретной машине (и почему относительный путь предпочтительнее абсолютного) — см.
        // UpdateFolderResolver. В ConfigSyncService.SkipSettingsKeys этого ключа СОЗНАТЕЛЬНО нет —
        // в отличие от соседнего app_update_path, который остаётся локальным перебивом.
        ["app_update_path_shared"] = "",
        // Раньше "false" — и это была настоящая причина жалобы «фикс не работает»: транспорт
        // обновления полностью исправен (релиз виден, .sha256 сходится, установка проходит), но
        // при выключенном автообновлении единственной дорогой к новой версии была плашка с кнопкой
        // «Установить». Наладчик, который не заходит в Настройки и не приглядывается к плашкам,
        // оставался на своей сборке неделями и смотрел на давно исправленные баги. Значение по
        // умолчанию — только для установок, где ключ вообще ни разу не сохраняли: если кто-то
        // осознанно выключил автообновление у себя, в settings лежит явное "false", и оно здесь
        // не перебивается (в отличие от app_start_minimized, которому потребовался разовый сброс
        // уже сохранённых значений — см. Database.ResetAppStartMinimizedDefaultOnce).
        ["app_auto_update"] = "true",
        ["fw_auto_update_dirs"] = "[]",
        ["config_last_synced_at"] = "",
        ["scan_resolution_dpi"] = "200",
        ["config_push_interval_min"] = "0",
        ["reservation_ttl_hours"] = "72",
        ["onboarding_shown"] = "false",
        ["notification_categories_disabled"] = "[]",
        ["notification_categories_muted_unread"] = "[]",
        ["close_action"] = "close",
        ["inspection_auto_cleanup_days"] = "0",
        ["inspection_auto_cleanup_minutes"] = "",
        ["quick_apps_display_mode"] = "sidebar",
        // Раньше "true" — на новых установках больше не навязываем свёрнутый старт (см. также
        // Database.ResetAppStartMinimizedDefaultOnce — разовый сброс этого значения для баз,
        // созданных до этого изменения, у которых оно уже сохранено как "true").
        ["app_start_minimized"] = "false",
        ["layout_fallback_enabled"] = "true",
        ["layout_fallback_threshold"] = "3",
        // Порог статистики выборов прошивки (Database.FwUsage.cs): по такому запросу версию должны
        // выбрать хотя бы столько раз, прежде чем частота выбора начнёт двигать выдачу поиска. Без
        // порога один случайный клик уже поднимал бы версию наравне со «ставят стабильно» —
        // см. Database.Search.cs (EffectiveUsage/Rank). 2 — минимальный порог, отличающий
        // «выбрали хоть раз» от «выбирают регулярно», не требуя от оператора десятка одинаковых
        // кликов ради того, чтобы подсказка вообще заработала.
        ["fw_usage_threshold"] = "2",
        // На сколько умножать вклад счётчика открытий в ранг выдачи (см. Database.Search.cs
        // AutoUsageBonus). 1 — прежнее поведение; больше — популярность двигает выдачу сильнее; 0 —
        // счётчик перестаёт влиять вовсе, остаётся релевантность и ручной вес. В отличие от
        // fw_usage_threshold этот параметр СИНХРОНИЗИРУЕМЫЙ (его нет в ConfigSyncService.
        // SkipSettingsKeys): «насколько популярность вообще важна» — общая политика поиска, а не
        // чувствительность конкретной машины.
        ["fw_usage_multiplier"] = "1",
        // Делиться ли СВОИМ ручным весом выдачи с другими машинами (см. Database.FwUsage.cs weight).
        // false — вес остаётся личным (счётчик открытий уезжает всё равно, это общая статистика);
        // true — вес уезжает в общий конфиг и складывается с весом других. Сам переключатель
        // per-machine (в SkipSettingsKeys): каждая машина решает за свой вес.
        ["fw_weight_shared"] = "false",
        // Вход по AD при запуске включён по умолчанию: без авторизации функционал недоступен (гейт
        // App.OnStartup показывает AdStartupLoginDialog до главного окна). Раньше был выключен «чтобы
        // существующие установки стартовали как прежде», но по требованию — единая политика для всех
        // машин. Аварийный вход администратора (пароль «12345» из SeedDefaultAdminPasswordHash, пока
        // не сменён) остаётся всегда, поэтому недоступный домен на новой машине никого не запирает.
        // Явно сохранённое «false» на конкретной машине фолбэк не перебивает (как app_auto_update).
        ["ad_require_login"] = "true",
        ["ad_require_login_default_days"] = "14",
        ["ad_last_login"] = "",
        ["search_auto_sync"] = "true",
        ["loader_exe_path"] = "",
        ["loader_format_default"] = "false",
        ["loader_update_kernel_default"] = "false",
        ["loader_last_target"] = "",
        // Подключение к ПЛК. Сам Automation-процесс параметры подключения в запросе НЕ принимает —
        // он читает их из настроек Segnetics Loader (docs/loader/LOADER_AUTOMATION_API.md, раздел
        // «Запрос операции»), поэтому выбор наладчика мы переносим туда (LoaderConnectionSettings).
        // Пусто у режима = «что выбрано в самом Loader, то и оставить» — так ведёт себя новая
        // установка, пока наладчик ничего не выбрал.
        // Корпоративный вход (Keycloak/OpenID Connect) и серверный обмен — оба выключены по
        // умолчанию: без поднятого сервера и выданного client_id включать их нечем, а молчаливое
        // включение отрезало бы машины со старой версией от общих данных (docs/client-server-plan.md).
        ["oidc_authority"] = "",
        ["oidc_client_id"] = "",
        ["oidc_groups_claim"] = "groups",
        ["sync_transport"] = "fileshare",
        ["server_url"] = "",
        ["loader_connection_mode"] = "",
        ["loader_plc_ip"] = "",
        ["loader_network_adapter"] = "",
        // Проверять ли связь с ПЛК до заливки (TCP-проба по адресу выше). Экономит самый обидный
        // случай: наладчик ждёт минуту, чтобы получить «CONNECTION_FAILED» из-за невоткнутого шнурка.
        ["loader_check_link"] = "true",
        ["loader_link_timeout_ms"] = "1500",
        // Бета-опция UploadView: одна общая drag&drop-зона для файла/папки ПЛК и HMI-проекта вместо
        // двух раздельных зон. Выключено по умолчанию — раздельные зоны остаются поведением по
        // умолчанию для всех существующих и новых установок, пока программист явно не включит эту
        // опцию себе в Настройках (см. UnifiedPlcHmiZoneEnabled ниже).
        ["unified_plc_hmi_zone"] = "false",
        // Версия, для которой окно «Что нового» уже показано (или молча зачтено — см.
        // AppUpdateService.ShouldShowWhatsNew) на ЭТОЙ машине. Пусто = ключ ещё ни разу не писали
        // (самая первая установка, или самый первый запуск после появления этой фичи на уже
        // существующей установке) — в этом случае окно не показывается, а текущая версия молча
        // записывается сюда, чтобы отсчёт "что нового" начался со следующего реального обновления,
        // а не задним числом показал весь список изменений версии, на которой человек и так уже
        // работал. Per-machine — НЕ синхронизируется (см. ConfigSyncService.SkipSettingsKeys), как
        // theme/close_action: у каждой машины свой момент обновления.
        ["last_whatsnew_shown_version"] = "",
        // Постоянный журнал «что менялось по версиям приложения»: JSON-список AppChangelogEntry,
        // новые версии сверху. Наполняется при обновлении (MainWindowViewModel.CheckWhatsNewAsync
        // кладёт сюда тело release notes ровно тогда же, когда показывает окно «Что нового»), а
        // читается окном истории изменений в любой момент потом. Per-machine, как и
        // last_whatsnew_shown_version, — это журнал обновлений именно ЭТОЙ установки, не общий
        // справочник (см. ConfigSyncService.SkipSettingsKeys).
        ["app_changelog_history"] = "[]",
        // ── Печать этикеток с QR и наклеек ───────────────────────────────────────────────────
        // Базовый веб-адрес диска инструкций: из него собирается ссылка в QR-коде на этикетке
        // (LabelLinkBuilder) и по нему же открывается выложенный на хостинг файл. Предустановлен
        // рабочим адресом компании — это тот же адрес, что и у бакета в s3_endpoint выше, только
        // публичный: без него наклейка ведёт сетевым путём, который с телефона не открыть, а
        // вспоминать и вписывать его руками на каждой машине незачем (см. PresetKeys ниже — пустое
        // значение здесь означает «не настроено» и возвращает эту предустановку).
        ["instruction_base_url"] = DefaultInstructionBaseUrl,
        // Размер наклейки в миллиметрах. Per-machine (ConfigSyncService.SkipSettingsKeys): наклейка
        // заправлена в КОНКРЕТНЫЙ принтер, и у соседа она своя — приехавший чужой размер печатает
        // мимо высечки.
        ["label_width_mm"] = "97.5",
        ["label_height_mm"] = "72",
        // Поля и сдвиг — см. LabelLayout, там же разобрано, почему это разные настройки и почему без
        // полей у любого размера «обрезался верх». Поля по сторонам (label_margin_*_mm) появились
        // позже единого label_margin_mm; единое пишется и дальше — его читают старые версии
        // программы. Всё это тоже per-machine: описывает кромку конкретного принтера.
        ["label_margin_mm"] = "3",
        ["label_offset_x_mm"] = "0",
        ["label_offset_y_mm"] = "0",
        // Поворот содержимого на наклейке — для рулонных принтеров, где наклейка едет узкой стороной
        // вперёд. Per-machine по той же причине, что и размер: зависит от того, что за принтер.
        ["label_rotation"] = "None",
        ["label_qr_mm"] = "0",
        ["label_title_pt"] = "16",
        ["label_caption_pt"] = "9",
        ["label_show_link"] = "true",
        ["label_show_frame"] = "true",
        ["label_fancy_qr"] = "true",
        // Принтер этикеток — per-machine (у каждой машины свои принтеры). Пусто — печать на принтер
        // Windows по умолчанию.
        ["label_printer"] = "",
        // Папка с шаблонами наклеек («Проверено ОТК», «Проверьте перед подключением» и т.п.).
        // Пусто — берётся Конфиг\Наклейки на общем диске (см. StickerTemplates.FolderFor): чтобы
        // это заработало, настраивать ничего не нужно.
        ["stickers_folder"] = "",
        // Папка типовых паспортов — бланков, которые не ложатся ни на один тип шкафа (НКУ, Щит СПЛ,
        // ШР): пусто = Конфиг\Паспорта на общем диске (см. PassportService.TemplatesFolder).
        // Синхронизируемая, как и папка наклеек: где лежат общие бланки — политика предприятия.
        ["passport_templates_folder"] = "",
        // Метка в бланке, вместо которой подставляется название шкафа при печати
        // (DocxTemplateFiller). Синхронизируемая: бланки общие, значит и метка в них одна на всех —
        // разъехавшись, она перестала бы находиться в чужих шаблонах.
        ["passport_name_placeholder"] = DocxTemplateFiller.DefaultPlaceholder,
        // Бланк, выбранный в окне печати в прошлый раз — per-machine (SkipSettingsKeys): у каждого
        // наладчика свой привычный, и навязывать чужой выбор незачем.
        ["passport_template_last"] = "",
        // Паспорт печатается с двух сторон, разворот относительно КОРОТКОГО края — прямая просьба
        // (см. DuplexPrinting). Синхронизируемая: как оформляется паспорт, идущий заказчику, —
        // политика предприятия, а не привычка машины. Выключать штатно неоткуда (в интерфейсе этого
        // переключателя нет) — ключ оставлен на случай принтера, который двусторонней не умеет.
        ["passport_duplex_short_edge"] = "true",
        // Развёрнута ли секция бокового меню «ДЛЯ НАЛАДЧИКА». Per-machine (SkipSettingsKeys): это
        // состояние окна на конкретном компьютере, а не общая настройка — у соседа своя привычка.
        ["sidebar_setup_expanded"] = "false",
    };

    private readonly Database _db;

    public ConfigService(Database db)
    {
        _db = db;
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(LocalFw);
        Directory.CreateDirectory(LocalTemplates);
        Directory.CreateDirectory(LocalLoader);
    }

    public string Get(string key)
    {
        var preset = Defaults.GetValueOrDefault(key, "");
        var stored = _db.GetSetting(key, preset);
        // Пустая строка у ключей из PresetKeys — это «не настроено», а не значение: возвращаем
        // предустановку. Так стёртое поле и приехавшая по синхронизации пустота не могут навсегда
        // погасить адрес, который в компании один и тот же (см. док PresetKeys).
        return stored.Length == 0 && PresetKeys.Contains(key) ? preset : stored;
    }

    public void Set(string key, string value) => _db.SetSetting(key, value);

    public string RootPath() => Get("root_path");
    public void SetRootPath(string path) => Set("root_path", path);

    public string SecondDiskPath() => Get("second_disk_path");
    public void SetSecondDiskPath(string path) => Set("second_disk_path", path);

    // ── Хранилище на хостинге (S3) ───────────────────────────────────────────────────────────

    public string S3Endpoint() => Get("s3_endpoint");
    public void SetS3Endpoint(string url) => Set("s3_endpoint", url.Trim().TrimEnd('/'));

    public string S3Bucket() => Get("s3_bucket");
    public void SetS3Bucket(string bucket) => Set("s3_bucket", bucket.Trim().Trim('/'));

    public string S3Region() => Get("s3_region");
    public void SetS3Region(string region) => Set("s3_region", region.Trim());

    public string S3Prefix() => Get("s3_prefix");
    public void SetS3Prefix(string prefix) => Set("s3_prefix", prefix.Trim().Trim('/'));

    public string S3AccessKey() => Get("s3_access_key");
    public void SetS3AccessKey(string key) => Set("s3_access_key", key.Trim());

    /// <summary>Secret Access Key. Хранится зашифрованным тем же способом, что и общий конфиг на
    /// сетевой шаре (<see cref="ConfigFileCrypto"/>), и по той же причине: чтобы значение не читалось
    /// глазами у того, кто открыл файл базы. Это защита от случайного взгляда, а НЕ от того, у кого
    /// есть сам exe — ключ шифрования лежит внутри программы (осознанный компромисс, разобранный в
    /// комментарии ConfigFileCrypto). Ключ шифрования единый для всех машин (тот же ConfigFileCrypto,
    /// не DPAPI), поэтому сохранённый здесь шифротекст расшифровывается на ЛЮБОЙ машине — именно это и
    /// позволяет секрету синхронизироваться: GetAllSettings отдаёт значение «enc:…» как есть, оно
    /// уезжает в общий конфиг зашифрованным (ключей s3_* больше нет в ConfigSyncService.SkipSettingsKeys),
    /// а принимающая машина расшифровывает его здесь же. Так выкладывать может каждый, кому положено, а
    /// не только тот, кто вписал ключ у себя.
    ///
    /// Значения, сохранённые до появления шифрования (или вписанные в базу руками), читаются как
    /// есть — иначе однажды вписанный ключ перестал бы работать после обновления программы.</summary>
    public string S3SecretKey()
    {
        var stored = Get("s3_secret_key");
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) return stored;

        try
        {
            var data = Convert.FromBase64String(stored[EncryptedPrefix.Length..]);
            return ConfigFileCrypto.TryDecrypt(data) ?? "";
        }
        catch (FormatException)
        {
            // Строка испорчена — вести себя надо как при незаданном ключе (выкладка выключится и
            // скажет «не заданы ключи доступа»), а не падать при каждом обращении к настройкам.
            return "";
        }
    }

    public void SetS3SecretKey(string secret)
    {
        var value = (secret ?? "").Trim();
        Set("s3_secret_key", value.Length == 0
            ? ""
            : EncryptedPrefix + Convert.ToBase64String(ConfigFileCrypto.Encrypt(value)));
    }

    /// <summary>Метка «дальше зашифрованное значение» — по ней же отличается ключ, вписанный в базу
    /// руками до появления шифрования.</summary>
    private const string EncryptedPrefix = "enc:";

    public bool S3Publish() => Get("s3_publish").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetS3Publish(bool value) => Set("s3_publish", value ? "true" : "false");

    /// <summary>Все реквизиты хостинга одним значением — единственное место, которое их собирает,
    /// чтобы «настроен ли хостинг» считалось одинаково и в настройках, и на пути загрузки версии.
    /// Веб-адрес берётся из уже существующей настройки печати этикеток (instruction_base_url): это
    /// один и тот же адрес — тот, что уходит в QR-код и по которому файл открывается с телефона.</summary>
    public S3Settings S3() => new(
        Endpoint: S3Endpoint(),
        Bucket: S3Bucket(),
        Region: S3Region(),
        Prefix: S3Prefix(),
        AccessKey: S3AccessKey(),
        SecretKey: S3SecretKey(),
        WebUrl: InstructionBaseUrl(),
        Enabled: S3Publish())
    {
        Translit = Translit(),
        MaxFileBytes = HostingMaxFileBytes(),
        HardSizeLimit = HostingSizeLimitHard(),
    };

    /// <summary>Предел размера файла на хостинге в мегабайтах. Ноль или мусор в настройке —
    /// значение по умолчанию: пустое поле не должно означать «предела нет», иначе достаточно
    /// случайно стереть цифру, чтобы ограничение молча перестало работать.</summary>
    public int HostingMaxFileMb() =>
        int.TryParse(Get("hosting_max_file_mb"), out var mb) && mb > 0
            ? mb
            : (int)(S3Settings.DefaultMaxFileBytes / 1024 / 1024);

    public void SetHostingMaxFileMb(int mb) => Set("hosting_max_file_mb", Math.Max(1, mb).ToString());

    public long HostingMaxFileBytes() => (long)HostingMaxFileMb() * 1024 * 1024;

    /// <summary>Жёсткий запрет (true) или предупреждение с выкладкой (false).</summary>
    public bool HostingSizeLimitHard() =>
        !Get("hosting_size_limit_hard").Equals("false", StringComparison.OrdinalIgnoreCase);

    public void SetHostingSizeLimitHard(bool value) => Set("hosting_size_limit_hard", value ? "true" : "false");

    /// <summary>Вид страницы-заглушки «Инструкция в разработке» (см. <see cref="StubLayout"/>).</summary>
    public StubLayout StubLayout() => Services.StubLayout.Parse(Get("stub_layout")).Sane();

    public void SetStubLayout(StubLayout layout) => Set("stub_layout", layout.Sane().ToJson());

    /// <summary>Справочник написаний для адресов на хостинге. Читается из общей настройки, поэтому
    /// одинаков на всех машинах — от этого зависит, попадёт ли наклейка с QR в выложенный файл
    /// (см. <see cref="TranslitMap"/>).</summary>
    public TranslitMap Translit() => TranslitMap.Parse(Get("translit_map"));

    public void SetTranslit(TranslitMap map) => Set("translit_map", map.ToJson());

    /// <summary>Диск уже перестроен под раскладку «пять папок внутри версии» (этап 4). Пока false,
    /// новая версия рождается ровно так же, как рождалась всегда — файл прошивки в корне папки
    /// версии, документы в общих папках контроллера: иначе на неперестроенном диске одни версии были
    /// бы новой раскладки, другие старой, и разобраться, где что лежит, стало бы невозможно.
    /// Читается программа обе раскладки в любом случае (VersionLayout) — флаг влияет только на
    /// ЗАПИСЬ.</summary>
    public bool DiskLayoutV2() => Get("disk_layout_v2").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetDiskLayoutV2(bool value) => Set("disk_layout_v2", value ? "true" : "false");

    // ── Печать этикеток с QR и наклеек ───────────────────────────────────────────────────────

    /// <summary>Базовый веб-адрес диска инструкций (без хвостового «/»). Предустановлен адресом
    /// компании и пустым не бывает: стёртое поле возвращает предустановку (см. <see cref="PresetKeys"/>),
    /// потому что без адреса в QR уходит сетевой путь, который с телефона не открыть.</summary>
    public string InstructionBaseUrl() => Get("instruction_base_url");
    public void SetInstructionBaseUrl(string url) => Set("instruction_base_url", url.Trim().TrimEnd('/'));

    public double LabelWidthMm() => ParseMm(Get("label_width_mm"), 97.5);
    public void SetLabelWidthMm(double mm) => Set("label_width_mm", mm.ToString(CultureInfo.InvariantCulture));

    public double LabelHeightMm() => ParseMm(Get("label_height_mm"), 72);
    public void SetLabelHeightMm(double mm) => Set("label_height_mm", mm.ToString(CultureInfo.InvariantCulture));

    /// <summary>Числовая настройка макета этикетки (миллиметры или пункты) — читается тем же
    /// снисходительным к запятой разбором, что размер (см. <see cref="ParseMm"/>), но допускает 0 и
    /// отрицательные значения: сдвиг калибровки бывает в обе стороны, а «0» у стороны QR означает
    /// «посчитай сам». Границы разумного — не здесь, а в LabelLayout.Clamped: правило одно на всех,
    /// включая значения, приехавшие синхронизацией с чужой машины.</summary>
    public double LabelNumber(string key, double fallback)
    {
        var raw = Get(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        if (double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        return fallback;
    }

    public void SetLabelNumber(string key, double value) =>
        Set(key, value.ToString(CultureInfo.InvariantCulture));

    public bool LabelFlag(string key, bool fallback)
    {
        var raw = Get(key).Trim();
        return raw.Length == 0 ? fallback : raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public void SetLabelFlag(string key, bool value) => Set(key, value ? "true" : "false");

    /// <summary>Текстовая настройка макета этикетки (подпись над кодом, подпись в центре кода).
    /// Пустая строка в базе — это ЗНАЧЕНИЕ («подписи нет»), а не «настройка не задана»: отличаем их
    /// по самому факту наличия ключа, иначе стёртую подпись возвращал бы обратно запасной текст.</summary>
    public string LabelText(string key, string fallback) => _db.HasSetting(key) ? Get(key) : fallback;

    public void SetLabelText(string key, string value) => Set(key, (value ?? "").Trim());

    /// <summary>Имя принтера этикеток как его видит Windows. Пусто — принтер по умолчанию.</summary>
    public string LabelPrinter() => Get("label_printer");
    public void SetLabelPrinter(string name) => Set("label_printer", name.Trim());

    /// <summary>Папка с шаблонами наклеек — как её задал администратор. Пусто = «по умолчанию»,
    /// разворачивается в <c>&lt;диск&gt;\Конфиг\Наклейки</c> (см. StickerTemplates.FolderFor).</summary>
    public string StickersFolder() => Get("stickers_folder");
    public void SetStickersFolder(string path) => Set("stickers_folder", path.Trim());

    // Настройки «какая именно фотография-пасхалка» (easter_photo) больше нет: показывается вся общая
    // папка целиком (см. EasterEggPhoto.List). Настройка ехала между машинами отдельно от самого
    // файла и перетиралась последним записавшим — каждый видел свою. Старое значение в базе остаётся
    // лежать безвредным мусором: его никто не читает.

    /// <summary>Папка типовых паспортов — как её задал администратор. Пусто = «по умолчанию»,
    /// разворачивается в <c>&lt;диск&gt;\Конфиг\Паспорта</c> (см. PassportService.TemplatesFolder).</summary>
    public string PassportTemplatesFolder() => Get("passport_templates_folder");
    public void SetPassportTemplatesFolder(string path) => Set("passport_templates_folder", path.Trim());

    /// <summary>Метка в бланке, вместо которой при печати подставляется название шкафа. Пустое
    /// значение в настройках означает «вернуть метку по умолчанию», а не «подставлять везде»:
    /// пустая метка нашлась бы в каждой точке текста.</summary>
    public string PassportNamePlaceholder()
    {
        var value = Get("passport_name_placeholder").Trim();
        return value.Length > 0 ? value : DocxTemplateFiller.DefaultPlaceholder;
    }

    public void SetPassportNamePlaceholder(string value) => Set("passport_name_placeholder", value.Trim());

    /// <summary>Название бланка, выбранного в окне печати паспорта в прошлый раз (per-machine).</summary>
    public string PassportTemplateLast() => Get("passport_template_last");
    public void SetPassportTemplateLast(string name) => Set("passport_template_last", name.Trim());

    /// <summary>Печатать паспорт с двух сторон с переворотом относительно короткого края (см.
    /// DuplexPrinting). По умолчанию включено — так и задумано.</summary>
    public bool PassportDuplexShortEdge() => Get("passport_duplex_short_edge").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetPassportDuplexShortEdge(bool value) => Set("passport_duplex_short_edge", value ? "true" : "false");

    /// <summary>Развёрнута ли секция бокового меню «ДЛЯ НАЛАДЧИКА» (per-machine). В отличие от
    /// «ДОПОЛНИТЕЛЬНО», которая всегда открывается свёрнутой, эта секция помнит своё состояние: в неё
    /// заходят за работой (параметры, наклейки, паспорта), а не «посмотреть настройку раз в месяц», и
    /// сворачивать её заново на каждый запуск значило бы мешать тому, кто ей пользуется каждый день.</summary>
    public bool SidebarSetupExpanded() => Get("sidebar_setup_expanded").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetSidebarSetupExpanded(bool value) => Set("sidebar_setup_expanded", value ? "true" : "false");

    /// <summary>Размер этикетки пишется через точку (InvariantCulture): 97,5 на машине с русской
    /// локалью и 97.5 на английской — это одно и то же значение, и на чужой машине оно не должно
    /// превращаться в 975. Читаем обе записи, пишем всегда через точку.</summary>
    private static double ParseMm(string raw, double fallback)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) return v;
        if (double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) return v;
        return fallback;
    }

    /// <summary>Сетевая папка с релизными .exe приложения (см. AppUpdateService) — отдельная от root_path,
    /// т.к. обновление приложения логически не связано с диском прошивок.</summary>
    public string AppUpdatePath() => Get("app_update_path");
    public void SetAppUpdatePath(string path) => Set("app_update_path", path);

    /// <summary>Общая папка обновлений — задаётся администратором один раз и синхронизируется на все
    /// машины (см. UpdateFolderResolver). Сама по себе НЕ используется: работает
    /// <see cref="EffectiveAppUpdatePath"/>.</summary>
    public string AppUpdatePathShared() => Get("app_update_path_shared");
    public void SetAppUpdatePathShared(string path) => Set("app_update_path_shared", path);

    /// <summary>Папка обновлений, которая реально используется на ЭТОЙ машине: локальная настройка,
    /// если задана, иначе общая (относительная — от root_path этой машины). Пусто = папки нет,
    /// источник обновлений — GitHub. Единственная точка, откуда путь берут проверка обновлений при
    /// запуске, ручная проверка в Настройках и экран «Состояние подключения» — чтобы все трое
    /// говорили об одной и той же папке.</summary>
    public string EffectiveAppUpdatePath() =>
        UpdateFolderResolver.Resolve(AppUpdatePath(), AppUpdatePathShared(), RootPath());

    /// <summary>Если включено (по умолчанию) — найденное при запуске обновление ставится без
    /// подтверждения. Если выключено — показывается плашка с кнопкой «Обновить», которую нажимает
    /// пользователь.</summary>
    public bool AppAutoUpdate() => Get("app_auto_update").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetAppAutoUpdate(bool value) => Set("app_auto_update", value ? "true" : "false");

    /// <summary>Если включено (по умолчанию) — поиск по прошивкам/параметрам/схемам, не нашедший
    /// ничего по запросу как он введён, повторяет попытку с раскладкой клавиатуры "наоборот"
    /// (см. SearchService.ConvertLayout) — на случай, если оператор забыл переключить раскладку.
    /// Выключение полностью отключает и саму подстановку, и всплывающий вопрос "это точно оно?".</summary>
    public bool LayoutFallbackEnabled() => Get("layout_fallback_enabled").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetLayoutFallbackEnabled(bool value) => Set("layout_fallback_enabled", value ? "true" : "false");

    /// <summary>How many consecutive net "да"/"нет" answers for the exact same query it takes before
    /// Database.RecordLayoutFallbackFeedback stops asking and either always applies the layout
    /// conversion or stops trying it — replaces what used to be the hardcoded
    /// Database.LayoutFallbackDecisionThreshold (still the default here and in the DB layer).</summary>
    public int LayoutFallbackThreshold() =>
        int.TryParse(Get("layout_fallback_threshold"), out var v) && v > 0 ? v : Data.Database.LayoutFallbackDecisionThreshold;
    public void SetLayoutFallbackThreshold(int value) => Set("layout_fallback_threshold", Math.Max(1, value).ToString());

    /// <summary>Сколько одинаковых ответов подряд на вопрос «это та прошивка, которую вы искали?» превращаются
    /// в решение — после этого вопрос не задаётся (см. Database.RecordFwUsageConfirmFeedback). Общая
    /// с раскладкой настройка: обучение устроено одинаково, и держать оператору два разных порога
    /// «через сколько программа перестанет переспрашивать» незачем.</summary>
    public int UsageConfirmThreshold() => LayoutFallbackThreshold();

    /// <summary>Спрашивать ли перед тем, как засчитать выбор прошивки в статистику. Выключение
    /// убирает вопрос вместе с самим сбором: считать «выбрали эту» по факту любого нажатия — то, от
    /// чего вопрос и защищает (случайно открыл не ту карточку — версия поехала вверх в выдаче).</summary>
    public bool UsageConfirmEnabled() => Get("usage_confirm_enabled") is not "false";
    public void SetUsageConfirmEnabled(bool value) => Set("usage_confirm_enabled", value ? "true" : "false");

    /// <summary>Момент последнего сброса статистики выборов прошивки. Настройка НЕ локальная — она
    /// намеренно уезжает в общий конфиг: статистика общая, и сброс должен доехать до всех машин,
    /// иначе первый же чужой снимок вернул бы старые числа. Каждая машина помнит отдельно, какой
    /// сброс она уже выполнила у себя (FwUsageResetAppliedAt, локальный ключ).</summary>
    public string FwUsageResetAt() => Get("fw_usage_reset_at");
    public void SetFwUsageResetAt(string iso) => Set("fw_usage_reset_at", iso);

    public string FwUsageResetAppliedAt() => Get("fw_usage_reset_applied_at");
    public void SetFwUsageResetAppliedAt(string iso) => Set("fw_usage_reset_applied_at", iso);

    /// <summary>Отметка времени самого свежего переписывания hw модификации (см. hw_rewrite_log /
    /// ExportedHwRewrite), которое ЭТА машина уже проиграла у себя — локальный watermark, ровно как
    /// FwUsageResetAppliedAt рядом. По нему ConfigSyncService.ReplayHwRewrites берёт из общего снимка
    /// только строго более новые события и переименовывает свои строки прошивок, вместо того чтобы
    /// получить дубли. На машине оператора-автора продвигается сразу при самой правке (чтобы не
    /// проигрывать своё же). Per-machine — в общий конфиг не уезжает (SkipSettingsKeys).</summary>
    public string HwRewriteAppliedAt() => Get("hw_rewrite_applied_at");
    public void SetHwRewriteAppliedAt(string iso) => Set("hw_rewrite_applied_at", iso);

    /// <summary>Тот же локальный watermark, что HwRewriteAppliedAt выше, но для переносов версий на
    /// другую модель контроллера (ctrl_reassign_log / ExportedCtrlReassign / ConfigSyncService.
    /// ReplayCtrlReassigns). Per-machine — в общий конфиг не уезжает (SkipSettingsKeys).</summary>
    public string CtrlReassignAppliedAt() => Get("ctrl_reassign_applied_at");
    public void SetCtrlReassignAppliedAt(string iso) => Set("ctrl_reassign_applied_at", iso);

    /// <summary>Сколько раз версию должны выбрать по ОДНОМУ И ТОМУ ЖЕ запросу, прежде чем эта частота
    /// начнёт двигать выдачу поиска (см. Database.SearchFwVersions/EffectiveUsage). По смыслу — тот же
    /// вид настройки, что и LayoutFallbackThreshold выше (числовой порог чувствительности к
    /// накопленной статистике, а не сама статистика), и та per-machine — эта задумана так же: две
    /// машины видят одну и ту же общую статистику выборов (см. FwUsageResetAt), но насколько
    /// чувствительно ранжирование к ней — личная настройка чувствительности поиска на конкретном
    /// компьютере, а не орг-политика. ⚠️ Чтобы это действительно не синхронизировалось, ключ
    /// "fw_usage_threshold" нужно добавить в ConfigSyncService.SkipSettingsKeys (тот файл вне зоны
    /// этой правки — см. отчёт).</summary>
    public int FwUsageThreshold() =>
        int.TryParse(Get("fw_usage_threshold"), out var v) && v > 0 ? v : 2;
    public void SetFwUsageThreshold(int value) => Set("fw_usage_threshold", Math.Max(1, value).ToString());

    /// <summary>На сколько умножать вклад счётчика открытий в ранг выдачи (см. Database.Search.cs
    /// AutoUsageBonus и Defaults["fw_usage_multiplier"]). ≥0; 1 по умолчанию. Хранится и читается в
    /// инвариантной культуре, чтобы «1.5» одинаково понималось на машинах с разной локалью. В отличие
    /// от порога — СИНХРОНИЗИРУЕМЫЙ параметр (в общий конфиг уходит), см. его doc в Defaults.</summary>
    public double FwUsageMultiplier() =>
        double.TryParse(Get("fw_usage_multiplier"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : 1;
    public void SetFwUsageMultiplier(double value) =>
        Set("fw_usage_multiplier", Math.Max(0, value).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Максимальный вклад одного лишь счётчика открытий в ранг при текущем множителе —
    /// ориентир для оператора, задающего ручной вес: вес выше этого числа гарантированно поднимает
    /// версию над самой популярной с тем же совпадением (см. Database.Search.cs MaxUsageBonus=5 и
    /// AutoUsageBonus). Считается здесь, чтобы Настройки могли показать актуальное число, не завися от
    /// приватной константы ядра.</summary>
    public int FwUsageMaxAutoBonus() => (int)Math.Round(5 * FwUsageMultiplier(), MidpointRounding.AwayFromZero);

    /// <summary>Делиться ли ручным весом выдачи с другими машинами (см.
    /// Defaults["fw_weight_shared"] и Database.FwUsage.cs ExportFwUsage). Per-machine — в
    /// ConfigSyncService.SkipSettingsKeys, каждая машина решает за свой вес.</summary>
    public bool FwWeightShared() => Get("fw_weight_shared").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetFwWeightShared(bool value) => Set("fw_weight_shared", value ? "true" : "false");

    /// <summary>Автоматически подтягивать найденные поиском прошивки в локальный кэш, вместо кнопки
    /// «Синхронизировать» на каждой карточке (см. SearchView.AutoSyncMissing). Настройка личная,
    /// per-machine: локальный кэш — свойство конкретного ноутбука наладчика, а не орг-политика.</summary>
    public bool SearchAutoSync() => Get("search_auto_sync").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetSearchAutoSync(bool value) => Set("search_auto_sync", value ? "true" : "false");

    /// <summary>Необязательный путь к папке Segnetics Loader, GUI exe или Automation exe.
    /// Пустое значение выбирает Loader, поставляемый вместе с приложением.</summary>
    public string LoaderExePath() => Get("loader_exe_path");
    public void SetLoaderExePath(string path) => Set("loader_exe_path", path.Trim());

    /// <summary>Старые раздельные ключи сохраняются в конфигурации для совместимости. В текущем UI
    /// они образуют одну атомарную опцию подготовки ПЛК.</summary>
    public bool LoaderFormatDefault() => Get("loader_format_default").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetLoaderFormatDefault(bool value) => Set("loader_format_default", value ? "true" : "false");

    public bool LoaderUpdateKernelDefault() => Get("loader_update_kernel_default").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetLoaderUpdateKernelDefault(bool value) => Set("loader_update_kernel_default", value ? "true" : "false");

    public bool LoaderFormatAndUpdateDefault() => LoaderFormatDefault() && LoaderUpdateKernelDefault();

    public void SetLoaderFormatAndUpdateDefault(bool value)
    {
        SetLoaderFormatDefault(value);
        SetLoaderUpdateKernelDefault(value);
    }


    /// <summary>Как подключаемся к ПЛК: "usb" / "ethernet" / пусто («не трогать выбор в самом
    /// Loader»). Переносится в настройки Segnetics Loader перед запуском операции — см.
    /// <see cref="LoaderConnectionSettings"/> и комментарий у ключа в Defaults.</summary>
    public string LoaderConnectionMode() => Get("loader_connection_mode");
    public void SetLoaderConnectionMode(string mode) => Set("loader_connection_mode", mode.Trim().ToLowerInvariant());

    /// <summary>Адрес ПЛК для Ethernet-подключения. Для USB не используется.</summary>
    public string LoaderPlcIp() => Get("loader_plc_ip");
    public void SetLoaderPlcIp(string ip) => Set("loader_plc_ip", ip.Trim());

    /// <summary>Имя сетевого адаптера, через который идти к ПЛК (у наладчика их обычно два: рабочая
    /// сеть и переходник USB-Ethernet в шкаф). Пусто — выбор Loader'а не трогаем.</summary>
    public string LoaderNetworkAdapter() => Get("loader_network_adapter");
    public void SetLoaderNetworkAdapter(string adapter) => Set("loader_network_adapter", adapter.Trim());

    public bool LoaderCheckLink() => Get("loader_check_link").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetLoaderCheckLink(bool value) => Set("loader_check_link", value ? "true" : "false");

    public int LoaderLinkTimeoutMs() =>
        int.TryParse(Get("loader_link_timeout_ms"), out var ms) && ms > 0 ? ms : 1500;
    public void SetLoaderLinkTimeoutMs(int ms) => Set("loader_link_timeout_ms", ms.ToString());

    /// <summary>Папка осмотра (фото/сканы). Defaults to LocalFw if not set.</summary>
    public string InspectionFolder()
    {
        var v = Get("inspection_folder");
        return string.IsNullOrEmpty(v) ? LocalFw : v;
    }
    public void SetInspectionFolder(string path) => Set("inspection_folder", path);

    /// <summary>DPI used both to request a resolution from the scanner (best-effort — not every
    /// driver honors it) and to size the resulting PDF page to the document's real physical size.</summary>
    public int ScanResolutionDpi() => int.TryParse(Get("scan_resolution_dpi"), out var v) && v > 0 ? v : 200;
    public void SetScanResolutionDpi(int dpi) => Set("scan_resolution_dpi", dpi.ToString());

    /// <summary>exported_at value of the last shared config this machine actually applied — lets
    /// the startup check tell "a newer export exists on the share" from "we're already current".</summary>
    public string ConfigLastSyncedAt() => Get("config_last_synced_at");
    public void SetConfigLastSyncedAt(string exportedAt) => Set("config_last_synced_at", exportedAt);

    /// <summary>When this machine last successfully READ the shared config file — set on every
    /// check (background tick or manual button), whether or not it found anything new to apply.
    /// Deliberately separate from ConfigLastSyncedAt (which only moves on an actual Apply): if this
    /// timestamp isn't advancing, the pull side isn't running at all (dead timer, unreachable share);
    /// if it IS advancing but ConfigLastSyncedAt stays put, checks are running and genuinely finding
    /// nothing to apply. Surfaced on Настройки → Сетевые диски so a "sync isn't arriving" report can
    /// actually be narrowed down instead of guessed at. Per-machine only — never synced (see
    /// ConfigSyncService.SkipSettingsKeys).</summary>
    public string ConfigLastCheckedAt() => Get("config_last_checked_at");
    public void SetConfigLastCheckedAt(string at) => Set("config_last_checked_at", at);

    /// <summary>exported_at value this machine last wrote to the share (manual "Отправить сейчас" or
    /// the administrator's auto-push timer) — surfaced passively on NetworkSyncView instead of a
    /// status-bar toast on every auto-push tick.</summary>
    public string ConfigLastPushedAt() => Get("config_last_pushed_at");
    public void SetConfigLastPushedAt(string exportedAt) => Set("config_last_pushed_at", exportedAt);

    /// <summary>UpdatedAt самого свежего тикета, который эта машина уже показывала оператору (страницу
    /// «Тикеты» открывали при этом состоянии). По нему бейдж на пункте меню считает «сколько тикетов/
    /// смен статуса пришло с других машин с тех пор» — раньше о новом тикете не узнавали вовсе, пока
    /// сами не заходили на страницу (PullNewEvents срабатывал только при её открытии). Per-machine —
    /// у каждого компьютера свой момент «последнего просмотра», в общий конфиг не уходит (см.
    /// ConfigSyncService.SkipSettingsKeys).</summary>
    public string TicketsLastSeenAt() => Get("tickets_last_seen_at");
    public void SetTicketsLastSeenAt(string at) => Set("tickets_last_seen_at", at);

    /// <summary>Подробный режим синхронизации (кнопка на «Сетевые диски») — когда включён, каждый
    /// фоновый тик приёма/отправки/тикетов пишет в статус-строку, ЧТО именно синхронизировалось
    /// (обычно всё это происходит молча). Нужен, чтобы отследить «что синхронится, а что нет», не
    /// гадая. Per-machine, в общий конфиг не уходит (см. ConfigSyncService.SkipSettingsKeys).</summary>
    public bool SyncVerbose() => Get("sync_verbose") == "true";
    public void SetSyncVerbose(bool value) => Set("sync_verbose", value ? "true" : "false");

    /// <summary>"ldap" (default — unchanged behaviour for existing installs) = only способ №1 (прямой
    /// LDAP-бинд, требует сетевого доступа к контроллеру домена); "http" = только способ №2 (HTTP-
    /// запрос к AdHttpUrl() с NTLM/Negotiate); "both" = пробовать LDAP, и только если домен
    /// недоступен (не если пароль неверный) — попробовать HTTP как запасной вариант. См.
    /// AntarusPoFinder.App.AdCredentialValidatorFactory за тем, как это значение превращается в
    /// конкретный IAdCredentialValidator.</summary>
    /// <remarks>"oidc" — четвёртый вариант: вход через корпоративный SSO (Keycloak/OpenID Connect),
    /// пароля приложение при этом не видит вовсе. Добавлен так же, как когда-то "http": прежние
    /// значения и их поведение не тронуты, установка, ничего не менявшая, остаётся на "ldap".</remarks>
    public string AdAuthMode() => Get("ad_auth_mode") switch
    {
        "http" => "http",
        "both" => "both",
        "oidc" => "oidc",
        _ => "ldap",
    };
    public void SetAdAuthMode(string mode) => Set("ad_auth_mode", mode is "http" or "both" or "oidc" ? mode : "ldap");

    /// <summary>Адрес realm Keycloak (или любого другого OpenID-провайдера): именно от него строится
    /// адрес описания <c>{authority}/.well-known/openid-configuration</c>, из которого берутся все
    /// остальные адреса. Пусто — SSO не настроен, и режим "oidc" выбрать нельзя (см. OidcConfigured).</summary>
    public string OidcAuthority() => Get("oidc_authority");
    public void SetOidcAuthority(string url) => Set("oidc_authority", url.Trim().TrimEnd('/'));

    /// <summary>client_id этого приложения в Keycloak. Секрета нет и быть не должно: настольное
    /// приложение — публичный клиент, безопасность даёт PKCE, а не спрятанный в exe секрет.</summary>
    public string OidcClientId() => Get("oidc_client_id");
    public void SetOidcClientId(string id) => Set("oidc_client_id", id.Trim());

    /// <summary>Из какого claim'а токена брать группы пользователя — по ним считается роль в
    /// приложении теми же тремя настройками ad_group_*, что и для AD.</summary>
    public string OidcGroupsClaim() => Get("oidc_groups_claim") is { Length: > 0 } c ? c : "groups";
    public void SetOidcGroupsClaim(string claim) => Set("oidc_groups_claim", claim.Trim());

    /// <summary>Настроен ли SSO настолько, чтобы им можно было входить.</summary>
    public bool OidcConfigured() =>
        !string.IsNullOrWhiteSpace(OidcAuthority()) && !string.IsNullOrWhiteSpace(OidcClientId());

    /// <summary>Чем обмениваться общими данными: "fileshare" (сетевая папка, как сегодня) или
    /// "server" (HTTP + живые уведомления по WebSocket). Переключение на сервер — решение, которое
    /// отрезает машины со старой версией программы от обновлений справочника, поэтому по умолчанию
    /// файловая папка и остаётся (docs/client-server-plan.md, раздел 6).</summary>
    public string SyncTransport() => Get("sync_transport") == "server" ? "server" : "fileshare";
    public void SetSyncTransport(string kind) => Set("sync_transport", kind == "server" ? "server" : "fileshare");

    /// <summary>Базовый адрес сервера приложения (когда он появится). От него же строится адрес
    /// живых уведомлений: та же машина и путь /ws, схема http→ws, https→wss.</summary>
    public string ServerUrl() => Get("server_url");
    public void SetServerUrl(string url) => Set("server_url", url.Trim().TrimEnd('/'));

    /// <summary>Базовый URL внутреннего веб-сервера компании для способа №2 (HTTP-проверка пароля,
    /// см. HttpAdCredentialValidator). По умолчанию предустановлен рабочий адрес диска предприятия
    /// (https://disk.antarus.su/cloud, см. Defaults); администратор может сменить его в Настройки →
    /// Общие или в «Доп. параметрах» окна входа, если IT поменяет формат (см. AdHttpUrlPlaceholder
    /// в SettingsView для подсказки в самом поле). Синхронизируемая политика входа — задаётся один
    /// раз администратором и доезжает до всех машин (нет в ConfigSyncService.SkipSettingsKeys).</summary>
    public string AdHttpUrl() => Get("ad_http_url");
    public void SetAdHttpUrl(string url) => Set("ad_http_url", url.Trim());

    /// <summary>Пароль программиста — пустая строка означает «пароль не задан»
    /// (см. VerifyProgrammerPassword), непустая — всегда хеш, никогда открытый текст.</summary>
    public string ProgrammerPassword() => Get("programmer_password");

    /// <summary>Хеширует и сохраняет новый пароль администратора. В отличие от программиста,
    /// у администратора нет режима «пароль не задан» — пустая строка здесь так и хешируется
    /// (пустой пароль), это осознанное поведение вызывающей стороны, а не сигнал «не менять».</summary>
    public void SetAdminPassword(string plainPassword) => Set("admin_password", PasswordHasher.Hash(plainPassword ?? ""));

    /// <summary>Хеширует и сохраняет новый пароль программиста — кроме одного случая: пустая
    /// строка сохраняется как есть (не хешируется), потому что пустое значение — это сигнал
    /// «пароль для роли программиста не требуется» (см. VerifyProgrammerPassword), а не «пароль —
    /// пустая строка». Если бы пустая строка тоже хешировалась, отличить один случай от другого
    /// было бы уже нельзя (оба выглядели бы как валидный непустой хеш).</summary>
    public void SetProgrammerPassword(string plainPassword) =>
        Set("programmer_password", string.IsNullOrEmpty(plainPassword) ? "" : PasswordHasher.Hash(plainPassword));

    /// <summary>Единственное место, которое должно сравнивать введённый пароль администратора с
    /// сохранённым — заменяет прежнее прямое сравнение строк (input == AdminPassword()), которое
    /// сравнивало открытый текст с открытым текстом; теперь сохранённое значение — хеш, поэтому
    /// сравнение обязано идти через PasswordHasher.Verify (соль, число итераций, сравнение с
    /// постоянным временем — см. класс).</summary>
    public bool VerifyAdminPassword(string input) => PasswordHasher.Verify(input ?? "", Get("admin_password"));

    /// <summary>Как VerifyAdminPassword, но с сохранением прежней логики «пустой пароль
    /// программиста = проверка не требуется, вход в роль пускает без пароля» — это поведение уже
    /// было в коде до этого фикса (см. RoleSwitchDialog/SettingsView до правки:
    /// `!string.IsNullOrEmpty(ProgrammerPassword()) &amp;&amp; password != ProgrammerPassword()`),
    /// здесь оно просто перенесено внутрь ConfigService вместе с самой проверкой.</summary>
    public bool VerifyProgrammerPassword(string input)
    {
        var stored = Get("programmer_password");
        return string.IsNullOrEmpty(stored) || PasswordHasher.Verify(input ?? "", stored);
    }

    public string CurrentRole() => Get("current_role");
    public void SetRole(string role) => Set("current_role", role);

    public string Theme() => Get("theme");
    public void SetTheme(string theme) => Set("theme", theme);

    public bool KeepArchives() => Get("keep_archives").Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>0 = automatic pull sync disabled on this machine (see MainWindowViewModel.StartTimers) —
    /// manual "Синхронизировать сейчас" still works either way. Только чтение: поле настройки убрано
    /// из «Сетевых дисков» (Задача 2 — маркер ревизии опрашивается на фиксированном коротком
    /// интервале), значение остаётся ради тех баз, где его когда-то выставили.</summary>
    public int SyncIntervalMin()
    {
        return int.TryParse(Get("sync_interval_min"), out var v) ? Math.Max(0, v) : 5;
    }

    /// <summary>Administrator-only: periodically export the local config to the shared drive so
    /// naladchik/programmer clients pick up hierarchy/tag/reservation changes without the admin
    /// having to remember to click "Отправить сейчас" — see Настройки → Сетевые диски →
    /// "ОТПРАВКА ИЗМЕНЕНИЙ НА ДИСК". 0 = automatic push disabled (see
    /// MainWindowViewModel.RefreshConfigSync) — manual "Отправить сейчас" still works either way.
    /// No separate on/off checkbox — used to have one (config_auto_push), removed as a redundant
    /// second way to express what this field's own 0-means-off already covered, same pattern as
    /// sync_interval_min/inspection_auto_cleanup_days. Defaults to 0 (off) for fresh installs.</summary>
    public int ConfigPushIntervalMin()
    {
        return int.TryParse(Get("config_push_interval_min"), out var v) ? Math.Max(0, v) : 0;
    }
    public void SetConfigPushIntervalMin(int minutes) => Set("config_push_interval_min", Math.Max(0, minutes).ToString());

    /// <summary>Whether the first-launch interactive onboarding tour has already run on this machine
    /// (per-machine, not synced — see ConfigSyncService.SkipSettingsKeys). The manual replay button
    /// in MainWindow ignores this flag entirely; it only gates the automatic one-time trigger.</summary>
    public bool OnboardingShown() => Get("onboarding_shown").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetOnboardingShown(bool value) => Set("onboarding_shown", value ? "true" : "false");

    /// <summary>Default lifetime for a new version reservation before Database.ExpireStaleReservations
    /// auto-cancels it — see Настройки → Резервация номеров. 0 = reservations never expire by default;
    /// a programmer can still override this per-reservation (see UploadView.ReserveVersion_Click).</summary>
    public int ReservationTtlHours() => int.TryParse(Get("reservation_ttl_hours"), out var v) && v >= 0 ? v : 72;
    public void SetReservationTtlHours(int hours) => Set("reservation_ttl_hours", Math.Max(0, hours).ToString());

    public List<QuickApp> QuickApps()
    {
        // Self-healing best-effort: a corrupted/pre-migration value here just means the quick-apps
        // row starts back at empty (visibly so — the operator sees an empty list and re-adds them)
        // rather than the app failing to start over one bad setting value.
        try { return JsonSerializer.Deserialize<List<QuickApp>>(Get("quick_apps")) ?? new(); }
        catch { return new(); }
    }

    public void SetQuickApps(List<QuickApp> apps) => Set("quick_apps", JsonSerializer.Serialize(apps));

    public int ImageServerPort() => int.TryParse(Get("image_server_port"), out var v) ? v : 9876;

    /// <summary>Local-cache directory names (see LocalFirmwareCache.SanitizeName) the user has opted
    /// into silent auto-update for — everything else just surfaces in the "Обновить" banner/window.</summary>
    public HashSet<string> FwAutoUpdateDirs()
    {
        // Same self-healing reasoning as QuickApps above: a corrupted value falls back to "nothing
        // opted into auto-update" (the safe default — everything just surfaces via the manual
        // banner/window instead), not a startup failure.
        try { return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(Get("fw_auto_update_dirs")) ?? new(), StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    public bool IsFwAutoUpdate(string localDir) => FwAutoUpdateDirs().Contains(localDir);

    public void SetFwAutoUpdate(string localDir, bool enabled)
    {
        var set = FwAutoUpdateDirs();
        if (enabled) set.Add(localDir); else set.Remove(localDir);
        Set("fw_auto_update_dirs", JsonSerializer.Serialize(set.ToList()));
    }

    /// <summary>Per-machine (not synced — see ConfigSyncService.SkipSettingsKeys), like scan_resolution_dpi/
    /// onboarding_shown: what one operator wants muted on their PC has nothing to do with another
    /// machine's preferences. All categories are enabled by default (empty disabled-set) so adding
    /// this feature doesn't silently mute anything for existing installs.</summary>
    public HashSet<NotificationCategory> DisabledNotificationCategories()
    {
        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(Get("notification_categories_disabled")) ?? new();
            return new HashSet<NotificationCategory>(names
                .Select(n => Enum.TryParse<NotificationCategory>(n, out var c) ? (NotificationCategory?)c : null)
                .Where(c => c.HasValue)
                .Select(c => c!.Value));
        }
        // Corrupted value falls back to "nothing disabled" — the safe default per the doc above
        // (every category enabled), not a startup failure over one bad setting.
        catch { return new HashSet<NotificationCategory>(); }
    }

    public bool IsNotificationCategoryEnabled(NotificationCategory category) =>
        !DisabledNotificationCategories().Contains(category);

    public void SetNotificationCategoryEnabled(NotificationCategory category, bool enabled)
    {
        var set = DisabledNotificationCategories();
        if (enabled) set.Remove(category); else set.Add(category);
        Set("notification_categories_disabled", JsonSerializer.Serialize(set.Select(c => c.ToString()).ToList()));
    }

    /// <summary>Separate from DisabledNotificationCategories above — a category can stay fully
    /// enabled (still shows in the status bar / still lands in history) while being excluded from the
    /// unread badge count on the "Уведомления" sidebar button, for chatty-but-low-priority categories
    /// the operator doesn't want bumping the badge every time (e.g. Sync's routine "Путь сохранён"
    /// toasts). Per-machine, same as notification_categories_disabled — see ConfigSyncService.
    /// SkipSettingsKeys. All categories count toward the badge by default.</summary>
    public HashSet<NotificationCategory> MutedFromUnreadNotificationCategories()
    {
        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(Get("notification_categories_muted_unread")) ?? new();
            return new HashSet<NotificationCategory>(names
                .Select(n => Enum.TryParse<NotificationCategory>(n, out var c) ? (NotificationCategory?)c : null)
                .Where(c => c.HasValue)
                .Select(c => c!.Value));
        }
        // Same self-healing fallback as DisabledNotificationCategories above: corrupted value ->
        // "nothing muted" (every category still counts toward the badge, the safe default).
        catch { return new HashSet<NotificationCategory>(); }
    }

    public bool IsNotificationCategoryCountedUnread(NotificationCategory category) =>
        !MutedFromUnreadNotificationCategories().Contains(category);

    public void SetNotificationCategoryCountedUnread(NotificationCategory category, bool counted)
    {
        var set = MutedFromUnreadNotificationCategories();
        if (counted) set.Remove(category); else set.Add(category);
        Set("notification_categories_muted_unread", JsonSerializer.Serialize(set.Select(c => c.ToString()).ToList()));
    }

    /// <summary>"close" = закрытие окна завершает процесс как раньше (default — не менять поведение
    /// для существующих установок без явного выбора пользователя); "tray" = сворачивать в системный
    /// трей вместо закрытия. Per-machine — трей на одном ПК не должен навязываться другому.</summary>
    public string CloseAction() => Get("close_action");
    public void SetCloseAction(string action) => Set("close_action", action);

    /// <summary>Per-machine (not synced — same reasoning as close_action/theme): whether the window
    /// should start minimized regardless of how the process was launched (double-click, or the
    /// Windows autostart Run-key entry — see AutostartService in AntarusPoFinder.App, which is the
    /// source of truth for whether autostart itself is on, not a setting stored here). Read once at
    /// startup by App.OnStartup, before the window is first shown.</summary>
    public bool AppStartMinimized() => Get("app_start_minimized").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetAppStartMinimized(bool value) => Set("app_start_minimized", value ? "true" : "false");

    /// <summary>0 (default for new installs — never surprise anyone with unexpected deletion) means
    /// auto-cleanup of the Осмотр folder is off. Any other N means files older than N minutes get
    /// deleted from it periodically — see InspectionCleanupService.Cleanup, called from
    /// MainWindowViewModel.RunSync alongside the app's other periodic background checks. Per-machine
    /// (not synced — see ConfigSyncService.SkipSettingsKeys), same reasoning as inspection_folder
    /// itself: what one operator wants cleaned has nothing to do with another machine's folder.
    ///
    /// Round 34: widened from whole days (old key inspection_auto_cleanup_days) to minutes, so the
    /// UI can offer days/hours/minutes inputs instead of days only. The new key defaults to "" (an
    /// explicit "never configured on this machine" sentinel, distinct from "0") — as long as it's
    /// unset, this reads the OLD days key instead and converts it, so an existing install that had
    /// already configured e.g. "5 days" doesn't silently have its cleanup disabled just because it
    /// upgraded before ever touching the new inputs. The very first time this machine saves through
    /// the new UI (even to explicitly disable it, 0/0/0), the new key is written and takes over for
    /// good — the old key is left in place but never consulted again after that.</summary>
    public int InspectionAutoCleanupMinutes()
    {
        var raw = Get("inspection_auto_cleanup_minutes");
        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var minutes) && minutes >= 0)
            return minutes;

        var days = int.TryParse(Get("inspection_auto_cleanup_days"), out var d) && d >= 0 ? d : 0;
        return days * 24 * 60;
    }
    public void SetInspectionAutoCleanupMinutes(int minutes) => Set("inspection_auto_cleanup_minutes", Math.Max(0, minutes).ToString());

    /// <summary>"sidebar" (default — unchanged from how it always worked) = Быстрый доступ is a
    /// vertical list of labeled buttons at the bottom of the left sidebar's scrollable area; "top" =
    /// a horizontal row of round icon-only "dock" bubbles above the page content; "top_labeled" =
    /// same horizontal row, each bubble with its shortcut name captioned underneath. Purely a
    /// personal display preference for THIS machine (not synced — see ConfigSyncService.
    /// SkipSettingsKeys, same reasoning as close_action/theme), the underlying shortcut list itself
    /// (QuickApps()) is unaffected either way.</summary>
    public string QuickAppsDisplayMode() => Get("quick_apps_display_mode");
    public void SetQuickAppsDisplayMode(string mode) =>
        Set("quick_apps_display_mode", mode is "top" or "top_labeled" ? mode : "sidebar");

    /// <summary>Administrator-only, per-machine (this computer's policy, like every other AD setting
    /// below it — see ConfigSyncService.SkipSettingsKeys). On by default (единая политика для всех
    /// машин): App.OnStartup shows AdStartupLoginDialog before MainWindow unless this machine already
    /// has a still-valid cached session (see AdSessionService) for whichever login last authenticated
    /// here (AdLastLogin below). A machine that explicitly saved "false" keeps it (fallback doesn't
    /// override an explicit value); the administrator escape password is always available in the
    /// gate, so a fresh machine with an unreachable domain is never locked out.</summary>
    public bool AdRequireLogin() => Get("ad_require_login").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetAdRequireLogin(bool value) => Set("ad_require_login", value ? "true" : "false");

    /// <summary>Default "remember me" period (days) offered in the AD login UI — used whenever the
    /// operator leaves the picker on "как задано администратором" instead of choosing their own
    /// number of days or "всегда". Replaces what used to be a hardcoded 14.</summary>
    public int AdRequireLoginDefaultDays() => int.TryParse(Get("ad_require_login_default_days"), out var v) && v > 0 ? v : 14;
    public void SetAdRequireLoginDefaultDays(int days) => Set("ad_require_login_default_days", Math.Max(1, days).ToString());

    /// <summary>Normalized AD login (see AppUserAuthService.NormalizeAdLogin) that last successfully
    /// authenticated on THIS machine — the one AdRequireLogin's startup gate checks a cached session
    /// for. Set on every successful AD login (mandatory gate or the optional in-app switch-role
    /// dialog), never on the administrator escape hatch (that one is deliberately never cached).</summary>
    public string AdLastLogin() => Get("ad_last_login");
    public void SetAdLastLogin(string normalizedLogin) => Set("ad_last_login", normalizedLogin);

    /// <summary>Бета-опция: на странице «Загрузка прошивки» вместо двух раздельных drag&amp;drop-зон
    /// (прошивка ПЛК сверху + отдельная HMI-зона под галочкой «Добавить HMI») показывается ОДНА общая
    /// зона — файл/папку ПЛК и HMI-проект можно кинуть в неё вместе или по очереди, а приложение само
    /// определяет, что есть что, по расширению файла (см. UploadView.ClassifyAndAssignOne); если
    /// определить однозначно не удалось — переспрашивает диалогом. Выключено по умолчанию: раздельные
    /// зоны — проверенное поведение, единая зона — новая экспериментальная функция, которая может
    /// ошибиться в распознавании файлов на нестандартной структуре проекта. Задумана как per-machine
    /// настройка, как и остальные поля вкладки «Общие» (переключатель решает, как выглядит форма
    /// загрузки НА ЭТОМ компьютере, а не орг-политика) — НО в этой волне правок сознательно НЕ
    /// добавлена в ConfigSyncService.SkipSettingsKeys (синхронизация настроек вне рамок задачи), так
    /// что пока что значение может утечь в общий конфиг и подтянуться на другую машину при экспорте/
    /// импорте — если это станет проблемой, добавить "unified_plc_hmi_zone" в SkipSettingsKeys.</summary>
    public bool UnifiedPlcHmiZoneEnabled() => Get("unified_plc_hmi_zone").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetUnifiedPlcHmiZoneEnabled(bool value) => Set("unified_plc_hmi_zone", value ? "true" : "false");

    /// <summary>Версия, для которой окно «Что нового» уже показано/зачтено на этой машине — см.
    /// Defaults["last_whatsnew_shown_version"] и AppUpdateService.ShouldShowWhatsNew за полной логикой.</summary>
    public string LastWhatsNewShownVersion() => Get("last_whatsnew_shown_version");
    public void SetLastWhatsNewShownVersion(string version) => Set("last_whatsnew_shown_version", version);

    /// <summary>Сколько версий держим в журнале изменений: история «что нового» полезна на несколько
    /// релизов назад, но не бесконечно — старое всё равно уже неактуально, а раздувать настройку
    /// незачем.</summary>
    private const int AppChangelogLimit = 50;

    /// <summary>Постоянный журнал изменений приложения — от новых версий к старым. Битый JSON
    /// самолечится в пустой список (как и остальные JSON-настройки здесь), а не роняет запуск.</summary>
    public List<AppChangelogEntry> AppChangelogHistory()
    {
        try { return JsonSerializer.Deserialize<List<AppChangelogEntry>>(Get("app_changelog_history")) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Дописать в журнал изменений версию с её release notes. Дедуп по версии (одна строка на
    /// версию — иначе повторный показ «Что нового» той же версии сыпал бы дубли), новая всегда сверху,
    /// список подрезается до AppChangelogLimit. Пустую версию игнорируем.</summary>
    public void AddAppChangelogEntry(string version, string notes, DateTime seenAt)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        var list = AppChangelogHistory();
        list.RemoveAll(e => e.Version == version);
        list.Insert(0, new AppChangelogEntry(version, notes ?? "", seenAt));
        while (list.Count > AppChangelogLimit) list.RemoveAt(list.Count - 1);
        Set("app_changelog_history", JsonSerializer.Serialize(list));
    }
}
