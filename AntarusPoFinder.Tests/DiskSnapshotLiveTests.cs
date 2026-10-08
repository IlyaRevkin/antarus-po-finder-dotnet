using System;
using System.IO;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Слепок диска ЦЕЛИКОМ: обход папки, сжатие, выкладка в настоящий бакет, проверка, что
/// объект там появился, и уборка за собой.
///
/// Почему этого не делает обычный тест. Всё, что можно было проверить без сети, уже проверено
/// (DiskSnapshotTests, DiskSnapshotStorageTests); не проверенным остаётся ровно стык с хранилищем —
/// подпись запроса, имя объекта, тип содержимого. А стык и есть то место, где такие вещи ломаются.
///
/// Почему он выключен по умолчанию. Тест ходит в платный бакет и требует ключей, которых в
/// репозитории нет и не будет. Включается переменными окружения:
///
///     ANTARUS_S3_TEST_ENDPOINT, ANTARUS_S3_TEST_BUCKET, ANTARUS_S3_TEST_REGION,
///     ANTARUS_S3_TEST_KEY, ANTARUS_S3_TEST_SECRET
///
/// Без них — тихо пропускается, и это правильно: на машине без ключей он не «падает», он
/// неприменим. Пишет в собственную папку машины «selftest-<случайное>» и удаляет её за собой, так
/// что рабочим слепкам помешать не может.</summary>
public class DiskSnapshotLiveTests
{
    private static S3Settings? FromEnvironment()
    {
        string? E(string name) => Environment.GetEnvironmentVariable(name);
        var key = E("ANTARUS_S3_TEST_KEY");
        var secret = E("ANTARUS_S3_TEST_SECRET");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret)) return null;

        return new S3Settings(
            E("ANTARUS_S3_TEST_ENDPOINT") ?? "https://s3.twcstorage.ru",
            E("ANTARUS_S3_TEST_BUCKET") ?? "amperus",
            E("ANTARUS_S3_TEST_REGION") ?? "ru-1",
            Prefix: "", key, secret, WebUrl: "", Enabled: true);
    }

    [Fact]
    public async Task Слепок_доезжает_до_хранилища_и_убирается_за_собой()
    {
        var settings = FromEnvironment();
        if (settings is null) return; // ключей нет — тест неприменим, см. описание класса

        var machine = "selftest-" + Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(Path.GetTempPath(), "antarus-snapshot-" + Guid.NewGuid().ToString("N")[..8]);
        var versionDir = Path.Combine(root, "ПО", "НГР", "2.0", "SMH5", "1.1.4.32", "Прошивка");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "1.1.4.32.psl"), "не прошивка, а проверка");

        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var client = new S3Client();
        var service = new DiskSnapshotService(settings, client);

        try
        {
            var outcome = await service.RunAsync(db, root, machine);

            Assert.True(outcome.Ok, outcome.Error);
            Assert.True(outcome.Files > 0, "в слепок должен был попасть хотя бы один файл");
            Assert.False(outcome.Truncated);

            // Объект реально лежит в бакете — а не «запрос вроде бы ушёл».
            var head = await client.HeadAsync(settings, DiskSnapshotStorage.LatestKey(machine));
            Assert.True(head.Ok, head.Error);
            Assert.True(head.Exists, "последний слепок не найден в хранилище");
            Assert.True(head.Length is > 0);
        }
        finally
        {
            var page = await client.ListAsync(settings, DiskSnapshotStorage.MachineFolder(machine), grouped: false);
            if (page.Ok)
                foreach (var o in page.Objects)
                    await client.DeleteAsync(settings, o.Key);

            try { Directory.Delete(root, recursive: true); } catch { /* временная папка */ }
        }
    }
}
