using System.IO;
using AntarusPoFinder.App.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Подхват снимков, положенных в папку осмотра с телефона по SMB.
///
/// Проверяется ровно та часть, из-за которой всё и затевалось: файл, который ещё пишут, брать
/// рано. Настоящую сетевую шару в тесте не поднять, но писатель, держащий файл открытым, ведёт
/// себя для нас так же, как копирование по сети, — это и воспроизводится.</summary>
public class IncomingFileProbeTests
{
    [Fact]
    public void Файл_который_пишут_не_считается_готовым()
    {
        var dir = Directory.CreateTempSubdirectory("antarus-incoming");
        try
        {
            var path = Path.Combine(dir.FullName, "photo.jpg");
            using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                writer.Write(new byte[1024]);
                writer.Flush();

                // Писатель ещё держит файл — именно это и происходит, пока снимок едет по сети.
                Assert.Null(IncomingFileProbe.TryMeasureIfFree(path));
            }

            // Писатель отпустил — файл меряется, и длина настоящая.
            Assert.Equal(1024, IncomingFileProbe.TryMeasureIfFree(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Пропавший_файл_не_ошибка_а_просто_неизвестно()
    {
        var dir = Directory.CreateTempSubdirectory("antarus-incoming");
        try
        {
            // Снимок могли удалить ровно между событием наблюдателя и замером — это штатный
            // случай «пока не трогать», а не повод уронить тик таймера исключением.
            Assert.Null(IncomingFileProbe.TryMeasureIfFree(Path.Combine(dir.FullName, "нет-такого.jpg")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
