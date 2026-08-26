using System.IO;
using System.Windows;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>«Эту версию сейчас трогать нельзя» — одной строкой на все места, где над версией
/// что-то делают.
///
/// Появилось вместе с немодальными окнами долгих операций. Пока окно заливки/сборки запирало
/// программу, до кнопок «Откатить» и «Удалить» было не дотянуться физически; теперь программа
/// свободна, а сборка LFS в это самое время пишет .lfs в папку версии. Удалить запись из каталога
/// или откатить версию посреди записи — значит получить на диске файл, которому в базе больше ничего
/// не соответствует, причём молча.
///
/// Отказ показывается СООБЩЕНИЕМ, а не погашенной кнопкой: погашенная кнопка ничего не объясняет, и
/// человек решает, что программа сломалась.</summary>
internal static class BusySubjectGuard
{
    /// <summary>true — действие запрещено, объяснение уже показано.</summary>
    public static bool Blocked(AppServices services, string? versionFolder, string caption)
    {
        var reason = services.Operations.SubjectBusyReason(FolderKey(versionFolder, services));
        if (reason is null) return false;
        AppMessageBox.Show(reason, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>Ключ папки версии по записанному в базе пути. Путь мог быть записан коллегой в его
    /// форме диска — приводим к своей тем же <see cref="FirmwarePathLocalizer"/>, которым это делают
    /// все остальные (иначе ключ занятой папки и ключ проверяемой не совпали бы, и защита молча
    /// пропускала бы всё).
    ///
    /// disk_path может указывать и на ОДИНОЧНЫЙ ФАЙЛ — тогда «папка версии» это его родитель:
    /// операции занимают именно папку.</summary>
    private static string FolderKey(string? diskPath, AppServices services)
    {
        var localized = FirmwarePathLocalizer.Localize(diskPath ?? "", services.Cfg.RootPath());
        if (string.IsNullOrEmpty(localized)) return LongOperationSubject.None;
        if (!Directory.Exists(localized) && Path.GetDirectoryName(localized) is { Length: > 0 } parent)
            localized = parent;
        return LongOperationSubject.Folder(localized);
    }
}
