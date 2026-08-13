using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Что из настроек этикетки уезжает к коллегам, а что остаётся своим.
///
/// Требование: «настройки принтера и сохранение их для этикетки это полностью не синхрон
/// параметры, так как для каждой машины они уникальны, кроме дизайна и текста». Отсюда правило:
/// железо и подгонка — своё, оформление и тексты — общее.
///
/// Главный тест здесь — последний: он не даёт ЗАБЫТЬ новый ключ. Настройка этикетки, добавленная
/// когда-нибудь потом и не отнесённая ни к железу, ни к оформлению, роняет сборку, а не молча уезжает
/// (или молча не уезжает) к соседям.</summary>
public class LabelSettingsSyncTests
{
    /// <summary>Своё у каждой машины: описывает КОНКРЕТНЫЙ принтер и заправленную в него наклейку.</summary>
    private static readonly string[] PerMachine =
    {
        "label_printer",
        "label_width_mm", "label_height_mm",
        "label_margin_mm", "label_margin_left_mm", "label_margin_top_mm",
        "label_margin_right_mm", "label_margin_bottom_mm",
        "label_offset_x_mm", "label_offset_y_mm",
        "label_rotation",
    };

    /// <summary>Общее для всех: как этикетка выглядит и что на ней написано.</summary>
    private static readonly string[] Shared =
    {
        "label_qr_mm", "label_title_pt", "label_caption_pt",
        "label_show_link", "label_show_frame",
        "label_qr_style", "label_fancy_qr", "label_qr_place",
        "label_headline", "label_show_headline", "label_headline_place", "label_headline_align",
        "label_note", "label_hole_text",
    };

    [Theory]
    [MemberData(nameof(PerMachineKeys))]
    public void Hardware_and_fit_stay_on_this_machine(string key) =>
        Assert.Contains(key, ConfigSyncSkipKeys.Read());

    [Theory]
    [MemberData(nameof(SharedKeys))]
    public void Design_and_texts_travel_to_everyone(string key) =>
        Assert.DoesNotContain(key, ConfigSyncSkipKeys.Read());

    public static TheoryData<string> PerMachineKeys() => Data(PerMachine);
    public static TheoryData<string> SharedKeys() => Data(Shared);

    private static TheoryData<string> Data(string[] keys)
    {
        var data = new TheoryData<string>();
        foreach (var key in keys) data.Add(key);
        return data;
    }

    [Fact]
    public void Every_saved_label_setting_is_classified()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        new LabelLayout().SaveTo(cfg);

        var saved = db.GetAllSettings().Keys
            .Where(k => k.StartsWith("label_", System.StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(saved);

        var known = PerMachine.Concat(Shared).ToHashSet(System.StringComparer.Ordinal);
        var unclassified = saved.Where(k => !known.Contains(k)).ToList();
        Assert.True(unclassified.Count == 0,
            "Новая настройка этикетки не отнесена ни к железу, ни к оформлению: " + string.Join(", ", unclassified) +
            ". Решите, уезжает ли она к коллегам, и допишите её в ConfigSyncService.SkipSettingsKeys либо в список общих здесь.");
    }

    [Fact]
    public void Per_side_margins_survive_a_save_and_read_round_trip()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        var layout = new LabelLayout
        {
            Margins = new LabelMargins(1, 2, 3, 4),
            Rotation = LabelRotation.Clockwise90,
            NoteText = "Договор 42",
        };
        layout.SaveTo(cfg);

        var read = LabelLayout.FromConfig(cfg);
        Assert.Equal(new LabelMargins(1, 2, 3, 4), read.Margins);
        Assert.Equal(LabelRotation.Clockwise90, read.Rotation);
        Assert.Equal("Договор 42", read.NoteText);
    }

    [Fact]
    public void Old_single_margin_setting_is_still_read_when_per_side_ones_were_never_saved()
    {
        // Обновление программы не должно обнулить подобранный отступ: пока полей по сторонам в базе
        // нет, читается прежний единый ключ.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.SetLabelNumber("label_margin_mm", 4.5);

        Assert.Equal(new LabelMargins(4.5, 4.5, 4.5, 4.5), LabelLayout.FromConfig(cfg).Margins);
    }

    [Fact]
    public void Saving_keeps_the_old_single_margin_key_for_older_program_versions()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        new LabelLayout { Margins = new LabelMargins(2, 5, 3, 6) }.SaveTo(cfg);

        // Самое узкое поле: версия, которая про стороны не знает, точно не выведет содержимое за край.
        Assert.Equal(2, cfg.LabelNumber("label_margin_mm", -1));
    }
}
