using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App;

/// <summary>Shared service instances passed into every page's ViewModel — the WPF equivalent of
/// the Python app's "pages hold a back-reference to MainWindow" pattern, but as plain constructor
/// injection instead of a God-object reference.</summary>
public class AppServices
{
    public Database Db { get; }
    public ConfigService Cfg { get; }
    public HierarchyService Hierarchy { get; }
    public SchematicService Schematics { get; }

    /// <summary>Идущие прямо сейчас долгие операции (заливка в ПЛК, сборка LFS, перестройка диска).
    /// Один список на приложение — им же заменена та защита, которую раньше давала модальность окна:
    /// пока окно операции запирало программу, вторую такую же нажать было физически нельзя, а теперь
    /// это надо проверять явно. См. LongOperationRegistry.</summary>
    public LongOperationRegistry Operations { get; } = new();

    /// <summary>Set once per session on a successful AD login (see RoleSwitchDialog.AdAuth_Click),
    /// via either the AD-group or the app-roster path — null for the whole session if the operator
    /// only ever picked a role through the plain shared-password dialog, in which case CurrentUserName
    /// keeps falling back to the Windows/machine account exactly as before this existed.</summary>
    public string? CurrentAdLogin { get; set; }

    /// <summary>"Кто сейчас действует" for every CreatedBy/exported_by/"кем зарезервирован" field —
    /// the AD login if this session authenticated via AD, otherwise the shared Windows/machine
    /// account name that was the only "who" available before AD login threaded an identity through.
    /// Root cause this fixes: two colleagues both logging in via AD got the right roles, but every
    /// downstream audit field still said "наладка3" (the shared PC account), not "revkin.i".</summary>
    public string CurrentUserName => CurrentAdLogin ?? Environment.UserName;

    /// <summary>Выкладчик на хостинг по текущим настройкам — или null, если выкладывать некуда
    /// (ключи не выданы, выкладка выключена). Собирается здесь, а не по месту, ровно из-за одной
    /// детали: инструкция в формате Word уходит на хостинг собранным PDF, а конвертер живёт в
    /// приложении. Забудь его подставить в одном из вызовов — и с той страницы docx молча перестанет
    /// выкладываться.</summary>
    public IInstructionPublisher? Publisher() =>
        InstructionPublisher.For(Cfg.S3(), new Services.DocxToPdfConverter.Adapter(), StubWriter());

    /// <summary>Кто убирает выложенное с хостинга (удаление инструкции). Тот же самый объект, что и
    /// <see cref="Publisher"/>: выкладка и снятие — две стороны одного клиента, и null у них означает
    /// ровно одно и то же — «хостинг не настроен».</summary>
    public IInstructionUnpublisher? Unpublisher() => Publisher() as IInstructionUnpublisher;

    /// <summary>Рисовальщик заглушек «Инструкция в разработке» с текущим макетом. Через одну точку
    /// по той же причине, что и выкладчик выше: макет настраивается, и забудь его подставить в одном
    /// из шести вызовов — с той страницы заглушки поедут в старом виде, а заметят это уже по QR
    /// со шкафа.</summary>
    public Services.InstructionStubWriter StubWriter() => new(Cfg.StubLayouts());

    public AppServices()
    {
        Db = new Database(ConfigService.DbPath);
        Cfg = new ConfigService(Db);
        Hierarchy = new HierarchyService(Db);
        Schematics = new SchematicService();
    }

    /// <summary>Test-only seam: ConfigService.AppData/DbPath are `static readonly`, resolved once
    /// per process from ANTARUS_TEST_APPDATA — so a single test process can never construct two
    /// independent AppServices() instances representing two separate machines/profiles the normal
    /// way. This overload takes already-built, independently-pathed instances (see
    /// AntarusPoFinder.Tests EndToEndSyncTests) so ConfigSyncService/TicketSyncService — which take
    /// an AppServices, not a bare Database — can be exercised against two simulated machines sharing
    /// one on-disk "network drive" root in-process, instead of only unit-testing Database directly.</summary>
    public AppServices(Database db, ConfigService cfg, HierarchyService hierarchy)
    {
        Db = db;
        Cfg = cfg;
        Hierarchy = hierarchy;
        Schematics = new SchematicService();
    }
}
