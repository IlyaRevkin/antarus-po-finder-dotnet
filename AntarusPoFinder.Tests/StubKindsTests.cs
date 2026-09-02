using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Три повода положить страницу вместо инструкции или рядом с ней — и главное свойство,
/// которого до сих пор не было: <b>правка макета доезжает до уже лежащих файлов</b>.
///
/// Жалоба звучала так: «меняя макет заглушки, у меня сами заглушки не меняются, хоть я их
/// перезаливаю, хоть удаляю и заливаю». Причина была одна на два места: готовый PDF рисовался ОДИН
/// раз, а дальше и обход диска, и перезаливка на хостинг видели «файл на месте» и не трогали его —
/// перезаливка при этом добросовестно отправляла наверх те же самые старые байты. Лечится отпечатком
/// макета в метке файла (см. InstructionStub.Marker).</summary>
public class StubKindsTests : IDisposable
{
    /// <summary>Рисует не картинку, а текст макета — этого хватает, чтобы отличить одну страницу от
    /// другой и увидеть, перерисовали её или нет.</summary>
    private sealed class FakeWriter : IInstructionStubWriter
    {
        private readonly StubLayoutSet _layouts;
        public List<string> Written { get; } = new();

        public FakeWriter(StubLayoutSet? layouts = null) => _layouts = (layouts ?? StubLayoutSet.Default).Sane();

        public StubLayoutSet Layouts => _layouts;

        public void Write(string path, string text) => Write(path, StubKind.InDevelopment, null);

        public void Write(string path, StubKind kind, string? versionRaw)
        {
            var layout = _layouts.For(kind);
            Written.Add(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                layout.Fill(layout.Title, versionRaw, _layouts.Contacts) + "\n" +
                layout.Fill(layout.Hint, versionRaw, _layouts.Contacts) + "\n" +
                layout.Fill(layout.Contacts, versionRaw, _layouts.Contacts));
        }
    }

    private readonly TempRoot _tempRoot = new();
    private string Root => _tempRoot.Path;
    private const string Version = "2.1.0042.0001.20260422_1348";

    public void Dispose() => _tempRoot.Dispose();

    private string Folder(string name = "Инструкция")
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ── Тот самый баг ────────────────────────────────────────────────────────

    /// <summary>Правка макета перерисовывает уже лежащую заглушку — по ТОМУ ЖЕ пути, чтобы
    /// напечатанные наклейки не пришлось переклеивать.</summary>
    [Fact]
    public void ChangingTheLayout_RedrawsAStubThatIsAlreadyOnDisk()
    {
        var folder = Folder();
        var before = new FakeWriter();

        Assert.Equal(StubAction.Created, InstructionStub.Ensure(folder, Version, before));
        var stub = InstructionStub.PathFor(folder, Version);
        Assert.Contains(InstructionStub.Text, File.ReadAllText(stub));

        // Тем же макетом — файл не трогается вовсе: иначе каждая перестройка диска переписывала бы
        // сотни файлов на сетевом диске.
        Assert.Equal(StubAction.None, InstructionStub.Ensure(folder, Version, new FakeWriter()));

        var after = new FakeWriter(StubLayoutSet.Default.With(StubKind.InDevelopment,
            StubLayout.DefaultFor(StubKind.InDevelopment) with { Title = "Руководство готовится" }));

        Assert.Equal(StubAction.Refreshed, InstructionStub.Ensure(folder, Version, after));
        Assert.Equal(stub, Assert.Single(after.Written));
        Assert.Contains("Руководство готовится", File.ReadAllText(stub));
        // Перерисовали — и на этом успокоились.
        Assert.Equal(StubAction.None, InstructionStub.Ensure(folder, Version, after));
    }

