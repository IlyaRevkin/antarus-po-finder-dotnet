using System;
using System.IO;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Пасхалка на номере версии: счётчик «двенадцать быстрых кликов подряд» и хранение
/// фотографии на общем диске машинно-независимым путём. WPF сюда не тащим — вся логика вынесена в
/// EasterEggClickCounter/EasterEggPhoto.</summary>
public class EasterEggTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(1500);

    // ── Счётчик кликов ────────────────────────────────────────────────────────

    [Fact]
    public void TwelveQuickClicks_NoCtrl_Open()
    {
        var counter = new EasterEggClickCounter(window: Window);
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        // Первые одиннадцать — ничего; каждый в пределах окна от предыдущего.
        for (var i = 0; i < 11; i++)
        {
            Assert.Equal(EasterEggAction.None, counter.Click(now, ctrlDown: false));
            now = now.AddMilliseconds(200);
        }

        // Двенадцатый замыкает серию — без Ctrl это «открыть».
        Assert.Equal(EasterEggAction.Open, counter.Click(now, ctrlDown: false));
    }

    [Fact]
    public void TwelveQuickClicks_WithCtrl_Set()
    {
        var counter = new EasterEggClickCounter(window: Window);
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 11; i++)
        {
            counter.Click(now, ctrlDown: true);
            now = now.AddMilliseconds(200);
        }

        // Состояние Ctrl берётся у замыкающего клика — с Ctrl это «задать».
        Assert.Equal(EasterEggAction.Set, counter.Click(now, ctrlDown: true));
    }

    [Fact]
    public void CtrlStateRead_AtTriggeringClick_Only()
    {
        var counter = new EasterEggClickCounter(window: Window);
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        // Первые одиннадцать без Ctrl, двенадцатый — с Ctrl: решает именно двенадцатый.
        for (var i = 0; i < 11; i++)
        {
            counter.Click(now, ctrlDown: false);
            now = now.AddMilliseconds(100);
        }
        Assert.Equal(EasterEggAction.Set, counter.Click(now, ctrlDown: true));
    }

    [Fact]
    public void PauseLongerThanWindow_ResetsStreak()
    {
        var counter = new EasterEggClickCounter(window: Window);
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        // Шесть кликов, затем пауза длиннее окна — серия обнуляется.
        for (var i = 0; i < 6; i++)
        {
            counter.Click(now, ctrlDown: false);
            now = now.AddMilliseconds(200);
        }
        now = now.AddMilliseconds(2000); // > 1.5 c

        // Теперь нужно снова набрать двенадцать; первые одиннадцать после паузы — ничего.
        for (var i = 0; i < 11; i++)
        {
            Assert.Equal(EasterEggAction.None, counter.Click(now, ctrlDown: false));
            now = now.AddMilliseconds(200);
        }
        Assert.Equal(EasterEggAction.Open, counter.Click(now, ctrlDown: false));
    }

    [Fact]
    public void ExactlyAtWindowBoundary_StillCounts()
    {
        var counter = new EasterEggClickCounter(window: Window);
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        // Ровно окно (не больше) между кликами — серия не рвётся.
        for (var i = 0; i < 11; i++)
        {
            Assert.Equal(EasterEggAction.None, counter.Click(now, ctrlDown: false));
            now = now.Add(Window);
        }
        Assert.Equal(EasterEggAction.Open, counter.Click(now, ctrlDown: false));
    }

    [Fact]
    public void FewerThanTwelve_NeverFires()
    {
        var counter = new EasterEggClickCounter(window: Window);
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 11; i++)
        {
            Assert.Equal(EasterEggAction.None, counter.Click(now, ctrlDown: false));
            now = now.AddMilliseconds(100);
        }
        Assert.Equal(11, counter.Count);
    }

    [Fact]
    public void FiresAgain_AfterReset()
    {
        var counter = new EasterEggClickCounter(window: Window);
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 11; i++)
            {
                Assert.Equal(EasterEggAction.None, counter.Click(now, ctrlDown: false));
                now = now.AddMilliseconds(100);
            }
            Assert.Equal(EasterEggAction.Open, counter.Click(now, ctrlDown: false));
            now = now.AddMilliseconds(100);
            Assert.Equal(0, counter.Count); // сброс после срабатывания
        }
    }

    // ── Хранение фотографии на общем диске ────────────────────────────────────

    [Fact]
    public void Import_CopiesToSharedFolder_AndReturnsPortableRelativePath()
    {
        var disk = Path.Combine(Path.GetTempPath(), "egg_disk_" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(Path.GetTempPath(), "egg_src_" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            Directory.CreateDirectory(disk);
            File.WriteAllBytes(src, new byte[] { 1, 2, 3 });

            var portable = EasterEggPhoto.Import(disk, src);

            // Значение — только относительный хвост от корня диска, без буквы диска.
            Assert.Equal(Path.Combine(EasterEggPhoto.Subfolder, Path.GetFileName(src)), portable);
            Assert.False(Path.IsPathRooted(portable));

            // Файл действительно лёг в общую папку.
            var dest = Path.Combine(disk, portable!);
            Assert.True(File.Exists(dest));
        }
        finally
        {
            try { Directory.Delete(disk, true); } catch { }
            try { File.Delete(src); } catch { }
        }
    }

    [Fact]
    public void Resolve_ExpandsRelativeAgainstEachMachinesDiskRoot()
    {
        // Значение задано на машине с диском Z:, открываем на машине с диском Y: — хвост тот же,
        // абсолютный путь сходится под своим корнем.
        var relative = Path.Combine(EasterEggPhoto.Subfolder, "фото.jpg");

        var onZ = EasterEggPhoto.Resolve(@"Z:\", relative);
        var onY = EasterEggPhoto.Resolve(@"Y:\", relative);

        Assert.Equal(Path.Combine(@"Z:\", relative), onZ);
        Assert.Equal(Path.Combine(@"Y:\", relative), onY);
    }

    [Fact]
    public void Resolve_EmptyOrNoDisk_ReturnsNull()
    {
        Assert.Null(EasterEggPhoto.Resolve(@"Z:\", ""));      // фотографии ещё нет
        Assert.Null(EasterEggPhoto.Resolve(null, "фото.jpg")); // диск не настроен
    }

    [Fact]
    public void Import_NoDisk_ReturnsNull_SavesNothing()
    {
        Assert.Null(EasterEggPhoto.Import(null, "c:\\где-то\\ф.png"));
        Assert.Null(EasterEggPhoto.Import("", "c:\\где-то\\ф.png"));
    }
}
