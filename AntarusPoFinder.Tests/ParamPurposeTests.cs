using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Назначение строки таблицы параметров — «для наладки» и «для сервиса»
/// (Core/Services/ParamPurposes.cs и столбец purpose в param_table_rows).
///
/// Главное, что здесь проверяется, — безопасность отбора: неразмеченная строка обязана оставаться
/// видной. Спрятанная от наладчика строка — это невыставленный параметр на объекте, и цена ошибки
/// тут выше, чем у любой другой мелочи в этой таблице.</summary>
public class ParamPurposeTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    public ParamPurposeTests() => _db = new Database(_dbFile.Path);

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private static ParamTableRow Row(string code, string purpose = "", string group = "Настройка ШУ",
        string title = "", string state = ParamValueState.Set) => new()
    {
        Kind = code.Length == 0 ? ParamRowKind.Note : ParamRowKind.Param,
        Code = code,
        Title = title.Length > 0 ? title : "Параметр " + code,
        GroupName = group,
        Purpose = purpose,
        ValueState = state,
    };

    [Fact]
    public void Format_svodit_nabor_k_odnomu_vidu()
    {
        Assert.Equal("commissioning,service",
            ParamPurposes.Normalize("service, COMMISSIONING"));
        Assert.Equal("", ParamPurposes.Normalize("  "));
        // Неизвестный токен отбрасывается: он мог приехать с машины новее этой.
        Assert.Equal("service", ParamPurposes.Normalize("service,montazh"));
    }

    [Fact]
    public void Otbor_ne_pryachet_nerazmechennoe()
    {
        var rows = new List<ParamTableRow>
        {
            Row("P0-02", ParamRowPurpose.Commissioning),
            Row("P1-01", ParamRowPurpose.Service),
            Row("P0-17"),
            Row("", title: "В ПЛК выставить частоту 55 Гц"),
        };

        var service = ParamPurposes.Filter(rows, ParamPurposes.ServiceLabel);
        // Сервисная, неразмеченная и пояснение — да; чисто наладочная — нет.
        Assert.Equal(new[] { "P1-01", "P0-17", "" }, service.Select(r => r.Code));
        Assert.Equal(4, ParamPurposes.Filter(rows, ParamPurposes.AnyLabel).Count);
        Assert.Equal(1, ParamPurposes.Unmarked(rows));
    }

    [Fact]
    public void Stroka_obeih_naznachenij_vidna_v_oboih_otborah()
    {
        var rows = new List<ParamTableRow> { Row("P1-03", "commissioning,service") };
        Assert.Single(ParamPurposes.Filter(rows, ParamPurposes.ServiceLabel));
        Assert.Single(ParamPurposes.Filter(rows, ParamPurposes.CommissioningLabel));
    }

    [Fact]
    public void Ugadka_uznaet_dvigatel_i_svyaz_a_somnitelnoe_ostavlyaet_pustym()
    {
        Assert.Equal(ParamRowPurpose.Service,
            ParamPurposes.Guess(Row("P1-02", group: "Двигатель", title: "Номинальный ток двигателя")));
        Assert.Equal(ParamRowPurpose.Commissioning,
            ParamPurposes.Guess(Row("PD-01", title: "Скорость обмена Modbus")));
        // Значение снимается по месту — с шильдика установленного двигателя, это сервис.
        Assert.Equal(ParamRowPurpose.Service,
            ParamPurposes.Guess(Row("P1-05", title: "Мощность", state: ParamValueState.OnSite)));
        Assert.Equal("", ParamPurposes.Guess(Row("P7-11", group: "Прочее", title: "Код изделия")));
        Assert.Equal("", ParamPurposes.Guess(Row("", title: "В ПЛК выставить частоту 55 Гц")));
    }

    [Fact]
    public void Ugadka_ne_perepisyvaet_ruchnuyu_razmetku()
    {
        var rows = new List<ParamTableRow>
        {
            Row("P1-02", ParamRowPurpose.Commissioning, group: "Двигатель", title: "Номинальный ток двигателя"),
            Row("PD-01", title: "Скорость обмена Modbus"),
        };

        Assert.Equal(1, ParamPurposes.GuessAll(rows));
        Assert.Equal(ParamRowPurpose.Commissioning, rows[0].Purpose);
        Assert.Equal(ParamRowPurpose.Commissioning, rows[1].Purpose);
    }

    [Fact]
    public void Perezalivka_ne_teryaet_razmetku()
    {
        var before = new List<ParamTableRow>
        {
            Row("P0-02", ParamRowPurpose.Commissioning),
            Row("P1-01", ParamRowPurpose.Service),
        };
        // Заново разобранный файл о назначениях не знает — в исходном txt их нет.
        var fresh = new List<ParamTableRow> { Row("P0-02"), Row("P1-01"), Row("P5-00") };

        Assert.Equal(2, ParamPurposes.CarryOver(before, fresh));
        Assert.Equal(ParamRowPurpose.Commissioning, fresh[0].Purpose);
        Assert.Equal(ParamRowPurpose.Service, fresh[1].Purpose);
        Assert.Equal("", fresh[2].Purpose);
    }

    [Fact]
    public void Perenos_ne_putaet_stroki_raznyh_apparatov()
    {
        var before = new List<ParamTableRow> { Row("P0-02", ParamRowPurpose.Service) };
        before[0].Applicability = "Только для ПЧ №1";
        var fresh = new List<ParamTableRow> { Row("P0-02") };
        fresh[0].Applicability = "Только для ПЧ №2";

        Assert.Equal(0, ParamPurposes.CarryOver(before, fresh));
        Assert.Equal("", fresh[0].Purpose);
    }

    [Fact]
    public void Razmetka_dohodit_do_bazy_i_obratno()
    {
        var tableId = _db.AddParamTable(new ParamTable
        {
            DiskPath = @"D:\ПО\ESQ", Filename = "ESQ-230.par", Name = "Задание Modbus",
        });
        var (revisionId, _) = ParamTableEditing.SaveRevision(_db, tableId, new List<ParamTableRow>
        {
            Row("P0-02", "SERVICE, commissioning"),
            Row("P1-01"),
        }, "первая заливка", "Илья");

        var stored = _db.GetParamTableRows(revisionId);
        // Записано в едином виде, а не как набрал человек: иначе порядок токенов всплыл бы
        // в разборе изменений правкой строки, которую никто не правил.
        Assert.Equal("commissioning,service", stored[0].Purpose);
        Assert.Equal("", stored[1].Purpose);
    }

    [Fact]
    public void Smena_naznacheniya_popadaet_v_razbor_izmenenij()
    {
        var before = new List<ParamTableRow> { Row("P0-02", ParamRowPurpose.Commissioning) };
        var after = new List<ParamTableRow> { Row("P0-02", ParamRowPurpose.Service) };

        var diff = ParamTableDiff.Compare(before, after);
        Assert.Equal(1, diff.Edited);

        // А перестановка токенов — не изменение.
        Assert.False(ParamTableDiff.Compare(
            new List<ParamTableRow> { Row("P0-02", "commissioning,service") },
            new List<ParamTableRow> { Row("P0-02", "service,commissioning") }).Any);
    }
}
