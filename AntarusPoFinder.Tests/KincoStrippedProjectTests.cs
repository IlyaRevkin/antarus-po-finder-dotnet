using System;
using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Проект KINCO, уехавший на диск без своих файлов, должен распознаваться как обрубок.
///
/// Жалоба Ильи 02.10.2026: «у коллег открывается kinco dtools и пишет, что по указанному пути файл
/// прошивки не найден, но открываешь этот путь — и там всё нормально».
///
/// Ошибку выдаёт сам DTools, а не программа: она молча открывает одинокий .dpj, среда стартует и не
/// находит остального проекта. Проверка «проект лежит без окружения» в программе БЫЛА, но работала
/// только для .fsprj — у KINCO точка входа .dpj, и под неё она не срабатывала ни разу.
///
/// Что проект KINCO устроен папкой, теперь известно точно, а не предположительно: в настоящем
/// проекте рядом с «УПД_MK070_v8-26.dpj» лежат .pkgx, .bak, PLCGEDefaultProperties.xml и подпапки
/// HMI0, image, sound, vg, tar, temp. Открывать один файл из этого набора бессмысленно.</summary>
public class KincoStrippedProjectTests : IDisposable
{
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string Make(string folderName, params string[] entries)
    {
        var dir = Path.Combine(_root.Path, folderName);
        Directory.CreateDirectory(dir);
        foreach (var e in entries)
        {
            if (e.EndsWith("\\", StringComparison.Ordinal)) Directory.CreateDirectory(Path.Combine(dir, e.TrimEnd('\\')));
            else File.WriteAllText(Path.Combine(dir, e), "x");
        }
        return dir;
    }

    /// <summary>Главное: один .dpj и больше ничего — это обрубок, и человеку надо сказать об этом
    /// ДО того, как DTools выдаст своё невразумительное «файл не найден».</summary>
    [Fact]
    public void Одинокий_dpj_признаётся_обрубком()
    {
        var dir = Make("УПД_MK070_v8-26", "УПД_MK070_v8-26.dpj");
        var dpj = Path.Combine(dir, "УПД_MK070_v8-26.dpj");

        Assert.True(HmiProjectFormat.IsStrippedCopy(dpj, "2.1.0042.0001"));
    }

    /// <summary>А целый проект обрубком не считается — иначе предупреждение сыпалось бы на исправные
    /// и ему перестали бы верить. Состав взят с настоящего проекта.</summary>
    [Fact]
    public void Целый_проект_kinco_не_считается_обрубком()
    {
        var dir = Make("УПД_MK070_v8-26",
            "УПД_MK070_v8-26.dpj", "УПД_MK070_v8-26.pkgx", "УПД_MK070_v8-26.bak",
            "PLCGEDefaultProperties.xml", "HMI0\\", "image\\", "sound\\", "vg\\");
        var dpj = Path.Combine(dir, "УПД_MK070_v8-26.dpj");

        Assert.False(HmiProjectFormat.IsStrippedCopy(dpj, "2.1.0042.0001"));
    }

    /// <summary>Проект KINCO, лежащий в СВОЕЙ папке с окружением, забирается папкой целиком — это
    /// распознавание по строению, оно было и раньше (см. ProjectTree).</summary>
    [Fact]
    public void Целый_проект_kinco_копируется_папкой()
    {
        var dir = Make("УПД_MK070_v8-26",
            "УПД_MK070_v8-26.dpj", "УПД_MK070_v8-26.pkgx", "HMI0\\", "vg\\");
        var dpj = Path.Combine(dir, "УПД_MK070_v8-26.dpj");

        Assert.Equal(dir, HmiProjectFormat.ProjectFolderOf(dpj));
    }

    /// <summary>А наша одиночная копия в общей папке HMI обрубком НЕ объявляется: так было решено
    /// раньше, и переворачивать это решение без доказательств нельзя. Здесь проверяется именно то,
    /// что узкая правка не задела прежнее поведение.</summary>
    [Fact]
    public void Одиночная_копия_в_общей_папке_hmi_не_трогается()
    {
        var dir = Make("HMI", "2.1.041_hmi.dpj", "2.1.040_hmi.dpj");
        var f = Path.Combine(dir, "2.1.041_hmi.dpj");

        Assert.False(HmiProjectFormat.LooksStrippedOfCompanions(f));
    }

    /// <summary>Прежнее поведение для Segnetics не тронуто.</summary>
    [Fact]
    public void Одинокий_fsprj_по_прежнему_обрубок()
    {
        var dir = Make("панель", "панель.fsprj");
        var f = Path.Combine(dir, "панель.fsprj");

        Assert.True(HmiProjectFormat.IsStrippedCopy(f, "2.1.0042.0001"));
    }
}
