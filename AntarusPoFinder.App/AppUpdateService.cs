using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace AntarusPoFinder.App;

public enum UpdateSourceKind { Folder, GitHub }

/// <summary>Sha256Url (только для GitHub-источника) — прямая ссылка на ассет "&lt;exe&gt;.sha256"
/// того же релиза, если он есть (см. build.ps1 — генерирует его рядом с exe при сборке). Пусто,
/// если релиз собран до появления этого фикса и такого ассета в нём нет — тогда проверка
/// целостности пропускается (обратная совместимость, см. DownloadReleaseAsync).</summary>
public record UpdateRelease(Version Version, string FileName, UpdateSourceKind Source, string LocalPath = "", string DownloadUrl = "", long ExpectedSize = 0, string Sha256Url = "");

public record UpdateCheckResult(UpdateSourceKind Source, string SourceLabel, List<UpdateRelease> Releases);

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

    /// <summary>Единая точка проверки обновлений: папка, если указана и доступна, иначе GitHub.
    /// Возвращает источник (для отображения пользователю) и все найденные релизы по убыванию
    /// версии — первый элемент используется как «последняя версия», остальные — для отката.</summary>
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string? folderPath)
    {
        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            return new UpdateCheckResult(UpdateSourceKind.Folder, $"папка обновлений ({folderPath})", ListFolderReleases(folderPath));

        var releases = await ListGitHubReleasesAsync();
        return new UpdateCheckResult(UpdateSourceKind.GitHub, GitHubSourceLabel, releases);
    }

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
    /// файл — первый .exe-ассет релиза. Релизы без .exe-ассета или без разбираемого тега
    /// пропускаются. Публикация нового релиза: <c>gh release create v1.2.0 publish/AntarusPoFinder.App.exe</c>
    /// (переименовав в AntarusPoFinder-{версия}.exe для единообразия с папочным источником).</summary>
    private static async Task<List<UpdateRelease>> ListGitHubReleasesAsync()
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases");
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
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
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
    /// Фикс подлинности (в дополнение к проверке размера выше, которая ловит только обрыв/усечение):
    /// если у релиза есть Sha256Url (см. ListGitHubReleasesAsync — ассет "&lt;exe&gt;.sha256" в том же
    /// релизе), скачанный файл сверяется по SHA256 ПЕРЕД тем, как его вообще можно будет
    /// установить/запустить — совпадающий размер ничего не говорит о содержимом, а размер+хеш вместе
    /// делают незаметную подмену файла (скомпрометированный GitHub-аккаунт, MITM без валидного TLS
    /// и т.п.) практически неосуществимой без обнаружения. Если .sha256-ассета нет (релиз собран до
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
    /// создаёт в installer/ (&lt;exe&gt;.sha256) — если администратор скопировал его на сетевой диск
    /// вместе с exe, подмена файла на самом диске (или порча при копировании) будет обнаружена.
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

    /// <summary>Turns a raw exception from CheckForUpdatesAsync/InstallAndRestartAsync into a
    /// message that actually points at a likely cause on a locked-down work PC, instead of a raw
    /// .NET exception string a naladchik/программист can't act on.</summary>
    public static string DescribeError(Exception ex)
    {
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
    /// (<c>*.update</c>), а вспомогательный PowerShell-скрипт дожидается завершения текущего
    /// процесса, переносит файл на место оригинала и перезапускает его. Скрипт пишется во
    /// временный .ps1-файл (UTF-8 с BOM, поэтому PowerShell читает кириллицу верно независимо от
    /// текущей кодовой страницы консоли) и запускается через <c>-File</c> — раньше здесь был
    /// <c>-EncodedCommand</c> (Base64), который решал ту же задачу с кодировкой, но на реальном
    /// рабочем ПК периодически блокировался антивирусом/EDR: закодированная inline PowerShell-
    /// команда — известная сигнатура вредоносных техник, и её блокировка выглядела как «скачалось,
    /// но не открывается» — сам файл на месте, просто процесс его переноса/перезапуска тихо
    /// убивался защитой до того, как успевал сработать.</summary>
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
        // (e.g. Move-Item denied on a read-only network share, or an antivirus/EDR briefly locking the
        // staged .exe) used to be completely invisible: the app just closed and, on next manual
        // launch, was still the old version with no clue why. Wrapping the risky part in try/catch and
        // writing failures to a fixed log path lets TakeLastUpdateError surface it on next startup.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $targetPid = {{Environment.ProcessId}}
            while (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
            try {
                Move-Item -LiteralPath '{{EscapeSingleQuoted(stagedExe)}}' -Destination '{{EscapeSingleQuoted(currentExe)}}' -Force
                Start-Process -FilePath '{{EscapeSingleQuoted(currentExe)}}'
            } catch {
                $_.Exception.Message | Out-File -LiteralPath '{{EscapeSingleQuoted(UpdateErrorLogPath)}}' -Encoding utf8
                Start-Process -FilePath '{{EscapeSingleQuoted(currentExe)}}'
            }
            Remove-Item -LiteralPath $PSCommandPath -Force
            """;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"antarus_update_{Environment.ProcessId}.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -File \"{scriptPath}\"",
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
            var message = File.ReadAllText(UpdateErrorLogPath).Trim();
            File.Delete(UpdateErrorLogPath);
            return string.IsNullOrEmpty(message) ? null : message;
        }
        // Reading/deleting this one-shot log file itself failing (locked, permissions) just means
        // this particular launch doesn't surface the previous update failure — not worth a second
        // layer of error reporting on top of the very mechanism that reports errors.
        catch { return null; }
    }

    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");
}
