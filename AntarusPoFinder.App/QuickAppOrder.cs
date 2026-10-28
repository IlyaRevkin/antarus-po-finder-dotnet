using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.App;

/// <summary>Правила перетаскивания в «Быстрый доступ»: куда встаёт перенесённая строка и что вообще
/// можно бросить в список.
///
/// Вынесено из страницы настроек отдельно, потому что ошибиться тут легко и незаметно: сдвиг на
/// единицу при переносе вниз даёт «перетащил на место соседа, а встало через одного», и такое ловится
/// только руками. Здесь это проверяется тестами, а страница остаётся тем, чем должна быть, — разметкой
/// и обработчиками.</summary>
public static class QuickAppOrder
{
    /// <summary>Переставить строку с позиции <paramref name="from"/> туда, где сейчас стоит
    /// <paramref name="to"/>.
    ///
    /// Индекс берётся ДО изъятия строки намеренно, и для переноса вниз это ровно то, чего ждёт рука:
    /// строка, на которую бросили, после изъятия поднимается на единицу, а перенесённая встаёт под
    /// ней — «бросил на соседа сверху вниз, встал под соседом». Для переноса вверх сдвига нет вовсе
    /// и строка занимает место цели. Поправка «+1 при движении вниз», которая напрашивается, даёт
    /// промах через одного.</summary>
    public static void Move<T>(IList<T> items, int from, int to)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (from < 0 || from >= items.Count) return;
        if (to < 0) to = 0;
        if (to >= items.Count) to = items.Count - 1;
        if (from == to) return;

        var item = items[from];
        items.RemoveAt(from);
        items.Insert(Math.Min(to, items.Count), item);
    }

    /// <summary>Что имеет смысл класть в быстрый доступ. Программы и ярлыки — да; папки — тоже да,
    /// их открывает проводник, и наладчики кладут туда рабочие каталоги.
    ///
    /// Документ (pdf, docx) не запрещаем: Windows откроет его тем, чем положено, и запрет здесь
    /// означал бы спор с человеком о его же работе. Отсекаем только то, что бросают по ошибке —
    /// пустые пути и несуществующие файлы.</summary>
    public static IEnumerable<string> Acceptable(IEnumerable<string> paths) =>
        (paths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => File.Exists(p) || Directory.Exists(p));

    /// <summary>Имя ярлыка по умолчанию — имя файла без расширения, для папки — имя папки.
    /// Человек переименует, если захочет; пустая строка в списке выглядела бы поломкой.</summary>
    public static string NameFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(trimmed)) return Path.GetFileName(trimmed);
        var name = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? Path.GetFileName(trimmed) : name;
    }
}
