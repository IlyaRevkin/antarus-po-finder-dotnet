namespace AntarusPoFinder.Core.Services;

/// <summary>Создание ярлыка Windows (.lnk) — реализация живёт в App (Core собирается под net8.0 без
/// привязки к Windows, а единственный вменяемый способ сделать .lnk — COM WScript.Shell). Нужен для
/// загрузки одной прошивки/параметров сразу под несколько подтипов шкафов: файлы кладутся один раз,
/// в папки остальных подтипов кладётся ярлык.</summary>
public interface IShortcutCreator
{
    /// <summary>Создаёт (или перезаписывает) ярлык shortcutPath, указывающий на targetPath — файл
    /// или папку. Бросает исключение при неудаче: вызывающий код превращает её в предупреждение,
    /// сама загрузка из-за ярлыка не отменяется.</summary>
    void Create(string shortcutPath, string targetPath, string description);
}
