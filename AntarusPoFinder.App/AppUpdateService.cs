using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App;

public enum UpdateSourceKind { Folder, GitHub }

/// <summary>Sha256Url (только для GitHub-источника) — прямая ссылка на ассет "&lt;exe&gt;.sha256"
/// того же релиза, если он есть (см. build.ps1 — генерирует его рядом с exe при сборке). Пусто,
/// если релиз собран до появления этого фикса и такого ассета в нём нет — тогда проверка
/// целостности пропускается (обратная совместимость, см. DownloadReleaseAsync).</summary>
public record UpdateRelease(Version Version, string FileName, UpdateSourceKind Source, string LocalPath = "", string DownloadUrl = "", long ExpectedSize = 0, string Sha256Url = "");

/// <summary><paramref name="FolderProblem"/> — заполнено, только когда папка обновлений задана, но
/// использовать её не вышло (шара недоступна, путь не существует): тогда источником стал GitHub, и
/// это НЕ штатная ситуация, а повод сказать об этом вслух — иначе «обновления идут с GitHub» и
/// «сетевая папка отвалилась» выглядят одинаково. null = папка не задана вовсе либо использована.</summary>
public record UpdateCheckResult(UpdateSourceKind Source, string SourceLabel, List<UpdateRelease> Releases, string? FolderProblem = null);

/// <summary>Состояние ОДНОГО источника обновлений — для окна «Состояние подключения» и для кнопки
/// «Проверить» в Настройках, где надо показать обе стороны сразу: что нашлось в папке, что нашлось
/// на GitHub, что установлено и что из этого будет использовано.</summary>
/// <param name="Configured">Источник вообще настроен (для папки — путь задан; GitHub настроен всегда).</param>
/// <param name="Available">До источника достучались и список версий получен.</param>
/// <param name="Location">Куда именно ходили — путь папки или репозиторий.</param>
/// <param name="LatestVersion">Самая новая найденная версия, null — если версий нет.</param>
/// <param name="Problem">Почему источник недоступен, человекочитаемо. null, если доступен.</param>
public record UpdateSourceStatus(bool Configured, bool Available, string Location, Version? LatestVersion, int ReleaseCount, string? Problem);

/// <summary>Обе стороны сразу + что из них будет использовано. <see cref="EffectiveSource"/> считается
/// по тому же правилу, что и в <see cref="AppUpdateService.CheckForUpdatesAsync"/> (папка приоритетнее
/// GitHub), null — если недоступны оба, то есть новые версии на эту машину не придут никак.</summary>
public record UpdateSourcesReport(UpdateSourceStatus Folder, UpdateSourceStatus GitHub, UpdateSourceKind? EffectiveSource, Version CurrentVersion);

/// <summary>Ни папка обновлений, ни GitHub не ответили. Отдельный тип исключения, а не «просто
/// сетевая ошибка GitHub», по прямому требованию: обе недоступности сразу — это ЯВНАЯ ошибка
/// («обновления на эту машину не придут»), а не «обновлений нет». Сообщение перечисляет обе
/// причины, чтобы разбор жалобы не начинался с угадывания, какая половина отвалилась.</summary>
public sealed class UpdateSourcesUnavailableException : Exception
{
    public string FolderPath { get; }
    public string FolderProblem { get; }

    public UpdateSourcesUnavailableException(string folderPath, string folderProblem, Exception gitHubError)
        : base($"Ни один источник обновлений недоступен. Папка «{folderPath}» — {folderProblem}. " +
               $"GitHub — {AppUpdateService.DescribeError(gitHubError)}", gitHubError)
    {
        FolderPath = folderPath;
        FolderProblem = folderProblem;
    }
}

/// <summary>Проверка и установка версий приложения (Настройки → Общие → Обновление приложения),
/// используется как оттуда, так и автоматической проверкой при запуске (см.
/// <c>MainWindowViewModel.CheckForAppUpdatesAsync</c>). Источник выбирается по одному правилу
/// в обоих местах: если задана сетевая папка обновлений — релизы ищутся там (файлы вида
/// <c>AntarusPoFinder-{version}.exe</c>); если нет — берутся GitHub Releases публичного
/// репозитория. Установка (в т.ч. откат на старую версию) одинакова для обоих источников: файл
/// (локальный или скачанный с GitHub) копируется рядом с текущим .exe, приложение закрывается,
/// bat-скрипт в %TEMP% дожидается завершения процесса, подменяет .exe и перезапускает его.</summary>
public static class AppUpdateService
{
    private const string GitHubOwner = "IlyaRevkin";
    private const string GitHubRepo = "antarus-po-finder-dotnet";
    public const string GitHubSourceLabel = $"репозиторий GitHub ({GitHubOwner}/{GitHubRepo}, публичный)";

