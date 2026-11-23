using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>«ЧТО ЗА ПАРАМЕТРЫ У ЭТОГО ШКАФА» — один ответ на оба устройства.
///
/// Тикет наладчика от 18.11.2026: «Непонятно как добавлять новые параметры для ПЧ к текущим
/// прошивкам, в редакторе нет подходящего пункта. Нужно переходить на новую систему параметров,
/// либо добавить понятную клавишу добавления файла TXT».
///
/// Непонятно было по делу: кнопка «Параметры» на карточке версии спрашивала ТОЛЬКО старые записи
/// param_files, и всё, что заведено таблицами, с прошивки видно не было. Здесь заперто, что
/// спрашивается теперь одно место и находит оба вида — и что таблица, заведённая от прошивки,
/// привязывается к её шкафу.</summary>
public class SubtypeParamsTests
{
    private const string Folder = @"Z:\Software\Параметры\ПЖ\2.0\Danfoss";

    [Fact]
    public void Находит_и_старый_файл_и_свободную_таблицу()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        db.AddParamFile(new ParamFile
        {
            SubtypeId = 7,
            Manufacturer = "Danfoss",
            Filename = "fc51.par",
            DiskPath = Folder,
        });
        var tableId = db.AddParamTable(new ParamTable { Name = "Пуск по месту", SubtypeIds = "7" });

        var found = SubtypeParams.For(db, 7);

        Assert.False(found.Empty);
        Assert.Contains(found.Files, f => f.Filename == "fc51.par");
        Assert.Contains(found.Tables, t => t.Id == tableId);
    }

    /// <summary>Таблица при файле находится через записи param_files того же файла — своей привязки
    /// у неё нет и быть не должно (см. ParamTableBinding).</summary>
    [Fact]
    public void Находит_таблицу_живущую_при_файле()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        db.AddParamFile(new ParamFile
        {
            SubtypeId = 7,
            Manufacturer = "Danfoss",
            Filename = "fc51.par",
            DiskPath = Folder,
        });
        var tableId = db.AddParamTable(new ParamTable
        {
            Name = "Задание Modbus",
            DiskPath = Folder,
            Filename = "fc51.par",
        });

        var found = SubtypeParams.For(db, 7);

        Assert.Contains(found.Tables, t => t.Id == tableId);
    }

    /// <summary>У файла по записи на КАЖДЫЙ привязанный подтип, поэтому одна и та же таблица
    /// находится через каждую из них. Показать её человеку дважды — значит заставить гадать, какая
    /// из двух одинаковых строк настоящая.</summary>
    [Fact]
    public void Одна_таблица_не_показывается_дважды()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        foreach (var subtype in new[] { 7, 7 })
            db.AddParamFile(new ParamFile
            {
                SubtypeId = subtype,
                Manufacturer = "Danfoss",
                Filename = "fc51.par",
                DiskPath = Folder,
            });
        db.AddParamTable(new ParamTable { Name = "Задание Modbus", DiskPath = Folder, Filename = "fc51.par" });

        var found = SubtypeParams.For(db, 7);

        Assert.Single(found.Tables);
    }

    /// <summary>Чужие параметры в выдачу не попадают: иначе на карточке насосной станции оказались
    /// бы настройки частотника от канализационной.</summary>
    [Fact]
    public void Чужой_шкаф_не_попадает()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        db.AddParamFile(new ParamFile { SubtypeId = 9, Manufacturer = "Danfoss", Filename = "чужой.par", DiskPath = Folder });
        db.AddParamTable(new ParamTable { Name = "Чужая таблица", SubtypeIds = "9" });

        var found = SubtypeParams.For(db, 7);

        Assert.True(found.Empty);
        Assert.Equal(0, found.Count);
    }

    /// <summary>Шкаф неизвестен — отвечаем пустотой, а не падаем: подтип у результата поиска
    /// бывает не проставлен.</summary>
    [Fact]
    public void Без_подтипа_пусто()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        Assert.True(SubtypeParams.For(db, null).Empty);
    }

    /// <summary>Таблицу завели С КАРТОЧКИ прошивки — шкаф уже известен, и привязка проставляется
    /// сама. Без этого таблица не находится там, где её потом ищут, и человек заводит вторую.</summary>
    [Fact]
    public void Заведённая_от_прошивки_таблица_привязывается_к_её_шкафу()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        var id = db.AddParamTable(new ParamTable { Name = "Пуск по месту" });
        var table = db.GetParamTable(id);

        Assert.True(SubtypeParams.BindTo(db, table, 7));

        Assert.Contains(SubtypeParams.For(db, 7).Tables, t => t.Id == id);
        // Повторная привязка к тому же шкафу ничего не меняет и не плодит записей.
        Assert.False(SubtypeParams.BindTo(db, db.GetParamTable(id), 7));
        Assert.Equal("7", db.GetParamTable(id)!.SubtypeIds);
    }

    /// <summary>У таблицы ПРИ ФАЙЛЕ привязка выводится из записей param_files, и своё поле ей не
    /// заполняется никогда: два ответа на один вопрос разъехались бы на первой же правке.</summary>
    [Fact]
    public void Таблице_при_файле_своя_привязка_не_пишется()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        var id = db.AddParamTable(new ParamTable
        {
            Name = "Задание Modbus",
            DiskPath = Folder,
            Filename = "fc51.par",
        });

        Assert.False(SubtypeParams.BindTo(db, db.GetParamTable(id), 7));
        Assert.Equal("", db.GetParamTable(id)!.SubtypeIds);
    }
}
