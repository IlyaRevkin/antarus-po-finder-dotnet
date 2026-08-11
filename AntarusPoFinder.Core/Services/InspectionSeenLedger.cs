using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AntarusPoFinder.Core.Services;

/// <summary>«Когда этот файл ВПЕРВЫЕ появился в папке осмотра» — машинно-локальный журнальчик, из-за
/// отсутствия которого автоочистка сносила только что положенный файл.
///
/// <b>Что было не так.</b> Возраст файла считался по <c>File.GetLastWriteTime</c>. Для снимка,
/// сделанного самой программой, это то же самое, что «когда он тут появился». Но файл, скопированный
/// в папку СТОРОННИМ способом (перетащили из проводника, скачали из почты, положили с телефона),
/// приносит с собой ЧУЖУЮ дату изменения — фотография недельной давности, скопированная минуту назад,
/// выглядела как пролежавшая неделю и удалялась первым же тиком. Ровно жалоба: «я закинул файл в
/// папку, он там ещё 10 минут не лежит, а программа его уже почистила».
///
/// <b>Как теперь.</b> Каждый обход записывает сюда момент, когда файл впервые увидели в папке, и
/// возраст считается ОТ НЕГО. Дата изменения при этом тоже учитывается — берётся ПОЗДНЕЙШЕЕ из двух:
/// файл, который переписали на месте, снова становится «свежим», и это правильно.
///
/// Журнал заведомо неполон при первом запуске (файлы уже лежат, а записи о них нет) — и это
/// безопасное направление: они считаются увиденными ПРЯМО СЕЙЧАС и проживут ещё один полный срок, а
/// не исчезнут разом. Потерянный/битый журнал даёт ровно то же самое, поэтому чинить его не нужно —
/// он просто пересоздаётся.</summary>
public sealed class InspectionSeenLedger
{
    private readonly string _path;
    private readonly Dictionary<string, DateTime> _seen;
    private bool _dirty;

    private InspectionSeenLedger(string path, Dictionary<string, DateTime> seen)
    {
        _path = path;
        _seen = seen;
    }

    public static InspectionSeenLedger Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path));
                if (raw is not null)
                    return new InspectionSeenLedger(path, new Dictionary<string, DateTime>(raw, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception)
        {
            // Битый/недоступный журнал — начинаем с чистого: см. класс-док, это безопасное направление.
        }
        return new InspectionSeenLedger(path, new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Момент, с которого файл считается лежащим в папке. Незнакомый файл регистрируется
    /// прямо здесь — значит его отсчёт начинается с этого обхода, а не задним числом.</summary>
    public DateTime FirstSeen(string file, DateTime now)
    {
        if (_seen.TryGetValue(file, out var at)) return at;
        _seen[file] = now;
        _dirty = true;
        return now;
    }

    public void Forget(string file)
    {
        if (_seen.Remove(file)) _dirty = true;
    }

    /// <summary>Убирает записи о файлах, которых в папке больше нет, — иначе журнал рос бы вечно, а
    /// файл с тем же именем, положенный заново, унаследовал бы возраст прежнего.</summary>
    public void Prune(ISet<string> existing)
    {
        var stale = new List<string>();
        foreach (var key in _seen.Keys)
            if (!existing.Contains(key)) stale.Add(key);
        foreach (var key in stale) { _seen.Remove(key); _dirty = true; }
    }

    /// <summary>Best-effort: не сохранился — в худшем случае файлы проживут ещё один срок.</summary>
    public void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_seen));
            _dirty = false;
        }
        catch (Exception)
        {
            // см. док
        }
    }
}
