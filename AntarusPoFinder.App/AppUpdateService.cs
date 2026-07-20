using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace AntarusPoFinder.App;

public enum UpdateSourceKind { Folder, GitHub }

public record UpdateRelease(Version Version, string FileName, UpdateSourceKind Source, string LocalPath = "", string DownloadUrl = "", long ExpectedSize = 0);

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

    private static readonly HttpClient Http = CreateHttpClient();

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

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                var size = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                releases.Add(new UpdateRelease(version, name, UpdateSourceKind.GitHub, DownloadUrl: url, ExpectedSize: size));
                break;
            }
        }
        return releases.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>Устанавливает релиз и перезапускает приложение. Для GitHub-источника сначала
    /// скачивает .exe-ассет во временную папку.</summary>
    public static async Task InstallAndRestartAsync(UpdateRelease release)
    {
        var localPath = release.Source == UpdateSourceKind.Folder
            ? release.LocalPath
            : await DownloadReleaseAsync(release);
        InstallAndRestart(localPath);
    }

    /// <summary>Downloads the release .exe and, if GitHub reported a size for the asset, verifies
    /// the downloaded file matches it byte-for-byte before handing it off to be installed — a
    /// silently-truncated download (dropped connection, corporate proxy cutting the stream short)
    /// would otherwise only surface as "downloaded fine, then won't launch", which is much harder
    /// for a naladchik to diagnose than a clear error right here.</summary>
    private static async Task<string> DownloadReleaseAsync(UpdateRelease release)
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
        return tempPath;
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
    private static void InstallAndRestart(string releaseFilePath)
    {
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Не удалось определить путь к текущему исполняемому файлу.");
        var stagedExe = currentExe + ".update";

        File.Copy(releaseFilePath, stagedExe, overwrite: true);

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $targetPid = {{Environment.ProcessId}}
            while (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
            Move-Item -LiteralPath '{{EscapeSingleQuoted(stagedExe)}}' -Destination '{{EscapeSingleQuoted(currentExe)}}' -Force
            Start-Process -FilePath '{{EscapeSingleQuoted(currentExe)}}'
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

    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");
}