    private static readonly Regex ReleaseFileRegex =
        new(@"^AntarusPoFinder-(\d+\.\d+\.\d+(?:\.\d+)?)\.exe$", RegexOptions.IgnoreCase);

    // Not `readonly` — see SetHttpClientForTests below, the seam AppUpdateServiceTests uses to
    // substitute a fake HttpMessageHandler instead of making real network calls to GitHub.
    private static HttpClient Http = CreateHttpClient();

    /// <summary>Test-only seam (AntarusPoFinder.Tests has InternalsVisibleTo access — see
    /// AntarusPoFinder.App/InternalsVisibleTo.cs): lets AppUpdateServiceTests point ListGitHubReleasesAsync/
    /// DownloadReleaseAsync at a fake HttpMessageHandler instead of the real GitHub API, so the
    /// release-listing/size-verification/network-error paths are covered deterministically and
    /// without depending on internet access in CI. Production code never calls this.</summary>
    internal static void SetHttpClientForTests(HttpClient client) => Http = client;

    /// <summary>Restores the real GitHub-facing client — call from test cleanup so a later test (or a
    /// later run in the same process) doesn't keep using a previous test's fake handler.</summary>
    internal static void ResetHttpClientForTests() => Http = CreateHttpClient();

    /// <summary>Some of the plant PCs this runs on are old enough that .NET/Schannel still default
    /// to TLS 1.0/1.1, which GitHub's API has stopped accepting — that surfaced as an "SSL connection
    /// could not be established" exception on "Проверить обновления"/startup check. Forcing TLS 1.2/1.3
    /// here doesn't depend on the OS-wide default.</summary>
    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AntarusPoFinder-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>Чистая (без побочных эффектов) логика решения «показывать ли окно "Что нового"» —
    /// вынесена сюда из MainWindowViewModel.CheckWhatsNewAsync специально, чтобы её можно было
    /// протестировать без WPF/Dispatcher/сети (см. AppUpdateServiceTests.ShouldShowWhatsNew_*).
    /// <paramref name="lastShownVersion"/> — значение ключа ConfigService "last_whatsnew_shown_version".
    /// Три случая (см. постановку задачи):
    /// • пусто (ключ ещё ни разу не писали — самая первая установка/первый запуск этой версии ключей)
    ///   → false: показывать нечего "что нового" относительно ничего, но вызывающая сторона обязана
    ///   молча записать текущую версию, чтобы СЛЕДУЮЩЕЕ реальное обновление такое сравнение уже прошло;
    /// • отличается от текущей версии → true: приложение только что обновилось;
    /// • совпадает с текущей → false: уже показывали (или записали) для этой версии, второй раз не надо.</summary>
    public static bool ShouldShowWhatsNew(string? lastShownVersion, string currentVersion) =>
        !string.IsNullOrEmpty(lastShownVersion) &&
        !string.Equals(lastShownVersion, currentVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Текущая версия в компактном 3-компонентном виде ("1.32.0"), без сборочной ревизии —
    /// AssemblyVersion (см. csproj &lt;Version&gt;) всегда несёт и четвёртый компонент (revision,
    /// обычно 0), который ничего не говорит пользователю и не совпадает с тем, что видно в тегах
    /// GitHub-релизов/имени exe. Единственный источник форматирования версии для отображения —
    /// используется и в Настройках (постоянная строка версии), и в сайдбаре, чтобы оба места
    /// гарантированно показывали одно и то же.</summary>
    public static string CurrentVersionText => Format(CurrentVersion);

    /// <summary>Версия так, как её видит человек. Четвёртая цифра показывается ТОЛЬКО когда она не
    /// ноль, и это принципиально: с 24.08.2026 она перестала быть локальным номером сборки и несёт
    /// смысл — «мелкая правка» (см. CLAUDE.md про нумерацию). Печатать её всегда значило бы
    /// переписать «1.74.0» в «1.74.0.0» во всех уже вышедших версиях; не печатать никогда — значило
    /// бы, что 1.74.0 и 1.74.0.1 на экране неразличимы, а именно по этой строке человек и говорит,
    /// что у него стоит. Двухкомпонентный тег («v1.2») ToString(3) роняет — отсюда проверка Build.</summary>
    public static string Format(Version version) =>
        version.Revision > 0 ? version.ToString(4)
        : version.Build >= 0 ? version.ToString(3)
        : version.ToString();

    /// <summary>Единая точка проверки обновлений: папка, если указана и доступна, иначе GitHub.
    /// Возвращает источник (для отображения пользователю) и все найденные релизы по убыванию
    /// версии — первый элемент используется как «последняя версия», остальные — для отката.
    ///
    /// Два случая, которые раньше были неотличимы от штатной работы и теперь видны явно:
    /// • папка задана, но недоступна, а GitHub жив — результат приходит с заполненным
    ///   <see cref="UpdateCheckResult.FolderProblem"/> (источник молча подменился запасным);
    /// • недоступны ОБА — бросается <see cref="UpdateSourcesUnavailableException"/> с обеими
    ///   причинами сразу, а не «просто ошибка GitHub».</summary>
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string? folderPath)
    {
        var folderConfigured = !string.IsNullOrWhiteSpace(folderPath);
        var folderProblem = folderConfigured ? DescribeFolderProblem(folderPath!) : null;

        if (folderConfigured && folderProblem is null)
            return new UpdateCheckResult(UpdateSourceKind.Folder, FolderSourceLabel(folderPath!), ListFolderReleases(folderPath!));

        List<UpdateRelease> releases;
        try
        {
            releases = await ListGitHubReleasesAsync();
        }
        catch (Exception ex) when (folderConfigured)
        {
            // Обе половины отвалились: папка задана, но недоступна, и GitHub не отвечает. Раньше сюда
            // прилетала «просто ошибка GitHub» — про недоступную папку в ней не было ни слова, хотя
            // именно она и была настроенным источником. Явная ошибка вместо тихого «обновлений нет»
            // — прямое требование: под будущим ограничением исходящих соединений по IP GitHub
            // отвалится насовсем, и молчание здесь означало бы, что машина годами сидит на старой
            // версии, а никто об этом не знает.
            throw new UpdateSourcesUnavailableException(folderPath!, folderProblem!, ex);
        }
        return new UpdateCheckResult(UpdateSourceKind.GitHub, GitHubSourceLabel, releases, folderProblem);
    }

