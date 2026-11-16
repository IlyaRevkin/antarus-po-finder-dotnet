using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Loader;

namespace AntarusPoFinder.Core.Services;

/// <summary>ЗАМЕНА ФАЙЛА ПРОШИВКИ В УЖЕ ВЫПУЩЕННОЙ ВЕРСИИ НА СЕРВЕРЕ.
///
/// Тикет Киселёва от 11.11.2026: «добавить функционал для публикации пересобранной вручную LFS в
/// общий доступ (замена на сервере)». Наладчик пересобрал .lfs у себя — SMLogix'ом, руками, вне
/// программы, — и теперь ему надо, чтобы коллеги брали с диска уже исправленный файл.
///
/// <b>Это ЗАМЕНА, а не новая версия.</b> Номер ПО не выдаётся, строка в базе не заводится и не
/// правится, папка версии остаётся той же: прошивка та же самая, у неё починили сборку. Обычный
/// путь — «загрузить заново и получить sw+1» — здесь не годится: он плодит версию там, где
/// изменений в программе ПЛК не было, и ломает соответствие «номер в шкафу ↔ номер в программе».
/// Ровно та же мысль, по которой устроен <see cref="FirmwareExecutionBuild"/>.
///
/// <b>Прежний файл не исчезает.</b> Он уезжает в «Прошивка\Предыдущие\» под своим именем с отметкой
/// даты и времени и с дописанным <c>.old</c> (см. <see cref="BackupExtension"/>). Три причины
/// именно такого вида:
/// <list type="number">
/// <item>отдельная папка — чтобы прежний файл не путался среди файлов версии и не попадал под
///       правило «в папке версии один файл прошивки» (чистильщик диска внутрь подпапок «Прошивка\»
///       не заходит вовсе, см. DiskCleanupScanner.ScanVersion);</item>
/// <item>другое расширение — чтобы его НИКОГДА не выбрал ни лоадер, ни карточка: и то и другое ищет
///       файл по расширению (<see cref="LoaderFiles.Find"/>), и второй .lfs рядом рано или поздно
///       уехал бы в контроллер вместо нового;</item>
/// <item>именно <c>.old</c>, а не <c>.bak</c>: <see cref="JunkFiles"/> считает .bak мусором и
///       чистильщик предложил бы его удалить, а про .old там сказано прямо — «под таким именем на
///       этом диске вполне может лежать нужная предыдущая сборка».</item>
/// </list>
/// Вернуть прежнюю сборку человек может без программы: убрать у файла «.old» и положить обратно.
///
/// <b>Кто и когда заменил — пишется на диск</b>, в CHANGELOG.md папки версии
/// (<see cref="ChangelogFile.TryAppendReplacement"/>), а не только в локальную базу. Причина та же,
/// по которой там живёт отметка модерации: общий диск — единственный канал, который между машинами
/// точно работает, и подмена файла в чужой выпущенной версии обязана быть видна коллеге независимо
/// от того, доехал ли до него обмен конфигом.</summary>
public static class FirmwareFileReplace
{
    /// <summary>Что можно заменить этим способом. Список ровно из одного расширения, и это не
    /// недоделка: .lfs — единственный файл версии, который ПЕРЕСОБИРАЮТ, не меняя программы ПЛК.
    /// Исходник .psl правкой становится другой прошивкой, и ей полагается свой номер; документы и
    /// карты кладутся своими способами. Расширять список — отдельное решение, а не «заодно».</summary>
    public static readonly string[] ReplaceableExtensions = { LoaderFiles.LfsExtension };

    /// <summary>Что дописывается к имени сохранённого прежнего файла (см. док класса).</summary>
    public const string BackupExtension = ".old";

