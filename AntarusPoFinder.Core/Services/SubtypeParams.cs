using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>ЧТО ЗА ПАРАМЕТРЫ ПЧ/УПП ОТНОСЯТСЯ К ЭТОМУ ШКАФУ — один ответ на оба устройства сразу.
///
/// <b>Откуда задача.</b> Тикет наладчика от 18.11.2026 дословно: «Непонятно как добавлять новые
/// параметры для ПЧ к текущим прошивкам, в редакторе нет подходящего пункта. Нужно переходить на
/// новую систему параметров, либо добавить понятную клавишу добавления файла TXT».
///
/// Разбор показал, что непонятно было не по невнимательности. Кнопка «Параметры» на карточке версии
/// спрашивала ТОЛЬКО старые записи param_files и, не найдя их, отправляла человека «загрузить
/// параметры в разделе Параметры» — а карточка загрузки на той странице давно скрыта (переход на
/// таблицы, см. ParamsView). То есть совет вёл к кнопке, которой нет. Сами таблицы при этом с
/// карточки не показывались вовсе: заведённое в новой системе с прошивки было не видно.
///
/// Поэтому спрашивать надо ОДНО место, и оно знает про оба устройства:
/// <list type="bullet">
/// <item>старые файлы параметров — записи param_files этого подтипа;</item>
/// <item>таблицы при файле — через записи param_files того же подтипа (привязка выводится,
///       см. <see cref="ParamTableBinding"/>);</item>
/// <item>свободные таблицы — по собственному полю subtype_ids.</item>
/// </list>
///
/// Дубли схлопываются по Id таблицы: одна и та же таблица при файле находится через каждую запись
/// param_files этого файла, а запись у файла по одной на КАЖДЫЙ привязанный подтип.</summary>
public static class SubtypeParams
{
    /// <summary>Всё, что относится к подтипу шкафа. Пустые списки — штатный ответ, а не ошибка.</summary>
    /// <param name="Files">Старые файлы параметров (.par и прочие выгрузки конфигуратора).</param>
    /// <param name="Tables">Таблицы параметров — и свободные, и живущие при файле.</param>
    public sealed record Found(IReadOnlyList<ParamFile> Files, IReadOnlyList<ParamTable> Tables)
    {
        /// <summary>Нечего показывать вовсе — ни файла, ни таблицы.</summary>
        public bool Empty => Files.Count == 0 && Tables.Count == 0;

        public int Count => Files.Count + Tables.Count;
    }

    public static Found For(Database db, int? subtypeId)
    {
        if (subtypeId is not int id)
            return new Found(Array.Empty<ParamFile>(), Array.Empty<ParamTable>());

        var files = db.GetParamFiles(subtypeId: id);

        // Ключ таблицы при файле — сам файл (папка + имя), а не строка param_files: у одного файла
        // этих строк столько, сколько у него подтипов. Поэтому идём от найденных файлов к их
        // таблицам и схлопываем по Id — иначе одна таблица пришла бы несколько раз.
        var tables = new List<ParamTable>();
        var seen = new HashSet<int>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.DiskPath) || string.IsNullOrWhiteSpace(file.Filename)) continue;
            foreach (var table in db.GetParamTablesForFile(file.DiskPath, file.Filename))
                if (table.Id is int tableId && seen.Add(tableId))
                    tables.Add(table);
        }

        // Свободные таблицы: у них файла нет, и привязка живёт своим полем документа.
        foreach (var table in db.GetParamTables())
        {
            if (table.Id is not int tableId || seen.Contains(tableId)) continue;
            if (!string.IsNullOrWhiteSpace(table.DiskPath) || !string.IsNullOrWhiteSpace(table.Filename)) continue;
            if (!ParamTableBinding.ParseIds(table.SubtypeIds).Contains(id)) continue;
            seen.Add(tableId);
            tables.Add(table);
        }

        return new Found(files, tables);
    }

    /// <summary>Привязать таблицу к подтипу шкафа, не трогая уже записанные. Нужно там, где таблицу
    /// заводят ОТ ПРОШИВКИ: шкаф в этот момент известен, и спрашивать его ещё раз значит заставлять
    /// человека повторить то, что он только что сделал кнопкой.
    ///
    /// Только для СВОБОДНОЙ таблицы: у таблицы при файле привязка выводится из записей param_files,
    /// и собственное поле у неё не заполняется никогда — два ответа на один вопрос разъехались бы
    /// на первой же правке (см. <see cref="ParamTableBinding"/>).
    ///
    /// Возвращает true, если привязка действительно добавилась.</summary>
    public static bool BindTo(Database db, ParamTable? table, int? subtypeId)
    {
        if (table?.Id is not int id || subtypeId is not int wanted || wanted <= 0) return false;
        if (!string.IsNullOrWhiteSpace(table.DiskPath) || !string.IsNullOrWhiteSpace(table.Filename)) return false;

        var ids = ParamTableBinding.ParseIds(table.SubtypeIds);
        if (ids.Contains(wanted)) return false;

        ids.Add(wanted);
        var formatted = ParamTableBinding.FormatIds(ids);
        db.UpdateParamTableSubtypes(id, formatted);
        table.SubtypeIds = formatted;
        return true;
    }
}