    /// <summary>Заглушка, положенная старой программой (метка без отпечатка), перерисовывается ровно
    /// один раз — а не считается «непонятно чем» и не остаётся навсегда прежней.</summary>
    [Fact]
    public void AStubFromBeforeStampsExisted_IsRedrawnOnce()
    {
        var folder = Folder();
        var stub = InstructionStub.PathFor(folder, Version);
        File.WriteAllText(stub, "старая заглушка\n" + InstructionStub.Marker + "\n");

        Assert.True(InstructionStub.IsStub(stub));
        Assert.Equal("", InstructionStub.StampOf(stub));

        var writer = new FakeWriter();
        Assert.Equal(StubAction.Refreshed, InstructionStub.Ensure(folder, Version, writer));
        Assert.NotEqual("", InstructionStub.StampOf(stub));
        Assert.Equal(StubAction.None, InstructionStub.Ensure(folder, Version, new FakeWriter()));
    }

    /// <summary>Смена одних лишь контактов сервиса тоже перерисовывает страницы: телефон на них
    /// напечатан, и старый телефон — худшее, что может открыться по наклейке.</summary>
    [Fact]
    public void ChangingOnlyTheServiceContacts_RedrawsToo()
    {
        var folder = Folder();
        Assert.Equal(StubAction.Created, InstructionStub.Ensure(folder, Version, new FakeWriter()));

        var moved = new FakeWriter(StubLayoutSet.Default with { ServiceContacts = "Сервис: 8 (800) 000-00-00" });
        Assert.Equal(StubAction.Refreshed, InstructionStub.Ensure(folder, Version, moved));
        Assert.Contains("8 (800) 000-00-00", File.ReadAllText(InstructionStub.PathFor(folder, Version)));
    }

    /// <summary>Правка, не влияющая на картинку, файлов не трогает. Иначе любая синхронизация
    /// конфига переписывала бы весь сетевой диск.</summary>
    [Fact]
    public void AnEditThatDoesNotChangeThePicture_TouchesNothing()
    {
        var folder = Folder();
        InstructionStub.Ensure(folder, Version, new FakeWriter());

        // Пробелы по краям текста и есть та самая незначащая правка — Sane() их срезает.
        var same = new FakeWriter(StubLayoutSet.Default.With(StubKind.InDevelopment,
            StubLayout.DefaultFor(StubKind.InDevelopment) with { Title = "  " + InstructionStub.Text + "  " }));

        Assert.Equal(StubAction.None, InstructionStub.Ensure(folder, Version, same));
        Assert.Empty(same.Written);
    }

    /// <summary>Отдельная точка входа для перезаливки на хостинг: она гоняет наверх байты с диска, и
    /// перед отправкой файл обязан быть перерисован.</summary>
    [Fact]
    public void Refresh_RedrawsAStubByItsPath_AndIgnoresRealDocuments()
    {
        var folder = Folder();
        InstructionStub.Ensure(folder, Version, new FakeWriter());
        var stub = InstructionStub.PathFor(folder, Version);

        var changed = new FakeWriter(StubLayoutSet.Default.With(StubKind.InDevelopment,
            StubLayout.DefaultFor(StubKind.InDevelopment) with { Title = "Готовится" }));
        Assert.Equal(StubAction.Refreshed, InstructionStub.Refresh(stub, changed));
        Assert.Contains("Готовится", File.ReadAllText(stub));

        // Настоящий документ перерисовке не подлежит ни при каких обстоятельствах.
        var real = Path.Combine(Folder("Другая"), $"инструкция_{Version}.pdf");
        File.WriteAllText(real, "настоящий документ");
        Assert.Equal(StubAction.None, InstructionStub.Refresh(real, changed));
        Assert.Equal("настоящий документ", File.ReadAllText(real));
    }

    // ── Три вида ─────────────────────────────────────────────────────────────