    /// <summary>Выбранный файл похож на то, что мы умеем заменять. Проверка по расширению, а не по
    /// содержимому: формата .lfs у нас нет, а отличить «не тот файл» от «тот» человек всё равно
    /// должен сам — наше дело не пустить сюда явно постороннее (архив, документ, .psl).</summary>
    public static bool LooksReplaceable(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && ReplaceableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Кому можно подменять файл на боевом сервере. Тем же, кто МОДЕРИРУЕТ прошивки, —
    /// наладчику и администратору (см. RolesConfig.RoleAccess, страница «Модерация прошивок»):
    /// решение «этот файл теперь едет в шкафы у всех» по сути то же самое, что решение модерации, и
    /// двух разных ответов на него быть не должно.
    ///
    /// Программисту НЕ даём намеренно, хотя прошивки заливает он: у него для исправленной сборки
    /// есть обычный путь — загрузить её версией. Замена молча меняет содержимое уже выданного
    /// номера, и чем меньше людей могут это сделать, тем лучше.</summary>
    public static bool CanReplace(string? role) => role is "administrator" or "naladchik";

    /// <summary>Что именно будет сделано — БЕЗ единой записи на диск. Нужен и окну подтверждения
    /// («что на что меняем и куда ляжет прежний»), и самой замене: считать пути дважды нельзя,
    /// иначе человек подтвердит одно, а произойдёт другое.</summary>
    /// <param name="TargetFile">Файл в папке версии, который будет заменён.</param>
    /// <param name="BackupFile">Куда уедет прежний файл.</param>
    /// <param name="Error">Почему нельзя. null — можно.</param>
    public sealed record Plan(string TargetFile, string BackupFile, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>Итог замены. Пути в нём — уже состоявшиеся, их показывают человеку и пишут в
    /// журнал.</summary>
    public sealed record Result(bool Ok, string? Error, string TargetFile, string BackupFile)
    {
        /// <summary>Файлы заменены, а вот запись «кто и когда» на диск не легла (нет прав на шаре,
        /// сеть отвалилась). Замену из-за этого НЕ откатываем — файл в шкафы уже поедет правильный,
        /// — но человеку про это говорим: без записи замена выглядит как чужая правка руками.</summary>
        public bool NoteMissing { get; init; }
    }

    /// <inheritdoc cref="Plan"/>
    /// <param name="versionDir">Папка версии на СЕТЕВОМ диске — та, что увидят коллеги.</param>
    /// <param name="sourceFile">Пересобранный файл, который кладём вместо прежнего.</param>
    /// <param name="now">Время, которое уйдёт в имя сохранённого файла и в запись.</param>
    public static Plan Prepare(string? versionDir, string? sourceFile, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || !SafeDirExists(versionDir))
            return new Plan("", "", "Папка версии не найдена на диске.");

        if (string.IsNullOrWhiteSpace(sourceFile) || !SafeFileExists(sourceFile))
            return new Plan("", "", "Выбранный файл не найден.");

        if (!LooksReplaceable(sourceFile))
            return new Plan("", "", "Заменить можно только файл "
                + string.Join(" / ", ReplaceableExtensions)
                + $". Выбран «{Path.GetFileName(sourceFile)}».");

        // Что заменяем — ищем тем же способом, которым карточка и лоадер ищут, ЧТО заливать в
        // контроллер. Иначе легко заменить не тот файл: в папке версии лежит и исходник, и сборка.
        var folder = VersionLayout.FirmwareWriteFolder(versionDir!);
        var ext = Path.GetExtension(sourceFile)!;
        var target = LoaderFiles.FindIn(VersionLayout.FirmwareFolders(versionDir), ext,
            skipFolders: new[] { VersionLayout.ReplacedWriteFolder(versionDir!) });

        if (target is null)
            return new Plan("", "", $"В этой версии нет файла {ext} — заменять нечего. "
                + "Если сборки там никогда не было, её кладут обычной загрузкой прошивки, а не заменой.");

        if (string.Equals(Path.GetFullPath(target), SafeFullPath(sourceFile), StringComparison.OrdinalIgnoreCase))
            return new Plan(target, "", "Выбран тот же самый файл, что лежит в версии, — менять нечего.");

        var backup = Path.Combine(VersionLayout.ReplacedWriteFolder(versionDir!),
            BackupName(Path.GetFileName(target), now));
        return new Plan(target, backup, null);

        string SafeFullPath(string p) { try { return Path.GetFullPath(p); } catch (Exception) { return p; } }
    }

    /// <summary>Имя сохранённого прежнего файла: «&lt;имя&gt;_&lt;ггггММдд_ЧЧмм&gt;&lt;расш&gt;.old».
    /// Отметка времени ровно в том виде, в каком она стоит в номере версии (см. FwVersionNumber), —
    /// человек на диске читает их одинаково; секунды не пишем по той же причине, по которой их нет
    /// в номере, но от повторной замены в ту же минуту защищаемся отдельно (см. <see cref="Replace"/>).</summary>
    public static string BackupName(string fileName, DateTime now) =>
        $"{Path.GetFileNameWithoutExtension(fileName)}_{now:yyyyMMdd_HHmm}{Path.GetExtension(fileName)}{BackupExtension}";

    /// <summary>Заменить файл прошивки в версии на диске.
    ///
    /// Порядок шагов выбран так, чтобы ЛЮБОЙ обрыв не оставил версию без рабочего файла:
    /// <list type="number">
    /// <item>прежний файл ПЕРЕНОСИТСЯ (а не копируется) в «Предыдущие\» — после этого места
    ///       целевого файла свободно, но прежний цел;</item>
    /// <item>новый кладётся через временное имя и одно переименование
    ///       (<see cref="LfsPublisher.Publish"/>) — обрыв посреди копирования по сетевой шаре
    ///       оставляет мусорный <c>.part</c>, а не битый .lfs;</item>
    /// <item>сорвалось — прежний файл возвращается на место, и версия остаётся ровно такой, какой
    ///       была. Это и есть главное требование тикета: ошибка на боевом сервере дорогая.</item>
    /// </list>
    /// Ходит на сетевую шару — звать только из фонового потока и только под правом
    /// <see cref="LongOperationRegistry"/>.</summary>
    /// <param name="who">Кто заменяет — попадёт в запись на диске.</param>
    public static Result Replace(string? versionDir, string? sourceFile, string who, DateTime now)
    {
        var plan = Prepare(versionDir, sourceFile, now);
        if (!plan.Ok) return new Result(false, plan.Error, plan.TargetFile, "");

        var backup = UniqueBackupPath(plan.BackupFile);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Move(plan.TargetFile, backup);
        }
        catch (Exception e)
        {
            return new Result(false, $"Не удалось сохранить прежний файл: {e.Message}", plan.TargetFile, "");
        }

        try
        {
            // Имя остаётся ПРЕЖНИМ, а не берётся у выбранного файла: на диске файл прошивки назван
            // номером версии (FirmwareNaming.BuildFirmwareFilename), и приехавшее с чужой машины
            // «проект1_испр.lfs» сломало бы и это правило, и чистильщик, который по нему сверяет.
            LfsPublisher.Publish(sourceFile!, Path.GetDirectoryName(plan.TargetFile)!,
                Path.GetFileName(plan.TargetFile));
        }
        catch (Exception e)
        {
            // Откат: без него версия осталась бы вообще без файла прошивки — хуже, чем со старым.
            try { File.Move(backup, plan.TargetFile); }
            catch (Exception) { /* вернуть не вышло — прежний файл всё равно цел, он в «Предыдущие\» */ }
            return new Result(false, $"Не удалось положить новый файл: {e.Message}", plan.TargetFile, "");
        }

        var noted = ChangelogFile.TryAppendReplacement(versionDir!, new FwFileReplacement(
            now.ToString("yyyy-MM-ddTHH:mm:ss"),
            (who ?? "").Trim(),
            Path.GetFileName(plan.TargetFile),
            Relative(versionDir!, backup)));

        AppLog.Info(AppLog.AreaOperation,
            $"замена файла прошивки: {plan.TargetFile} ← {sourceFile}, прежний в {backup}, кто: {who}");

        return new Result(true, null, plan.TargetFile, backup) { NoteMissing = !noted };
    }