    private static string FolderSourceLabel(string folderPath) => $"папка обновлений ({folderPath})";

    /// <summary>null — папкой можно пользоваться; иначе человекочитаемая причина, почему нельзя.
    /// Ходит на диск (на отвалившейся сетевой шаре Directory.Exists сам по себе отвечает секундами),
    /// поэтому вызывать её на потоке UI напрямую нельзя — см. ProbeSourcesAsync/ConnectionStatusService.</summary>
    internal static string? DescribeFolderProblem(string folderPath)
    {
        try
        {
            if (Directory.Exists(folderPath)) return null;
            return "папка недоступна (сетевой диск не подключён, путь не существует или нет прав)";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Опрашивает ОБА источника сразу и ничего не бросает — в отличие от
    /// <see cref="CheckForUpdatesAsync"/>, который спрашивает только тот источник, который реально
    /// будет использован. Нужен там, где показывают состояние, а не устанавливают обновление:
    /// кнопка «Проверить» в Настройках и экран «Состояние подключения». Оба похода ограничены
    /// <paramref name="timeout"/> — на неотвечающей шаре/через режущий трафик прокси проверка иначе
    /// висит десятками секунд.</summary>
    public static async Task<UpdateSourcesReport> ProbeSourcesAsync(string? folderPath, TimeSpan timeout)
    {
        var folderTask = ProbeFolderAsync(folderPath, timeout);
        var gitHubTask = ProbeGitHubAsync(timeout);
        await Task.WhenAll(folderTask, gitHubTask);

        var folder = folderTask.Result;
        var gitHub = gitHubTask.Result;

        UpdateSourceKind? effective =
            folder is { Configured: true, Available: true } ? UpdateSourceKind.Folder :
            gitHub.Available ? UpdateSourceKind.GitHub :
            null;

        return new UpdateSourcesReport(folder, gitHub, effective, CurrentVersion);
    }

    private static async Task<UpdateSourceStatus> ProbeFolderAsync(string? folderPath, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return new UpdateSourceStatus(false, false, "не задана", null, 0, "папка обновлений не настроена");

        var path = folderPath.Trim();
        var work = Task.Run(() =>
        {
            var problem = DescribeFolderProblem(path);
            if (problem is not null) return new UpdateSourceStatus(true, false, path, null, 0, problem);
            try
            {
                var releases = ListFolderReleases(path);
                return new UpdateSourceStatus(true, true, path, releases.Count > 0 ? releases[0].Version : null, releases.Count, null);
            }
            catch (Exception ex)
            {
                return new UpdateSourceStatus(true, false, path, null, 0, ex.Message);
            }
        });

        var finished = await Task.WhenAny(work, Task.Delay(timeout));
        if (finished != work)
            return new UpdateSourceStatus(true, false, path, null, 0,
                $"папка не ответила за {timeout.TotalSeconds:0} с (сетевой диск не отвечает)");
        return work.Result;
    }

    private static async Task<UpdateSourceStatus> ProbeGitHubAsync(TimeSpan timeout)
    {
        using var cts = new System.Threading.CancellationTokenSource(timeout);
        try
        {
            var releases = await ListGitHubReleasesAsync(cts.Token);
            return new UpdateSourceStatus(true, true, GitHubSourceLabel, releases.Count > 0 ? releases[0].Version : null, releases.Count, null);
        }
        catch (Exception ex)
        {
            var problem = cts.IsCancellationRequested
                ? $"GitHub не ответил за {timeout.TotalSeconds:0} с"
                : DescribeError(ex);
            return new UpdateSourceStatus(true, false, GitHubSourceLabel, null, 0, problem);
        }
    }

    /// <summary>Человекочитаемый отчёт по обоим источникам — то, что показывает кнопка «Проверить» в
    /// Настройках и что попадает в буфер обмена из «Состояния подключения» (этот текст надо
    /// уметь просто переслать).</summary>
    public static string DescribeSources(UpdateSourcesReport report)
    {
        var lines = new List<string>
        {
            $"Установлена версия: {Format(report.CurrentVersion)}",
        };

        lines.Add(report.Folder.Configured
            ? report.Folder.Available
                ? $"Папка обновлений ({report.Folder.Location}): доступна, {DescribeFound(report.Folder)}."
                : $"Папка обновлений ({report.Folder.Location}): НЕДОСТУПНА — {report.Folder.Problem}."
            : "Папка обновлений: не настроена.");

        lines.Add(report.GitHub.Available
            ? $"GitHub ({GitHubSourceLabel}): доступен, {DescribeFound(report.GitHub)}."
            : $"GitHub: НЕДОСТУПЕН — {report.GitHub.Problem}.");

        lines.Add(report.EffectiveSource switch
        {
            UpdateSourceKind.Folder => "Будет использована папка обновлений.",
            UpdateSourceKind.GitHub => "Будет использован GitHub.",
            _ => "Ни один источник обновлений не доступен — новые версии на эту машину сейчас не придут.",
        });

        return string.Join("\n", lines);
    }

    private static string DescribeFound(UpdateSourceStatus status) =>
        status.LatestVersion is null
            ? "версий не найдено"
            : $"последняя версия {FormatVersion(status.LatestVersion)} (всего версий: {status.ReleaseCount})";

    /// <summary>То же, что <see cref="Format"/> — оставлено отдельным именем, потому что зовётся из
    /// разбора имён файлов релиза, где версия приходит из чужой строки:
    /// на такой бросает исключение — отчёт о состоянии не должен падать из-за формата чужого тега.</summary>
    private static string FormatVersion(Version version) => Format(version);

    private static List<UpdateRelease> ListFolderReleases(string updatePath)
    {
        var releases = new List<UpdateRelease>();
        foreach (var file in Directory.GetFiles(updatePath, "AntarusPoFinder-*.exe"))
        {
            var name = Path.GetFileName(file);
            var m = ReleaseFileRegex.Match(name);
            if (m.Success && Version.TryParse(m.Groups[1].Value, out var v))
                releases.Add(new UpdateRelease(v, name, UpdateSourceKind.Folder, LocalPath: file));
        }
        return releases.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>Читает GitHub Releases репозитория: версия берётся из тега (без ведущей "v"),
    /// файл — ассет с именем AntarusPoFinder-{версия}.exe. Релизы без такого ассета или без
    /// разбираемого тега пропускаются. Публикация нового релиза: <c>gh release create v1.2.0 publish/AntarusPoFinder.App.exe</c>
    /// (переименовав в AntarusPoFinder-{версия}.exe для единообразия с папочным источником).</summary>
    private static async Task<List<UpdateRelease>> ListGitHubReleasesAsync(System.Threading.CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases", ct);
        using var doc = JsonDocument.Parse(json);

        var releases = new List<UpdateRelease>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var tag = item.GetProperty("tag_name").GetString() ?? "";
            var versionStr = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
            if (!Version.TryParse(versionStr, out var version)) continue;
            if (!item.TryGetProperty("assets", out var assets)) continue;

            string? exeName = null;
            var exeUrl = "";
            long exeSize = 0;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                // Именно ReleaseFileRegex, а НЕ «первый .exe в релизе»: к релизу прикладываются и
                // посторонние exe — antarus-sync.exe (служба обмена, см. tools/sync-server). По
                // старому правилу приложение скачало бы девятимегабайтную службу, проверило размер
                // и SHA (они сошлись бы — файл-то целый) и подменило бы ею само себя на всех
                // машинах разом. Имя ассета — единственное, что отличает одно от другого.
                if (!ReleaseFileRegex.IsMatch(name)) continue;
                exeName = name;
                exeUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                exeSize = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                break;
            }
            if (exeName is null) continue;

            // Фикс целостности: ищем ассет "<exe>.sha256" В ТОМ ЖЕ релизе (build.ps1 кладёт его
            // рядом с exe — см. installer/build.ps1). Сравниваем по точному имени, а не "первый
            // .sha256 в релизе", чтобы не перепутать с каким-нибудь другим вложением.
            var shaAssetName = exeName + ".sha256";
            var shaUrl = "";
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!string.Equals(name, shaAssetName, StringComparison.OrdinalIgnoreCase)) continue;
                shaUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                break;
            }

            releases.Add(new UpdateRelease(version, exeName, UpdateSourceKind.GitHub, DownloadUrl: exeUrl, ExpectedSize: exeSize, Sha256Url: shaUrl));
        }
        return releases.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>Тело GitHub-релиза (Markdown/plain-текст из формы «Describe this release») по номеру
    /// версии — источник текста для окна «Что нового» (см. MainWindowViewModel.CheckWhatsNewAsync).
    /// Запрашивает релиз по тегу <c>v{version}</c> (та же схема тегов, что ListGitHubReleasesAsync
    /// уже предполагает при чтении списка релизов). Возвращает null при ЛЮБОЙ ошибке (сети нет,
    /// такого тега/релиза не существует, GitHub недоступен, тело пустое) — это не критично, окно
    /// «Что нового» в этом случае просто не показывается, попытка не повторяется никакими
    /// повторными запросами отсюда. Не поддерживается для папочного источника обновлений — у файлов
    /// в сетевой папке обновлений нет release notes, только сам .exe.</summary>
    public static async Task<string?> GetReleaseNotesAsync(string version)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/tags/v{version}");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("body", out var bodyProp)) return null;
            var body = bodyProp.GetString();
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch
        {
            // Сеть недоступна / релиз с таким тегом не найден (404) / GitHub временно лежит / битый
            // JSON — во всех случаях одинаково: окно «Что нового» просто не покажется в этот раз.
            return null;
        }
    }

    /// <summary>Устанавливает релиз и перезапускает приложение. Для GitHub-источника сначала
    /// скачивает .exe-ассет во временную папку (с проверкой размера и, если доступен .sha256-ассет,
    /// SHA256 — см. DownloadReleaseAsync). Для папочного источника (сетевой диск, доверенный —
    /// администратор сам туда кладёт файлы) размер файлом не проверяется вовсе, а SHA256 сверяется,
    /// только если рядом реально лежит файл &lt;exe&gt;.sha256 — см. VerifyFolderSha256IfPresent.</summary>
    public static async Task InstallAndRestartAsync(UpdateRelease release)
    {
        string localPath;
        if (release.Source == UpdateSourceKind.Folder)
        {
            localPath = release.LocalPath;
            VerifyFolderSha256IfPresent(localPath);
        }
        else
        {
            localPath = await DownloadReleaseAsync(release);
        }
        InstallAndRestart(localPath);
    }

    /// <summary>Downloads the release .exe and, if GitHub reported a size for the asset, verifies
    /// the downloaded file matches it byte-for-byte before handing it off to be installed — a
    /// silently-truncated download (dropped connection, corporate proxy cutting the stream short)
    /// would otherwise only surface as "downloaded fine, then won't launch", which is much harder
    /// for a naladchik to diagnose than a clear error right here.
    ///
    /// Сверка содержимого (в дополнение к проверке размера выше, которая ловит только обрыв/усечение):
    /// если у релиза есть Sha256Url (см. ListGitHubReleasesAsync — ассет "&lt;exe&gt;.sha256" в том же
    /// релизе), скачанный файл сверяется по SHA256 ПЕРЕД тем, как его вообще можно будет
    /// установить/запустить — совпадающий размер ничего не говорит о содержимом. Ловится этим порча
    /// по дороге и MITM без валидного TLS, но НЕ подмена самого релиза: хеш лежит ассетом в том же
    /// релизе, и тот, кто может заменить exe (скомпрометированный GitHub-аккаунт), заменит и его.
    /// От подмены защищает подпись, а не хеш рядом с файлом — тот же открытый хвост, что описан у
    /// VerifyFolderSha256IfPresent. Если .sha256-ассета нет (релиз собран до
    /// этого фикса) — проверка молча пропускается, поведение как раньше (обратная совместимость),
    /// только в Debug-лог уходит пометка об этом. Internal (not private) so AppUpdateServiceTests can
    /// exercise the verification logic directly against a fake HttpMessageHandler, without going
    /// through InstallAndRestartAsync — which shuts the whole process down and is not something a
    /// test can safely call.</summary>
    internal static async Task<string> DownloadReleaseAsync(UpdateRelease release)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), release.FileName);
        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(tempPath);
            await stream.CopyToAsync(file);
        }

        var actualSize = new FileInfo(tempPath).Length;
        if (release.ExpectedSize > 0 && actualSize != release.ExpectedSize)
        {
            File.Delete(tempPath);
            throw new IOException(
                $"Файл скачался повреждённым (ожидалось {release.ExpectedSize} байт, получено {actualSize}) — попробуйте ещё раз. " +
                "Если повторяется — вероятно, корпоративный прокси/фаервол обрывает или подменяет соединение с GitHub.");
        }

        if (!string.IsNullOrEmpty(release.Sha256Url))
        {
            var expectedHex = await FetchExpectedSha256Async(release.Sha256Url);
            var actualHex = ComputeSha256Hex(tempPath);
            if (!string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                throw new IOException(
                    "Проверка подлинности не пройдена: SHA256 скачанного файла не совпадает с ожидаемым " +
                    "(файл в релизе на GitHub мог быть подменён или повреждён нестандартным образом). Установка отменена.");
            }
        }
        else
        {
            Debug.WriteLine($"[AppUpdateService] Релиз {release.FileName} без .sha256-ассета — проверка подлинности пропущена (старый релиз, обратная совместимость).");
        }

        return tempPath;
    }

    /// <summary>Симметричная проверка для папочного источника (сетевой диск обновлений) — см.
    /// InstallAndRestartAsync. Ищет файл рядом с exe по той же схеме именования, что и build.ps1
    /// создаёт в installer/ (&lt;exe&gt;.sha256): если администратор скопировал его на сетевой диск
    /// вместе с exe, порча файла при копировании будет обнаружена.
    ///
    /// <b>От ПОДМЕНЫ эта проверка не защищает, и рассчитывать на неё так нельзя.</b> Хеш лежит в той
    /// же папке, что и exe, и правится теми же правами: кто может заменить exe, тот заменит рядом и
    /// .sha256. Ловится этим только несогласованная пара — обрыв копирования, замена одного файла из
    /// двух, порча на диске. Настоящая защита от подмены — подпись, ключ которой в папке обновлений
    /// не лежит (Authenticode); это открытый хвост аудита, вынесенный на отдельное решение вместе с
    /// вопросом о синхронизируемом app_update_path_shared (см. UpdateFolderResolver).
    ///
    /// Internal — та же причина, что и у DownloadReleaseAsync: тестам нужен доступ напрямую.</summary>
    internal static void VerifyFolderSha256IfPresent(string exePath)
    {
        var shaPath = exePath + ".sha256";
        if (!File.Exists(shaPath))
        {
            Debug.WriteLine($"[AppUpdateService] {Path.GetFileName(exePath)}: файл .sha256 рядом не найден — проверка подлинности пропущена (старый релиз в папке обновлений, обратная совместимость).");
            return;
        }

        var expectedHex = ParseSha256Text(File.ReadAllText(shaPath));
        var actualHex = ComputeSha256Hex(exePath);
        if (!string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                $"Проверка подлинности не пройдена: SHA256 файла «{Path.GetFileName(exePath)}» не совпадает с .sha256 рядом с ним " +
                "(файл в папке обновлений мог быть подменён или повреждён). Установка отменена.");
    }

    /// <summary>Скачивает и парсит содержимое .sha256-ассета — см. ParseSha256Text за форматом.</summary>
    private static async Task<string> FetchExpectedSha256Async(string url) => ParseSha256Text(await Http.GetStringAsync(url));

    /// <summary>Файл .sha256 может быть либо голым hex-хешем (именно так его пишет build.ps1), либо
    /// классическим форматом "sha256sum" — "ХЕШ *имя_файла" — на случай, если кто-то когда-нибудь
    /// сгенерирует его вручную привычной утилитой. Первый пробельно-отделённый токен подходит для
    /// обоих случаев.</summary>
    private static string ParseSha256Text(string text) =>
        text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    private static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Журнал проверок обновлений — рядом с базой приложения (ConfigService.AppData), а не в
    /// %TEMP%: его читают, когда разбирают жалобу «новые версии не приходят», и он должен пережить
    /// чистку временных файлов. Пишется только неудача (успешная проверка молчит), поэтому файл
    /// растёт медленно; при превышении лимита старая половина отбрасывается.
    ///
    /// Существует именно потому, что Debug.WriteLine в релизной сборке не выполняется вовсе, а
    /// уведомление в истории сообщается один раз «на переходе» — по нему не видно, повторяется ли
    /// отказ каждые полчаса или был разовым.</summary>
    public static string UpdateCheckLogPath => Path.Combine(ConfigService.AppData, "update-check.log");

    private const long UpdateCheckLogMaxBytes = 256 * 1024;

    /// <summary>Никогда не бросает: журнал — вспомогательная вещь, и невозможность в него записать не
    /// должна ломать саму проверку обновлений.</summary>
    public static void LogSourceFailure(string message)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppData);
            var path = UpdateCheckLogPath;
            if (File.Exists(path) && new FileInfo(path).Length > UpdateCheckLogMaxBytes)
            {
                var lines = File.ReadAllLines(path);
                File.WriteAllLines(path, lines.Skip(lines.Length / 2));
            }
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* см. doc — журнал не имеет права мешать работе */ }
    }

    /// <summary>Turns a raw exception from CheckForUpdatesAsync/InstallAndRestartAsync into a
    /// message that actually points at a likely cause on a locked-down work PC, instead of a raw
    /// .NET exception string a naladchik/программист can't act on.</summary>
    public static string DescribeError(Exception ex)
    {
        // Проверяется ПЕРВЫМ: у этого исключения внутри лежит сетевая ошибка GitHub, и без этой
        // ветки разбор цепочки ниже вернул бы «не удалось соединиться с GitHub», потеряв главное —
        // что настроенная папка обновлений тоже недоступна и почему.
        if (ex is UpdateSourcesUnavailableException) return ex.Message;

        var chain = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException) chain.Add(e);

        if (chain.Any(e => e is System.Security.Authentication.AuthenticationException ||
                           e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                           e.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase)))
        {
            return $"Не удалось установить защищённое соединение с GitHub ({ex.Message}). " +
                   "Часто это корпоративный прокси/антивирус, подменяющий сертификат сайта — стоит уточнить у IT, " +
                   "не блокируется ли api.github.com/objects.githubusercontent.com.";
        }
        if (chain.Any(e => e is System.Net.Sockets.SocketException) ||
            ex is TaskCanceledException or HttpRequestException)
        {
            return $"Не удалось соединиться с GitHub ({ex.Message}). Проверьте интернет — если он есть, " +
                   "но ошибка повторяется, вероятно доступ к GitHub заблокирован на этом компьютере/в сети.";
        }
        return ex.Message;
    }

    /// <summary>Копирует выбранный релизный .exe поверх текущего и перезапускает приложение.
    /// Работает одинаково для обновления и для отката — единственное отличие в том, версия старше
    /// или новее текущей. Запущенный self-contained single-file .exe не может перезаписать сам
    /// себя напрямую (файл заблокирован, пока процесс жив), поэтому копия ставится рядом
    /// (<c>*.update</c>), а вспомогательный .cmd-скрипт дожидается завершения текущего процесса,
    /// переносит файл на место оригинала и перезапускает его.
    ///
    /// <b>Почему .cmd, а не PowerShell (прежняя реализация).</b> PowerShell запускался через
    /// <c>-File</c>, и его исполнение подчиняется <c>ExecutionPolicy</c>. В корпоративном домене
    /// групповая политика часто ставит <c>Restricted</c>/<c>AllSigned</c>, и <c>powershell -File</c>
    /// тогда молча отказывался выполнять скрипт: приложение к этому моменту уже сделало
    /// <c>Application.Current.Shutdown()</c> (закрылось), а подмена/перезапуск не отрабатывали — отсюда
    /// жалоба «скачалось, закрылось, обратно не открылось, exe остался старым» ровно у части людей (у
    /// кого политика мягче — RemoteSigned — всё работало). <c>cmd.exe</c> ExecutionPolicy не подчиняется
    /// и исполняется в любой заблокированной среде; логику генерации скрипта см. в
    /// <see cref="UpdateRestartScript"/> (там же тестируется экранирование и наличие перезапуска в
    /// ветке ошибки). Файл пишется в кодировке cp866 и первой строкой делает <c>chcp 866</c>, поэтому
    /// кириллица в логе ошибки печатается и читается одной и той же кодовой страницей.</summary>
    /// <summary>Fixed path (not per-PID) so the next app startup can find it regardless of which
    /// process wrote it — see <see cref="TakeLastUpdateError"/>.</summary>
    private static readonly string UpdateErrorLogPath = Path.Combine(Path.GetTempPath(), "antarus_update_error.log");

    private static void InstallAndRestart(string releaseFilePath)
    {
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Не удалось определить путь к текущему исполняемому файлу.");
        var stagedExe = currentExe + ".update";

        File.Copy(releaseFilePath, stagedExe, overwrite: true);

        // The script runs hidden, after this process (and its UI) is already gone — a failure here
        // (e.g. move denied on a read-only network share, or an antivirus/EDR briefly locking the
        // staged .exe) used to be completely invisible: the app just closed and, on next manual
        // launch, was still the old version with no clue why. The generated .cmd writes such failures
        // to a fixed log path so TakeLastUpdateError can surface it on next startup, and restarts the
        // (still old) exe anyway so the app is never left closed.
        var script = UpdateRestartScript.BuildCmd(Environment.ProcessId, stagedExe, currentExe, UpdateErrorLogPath);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"antarus_update_{Environment.ProcessId}.cmd");
        File.WriteAllText(scriptPath, script, TextFileEncoding.Cp866);

        // cmd.exe /c — исполняется всегда, вне зависимости от PowerShell ExecutionPolicy/GPO.
        Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        // Bypass MainWindow's "закрытие сворачивает в трей" setting — this Shutdown() must actually
        // exit (the launched script waits for THIS process to die before moving the staged exe into
        // place), not get cancelled by the window hiding itself instead. See MainWindow.ForceRealExit.
        MainWindow.ForceRealExit = true;
        Application.Current.Shutdown();
    }

    /// <summary>Called once on startup (see MainWindowViewModel) to surface a self-update failure
    /// that happened after the previous process had already closed — see InstallAndRestart. Consumes
    /// the log file so the same failure isn't reported again on the next launch.</summary>
    public static string? TakeLastUpdateError()
    {
        if (!File.Exists(UpdateErrorLogPath)) return null;
        try
        {
            // cp866: скрипт пишет лог именно этой кодовой страницей (chcp 866), и её же читаем —
            // иначе кириллица причины ошибки превратилась бы в «крокозябры». См. UpdateRestartScript.
            var message = File.ReadAllText(UpdateErrorLogPath, TextFileEncoding.Cp866).Trim();
            File.Delete(UpdateErrorLogPath);
            return string.IsNullOrEmpty(message) ? null : message;
        }
        // Reading/deleting this one-shot log file itself failing (locked, permissions) just means
        // this particular launch doesn't surface the previous update failure — not worth a second
        // layer of error reporting on top of the very mechanism that reports errors.
        catch { return null; }
    }
}