    /// <summary>Рядом с настоящей инструкцией НЕ ПОЯВЛЯЕТСЯ ничего. Страница с обращением в сервис
    /// вшивается в сам документ при выкладке (см. ServicePageStitcher), а не кладётся файлом-спутником:
    /// спутник виден только тому, кто смотрит папку на диске, а заказчик открывает по QR один файл.</summary>
    [Fact]
    public void NextToARealInstruction_NoCompanionFileAppears()
    {
        var folder = Folder();
        var real = Path.Combine(folder, $"инструкция_{Version}.pdf");
        File.WriteAllText(real, "настоящий документ");

        var writer = new FakeWriter();
        Assert.Equal(StubAction.None, InstructionStub.Ensure(folder, Version, writer));

        Assert.Empty(writer.Written);
        Assert.Single(Directory.GetFiles(folder));
        Assert.True(File.Exists(real), "настоящий документ не тронут");
        Assert.Null(InstructionStub.ExistingIn(folder));
    }

    /// <summary>Документ убрали — на освободившееся место встаёт обычная заглушка «в разработке».</summary>
    [Fact]
    public void WhenTheInstructionGoes_TheOrdinaryStubComesBack()
    {
        var folder = Folder();
        var real = Path.Combine(folder, $"инструкция_{Version}.pdf");
        File.WriteAllText(real, "настоящий документ");
        InstructionStub.Ensure(folder, Version, new FakeWriter());

        File.Delete(real);
        Assert.Equal(StubAction.Created, InstructionStub.Ensure(folder, Version, new FakeWriter()));

        Assert.NotNull(InstructionStub.ExistingIn(folder));
        Assert.Equal(StubKind.InDevelopment, InstructionStub.KindOf(InstructionStub.ExistingIn(folder)));
    }

    /// <summary>Страница «инструкции не будет» — ОДНА на всех и лежит в корне диска: в её адресе нет
    /// ни типа, ни подтипа, ни контроллера. Ровно это и просили.</summary>
    [Fact]
    public void TheNotPlannedPage_IsASingleFileAtTheDiskRoot()
    {
        var writer = new FakeWriter();

        Assert.Equal(StubAction.Created, InstructionStub.EnsureShared(Root, writer));
        var page = InstructionStub.SharedNotPlannedPath(Root);

        Assert.Equal(Path.Combine(Root, InstructionStub.SharedNotPlannedFileName), page);
        Assert.True(File.Exists(page));
        Assert.Equal(StubKind.NotPlanned, InstructionStub.KindOf(page));
        // Ключ на хостинге считается от пути относительно корня — у этой страницы он в один сегмент,
        // без типа и подтипа.
        Assert.Equal(InstructionStub.SharedNotPlannedFileName, LabelLinkBuilder.RelativeTo(Root, page));

        // Второй раз не пишется, а после правки макета перерисовывается — тот же файл, тот же адрес.
        Assert.Equal(StubAction.None, InstructionStub.EnsureShared(Root, new FakeWriter()));
        var changed = new FakeWriter(StubLayoutSet.Default.With(StubKind.NotPlanned,
            StubLayout.DefaultFor(StubKind.NotPlanned) with { Title = "Руководства нет" }));
        Assert.Equal(StubAction.Refreshed, InstructionStub.EnsureShared(Root, changed));
        Assert.Single(Directory.GetFiles(Root));
    }

    /// <summary>Во всех трёх видах есть телефон сервиса — ради этого всё и затевалось.</summary>
    [Theory]
    [InlineData(StubKind.InDevelopment)]
    [InlineData(StubKind.NotPlanned)]
    [InlineData(StubKind.ServiceNote)]
    public void EveryKind_ShowsTheServicePhone(StubKind kind)
    {
        var layout = StubLayout.DefaultFor(kind);
        var text = layout.Fill(layout.Contacts, Version, StubLayoutSet.Default.Contacts);

        Assert.Contains(ServiceContacts.Phone, text);
        Assert.Contains(ServiceContacts.Email, text);
    }