    /// <summary>Все замены этой версии, от старой к новой, — то, что программа показывает рядом с
    /// автором и исходным названием. Читается с диска, поэтому зовётся из фонового обхода папки
    /// версии, а не из отрисовки.</summary>
    public static IReadOnlyList<FwFileReplacement> History(string? versionDir)
    {
        if (string.IsNullOrWhiteSpace(versionDir)) return Array.Empty<FwFileReplacement>();
        try { return ChangelogFile.TryRead(versionDir!)?.Replacements ?? (IReadOnlyList<FwFileReplacement>)Array.Empty<FwFileReplacement>(); }
        catch (Exception) { return Array.Empty<FwFileReplacement>(); }
    }

    /// <summary>Строка для карточки: кто и когда заменил файл в последний раз. Пусто — замен не
    /// было. Дата человеческая (дд.ММ.гггг ЧЧ:мм), как везде в выдаче; если дату в записи разобрать
    /// не удалось — показываем как записано, а не прячем всю строку: факт замены важнее её вида.</summary>
    public static string LastReplacementNote(IReadOnlyList<FwFileReplacement>? history)
    {
        if (history is null || history.Count == 0) return "";
        var last = history[^1];
        var when = DateTime.TryParse(last.At, out var parsed) ? parsed.ToString("dd.MM.yyyy HH:mm") : last.At;
        var who = string.IsNullOrWhiteSpace(last.By) ? "неизвестно кто" : last.By.Trim();
        var tail = history.Count > 1 ? $" (всего замен: {history.Count})" : "";
        return string.IsNullOrWhiteSpace(when) ? $"{who}{tail}" : $"{who}, {when}{tail}";
    }

    /// <summary>Защита от второй замены в ту же минуту: имя сохранённого файла строится по отметке
    /// времени без секунд, и перезаписать им прошлый сохранённый файл значило бы потерять ровно ту
    /// сборку, ради сохранения которой всё и делается.</summary>
    private static string UniqueBackupPath(string path)
    {
        if (!SafeFileExists(path)) return path;
        for (var n = 2; n < 100; n++)
        {
            var candidate = path[..^BackupExtension.Length] + $"_{n}" + BackupExtension;
            if (!SafeFileExists(candidate)) return candidate;
        }
        return path[..^BackupExtension.Length] + $"_{Guid.NewGuid():N}" + BackupExtension;
    }

    private static string Relative(string versionDir, string path)
    {
        try { return Path.GetRelativePath(versionDir, path); }
        catch (Exception) { return path; }
    }

    private static bool SafeDirExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    private static bool SafeFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }
}
