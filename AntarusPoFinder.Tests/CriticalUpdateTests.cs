using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using AntarusPoFinder.App;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>КРИТИЧЕСКОЕ ОБНОВЛЕНИЕ СТАВИТСЯ САМО.
///
/// Просьба Ильи 16.10.2026: «надо завести функционал немедленных принудительных обновлений для
/// критических фиксов».
///
/// Почему это не прихоть. Накануне выяснилось, что заливка кирпичит контроллеры: то есть пока у
/// людей стоит старая версия, программа портит оборудование. Автообновление по умолчанию включено,
/// но его выключают — и ждать, пока каждый сам нажмёт «установить», в таком случае нельзя: цена
/// промедления — снятый и перешитый вручную шкаф.
///
/// Признак живёт в САМОМ выпуске, а не в настройке на машине: решение «это критично» принимается
/// один раз при выпуске и действует на всех.</summary>
public class CriticalUpdateTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _json;
        public FakeHandler(string json) => _json = json;
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json),
            });
    }

    private static UpdateRelease Release(bool critical) =>
        new(new Version(1, 2, 3), "AntarusPoFinder-1.2.3.exe", UpdateSourceKind.GitHub) { Critical = critical };

    /// <summary>Главное правило: критическое ставится, даже когда автообновление ВЫКЛЮЧЕНО. Ради
    /// этого всё и заводилось.</summary>
    [Fact]
    public void Критическое_ставится_даже_при_выключенном_автообновлении()
    {
        Assert.True(AppUpdateService.ShouldInstallAutomatically(Release(critical: true), autoUpdateEnabled: false));
    }

    /// <summary>Обычное — строго по настройке: иначе «принудительно» означало бы «всегда», и
    /// выключатель автообновления перестал бы что-либо значить.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Обычное_подчиняется_настройке(bool autoUpdate)
    {
        Assert.Equal(autoUpdate, AppUpdateService.ShouldInstallAutomatically(Release(critical: false), autoUpdate));
    }

    /// <summary>Метка читается из заметок релиза — там, где её видит и человек. Отдельного поля для
    /// этого у GitHub нет, а заводить параллельный способ пометки (файл-флажок в релизе) значило бы
    /// иметь две правды о том, критичен выпуск или нет.</summary>
    [Fact]
    public async System.Threading.Tasks.Task Метка_в_заметках_релиза_делает_выпуск_критическим()
    {
        const string json = """
            [
              {
                "tag_name": "v1.2.3",
                "body": "Починка заливки SMH5. [критическое]",
                "assets": [
                  { "name": "AntarusPoFinder-1.2.3.exe", "browser_download_url": "https://example.invalid/a.exe", "size": 1 }
                ]
              }
            ]
            """;
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHandler(json)));

            var result = await AppUpdateService.CheckForUpdatesAsync(null);

            Assert.True(result.Releases.Single().Critical);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Без_метки_выпуск_обычный()
    {
        const string json = """
            [
              {
                "tag_name": "v1.2.3",
                "body": "Мелкие правки интерфейса.",
                "assets": [
                  { "name": "AntarusPoFinder-1.2.3.exe", "browser_download_url": "https://example.invalid/a.exe", "size": 1 }
                ]
              }
            ]
            """;
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHandler(json)));

            var result = await AppUpdateService.CheckForUpdatesAsync(null);

            Assert.False(result.Releases.Single().Critical);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    /// <summary>В папке обновлений метка — файл рядом с exe. Папку наполняет администратор руками, и
    /// держать признак в ИМЕНИ самого exe нельзя: имя разбирается регуляркой и участвует в сверке с
    /// версией.</summary>
    [Fact]
    public async System.Threading.Tasks.Task В_папке_обновлений_меткой_служит_файл_рядом()
    {
        using var root = new TempRoot();
        var exe = Path.Combine(root.Path, "AntarusPoFinder-2.0.0.exe");
        File.WriteAllText(exe, "release");
        File.WriteAllText(exe + ".critical", "");

        var result = await AppUpdateService.CheckForUpdatesAsync(root.Path);

        Assert.True(result.Releases.Single().Critical);
    }

    [Fact]
    public async System.Threading.Tasks.Task Без_файла_метки_релиз_из_папки_обычный()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "AntarusPoFinder-2.0.0.exe"), "release");

        var result = await AppUpdateService.CheckForUpdatesAsync(root.Path);

        Assert.False(result.Releases.Single().Critical);
    }
}