    /// <summary>Тексты трёх видов не совпадают: обещание «скоро допишем» на шкафу, где инструкции не
    /// будет никогда, — то же враньё, от которого страница и должна избавлять.</summary>
    [Fact]
    public void TheThreeKinds_DoNotPromiseTheSameThing()
    {
        var titles = StubKinds.All.Select(k => StubLayout.DefaultFor(k).Title).ToList();
        Assert.Equal(titles.Count, titles.Distinct().Count());

        Assert.DoesNotContain("разработке", StubLayout.DefaultFor(StubKind.NotPlanned).Title,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("разработке", StubLayout.DefaultFor(StubKind.NotPlanned).Hint,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── Набор макетов ────────────────────────────────────────────────────────

    [Fact]
    public void TheLayoutSet_KeepsTheThreeApart_AndRoundTripsThroughJson()
    {
        var set = StubLayoutSet.Default
            .With(StubKind.NotPlanned, StubLayout.DefaultFor(StubKind.NotPlanned) with { Title = "Нет и не будет" })
            with
        { ServiceContacts = "Сервис: 8 (800) 111-22-33" };

        var back = StubLayoutSet.Parse(set.ToJson());

        Assert.Equal("Нет и не будет", back.For(StubKind.NotPlanned).Title);
        Assert.Equal(StubLayout.DefaultFor(StubKind.InDevelopment).Title, back.For(StubKind.InDevelopment).Title);
        Assert.Equal("Сервис: 8 (800) 111-22-33", back.Contacts);
        // Вид приклеен к макету — иначе перерисовка по лежащему файлу считала бы отпечаток не того.
        Assert.Equal(StubKind.NotPlanned, back.For(StubKind.NotPlanned).Kind);
    }

    /// <summary>Подогнанный человеком вид «в разработке» из прежней одиночной настройки не теряется
    /// при обновлении: общий конфиг ездит между машинами, и обнулить чужую подгонку нельзя.</summary>
    [Fact]
    public void TheLayoutSet_AdoptsTheOldSingleLayout_WhenThereIsNoSetYet()
    {
        var legacy = (StubLayout.Default with { Title = "Инструкция готовится", ShowFrame = true }).ToJson();

        var set = StubLayoutSet.Parse(null, legacy);

        Assert.Equal("Инструкция готовится", set.InDevelopment.Title);
        Assert.True(set.InDevelopment.ShowFrame);
        // Две остальные страницы при этом — свои собственные, а не копии первой.
        Assert.Equal(StubLayout.DefaultFor(StubKind.NotPlanned).Title, set.NotPlanned.Title);
    }

    [Fact]
    public void TheLayoutSet_BrokenJson_FallsBackToDefaultInsteadOfThrowing()
    {
        Assert.Equal(StubLayoutSet.Default.InDevelopment.Title, StubLayoutSet.Parse("{сломано").InDevelopment.Title);
        Assert.Equal(StubLayoutSet.Default.NotPlanned.Title, StubLayoutSet.Parse(null).NotPlanned.Title);
    }

    /// <summary>Отпечаток — это «одинаковая ли получится картинка», а не «одинаковые ли поля».</summary>
    [Fact]
    public void TheStamp_ChangesExactlyWhenThePictureDoes()
    {
        var set = StubLayoutSet.Default;

        Assert.Equal(set.Stamp(StubKind.InDevelopment), StubLayoutSet.Default.Stamp(StubKind.InDevelopment));
        Assert.NotEqual(set.Stamp(StubKind.InDevelopment), set.Stamp(StubKind.NotPlanned));

        var bigger = set.With(StubKind.InDevelopment, set.InDevelopment with { TitleSize = 0.09 });
        Assert.NotEqual(set.Stamp(StubKind.InDevelopment), bigger.Stamp(StubKind.InDevelopment));

        var framed = set.With(StubKind.InDevelopment, set.InDevelopment with { ShowFrame = true });
        Assert.NotEqual(set.Stamp(StubKind.InDevelopment), framed.Stamp(StubKind.InDevelopment));
    }
}
