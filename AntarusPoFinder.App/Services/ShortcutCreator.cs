using System;
using System.IO;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Ярлык Windows через COM-объект WScript.Shell — способ, не требующий ни сторонних
/// пакетов, ни P/Invoke в IShellLink. Живёт в App, а не в Core: Core собирается под net8.0 без
/// привязки к Windows (см. IShortcutCreator).
///
/// Используется при загрузке одной прошивки/параметров сразу под несколько подтипов шкафов: реальные
/// файлы лежат на диске один раз, в папки остальных подтипов кладётся ярлык на них.</summary>
public sealed class ShortcutCreator : IShortcutCreator
{
    public void Create(string shortcutPath, string targetPath, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell недоступен в этой системе.");

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Не удалось создать WScript.Shell.");
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, new object[] { shortcutPath })
                ?? throw new InvalidOperationException("Не удалось создать ярлык.");

            var shortcutType = shortcut.GetType();
            SetProperty(shortcutType, shortcut, "TargetPath", targetPath);
            SetProperty(shortcutType, shortcut, "Description", description);
            // Рабочая папка = сама папка назначения (для папки) или папка файла — иначе ярлык
            // открывается с непредсказуемым текущим каталогом.
            SetProperty(shortcutType, shortcut, "WorkingDirectory",
                Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath) ?? "");
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void SetProperty(Type type, object instance, string name, string value) =>
        type.InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, instance, new object[] { value });
}
