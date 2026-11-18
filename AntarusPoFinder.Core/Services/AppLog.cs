using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>Уровень события. Четыре, не больше: всё, что тоньше, на практике превращается в
/// «пишем всё подряд» и журнал перестают читать.</summary>
public enum AppLogLevel
{
    /// <summary>Подробность для разбора: ход долгой операции, промежуточные шаги. По умолчанию
    /// ПИШЕТСЯ — журнал для того и заведён, чтобы баг было видно без повторения.</summary>
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>ФОРМАТ СТРОКИ ЖУРНАЛА — одно событие, одна строка.
///
/// <b>Почему именно так.</b> Журнал читают по SSH из <c>diag/</c>, через <c>grep</c>, <c>awk</c> и
/// глазами, а не программой. Поэтому строка плоская и колонки фиксированы:
///
/// <code>
/// 2026-11-11 14:20:03.123 ERROR disk     обход Z:\Software сорвался: отказано в доступе
/// │                       │     │        └ текст
/// │                       │     └ подсистема (латиницей, из Areas)
/// │                       └ уровень
/// └ местное время
/// </code>
///
/// Время МЕСТНОЕ, в отличие от слепка диска (там UTC). Слепок сравнивают между машинами, а журнал
/// читают рядом с жалобой человека: «упало около двух» надо находить поиском по «14:», а не
/// пересчётом часовых поясов в уме.
///
/// <b>Перевод строки внутри события запрещён.</b> Стек исключения — это десяток строк, и если их
/// выпустить как есть, <c>grep ERROR</c> покажет первую строку беды и спрячет девять остальных, а
/// подсчёт событий по строкам начнёт врать. Поэтому переводы строк едут экранированными (<c>\n</c>
/// двумя знаками), и разворачивает их читающий:
/// <code>grep ' ERROR ' app.log | sed 's/\\n/\n/g'</code></summary>
public static class AppLogFormat
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>Ширина колонки подсистемы. Выравнивание — не украшение: без него глаз не отличает
    /// колонку от начала текста, и журнал на тысячу строк читается вдвое медленнее.</summary>
    public const int AreaWidth = 8;

    public static string LevelText(AppLogLevel level) => level switch
    {
        AppLogLevel.Debug => "DEBUG",
        AppLogLevel.Info => "INFO ",
        AppLogLevel.Warn => "WARN ",
        _ => "ERROR",
    };

    /// <summary>Разобрать уровень обратно (нужно отбору при чтении и тестам). Неизвестное —
    /// <see cref="AppLogLevel.Info"/>: чужая строка не повод падать.</summary>
    public static AppLogLevel ParseLevel(string text) => (text ?? "").Trim().ToUpperInvariant() switch
    {
        "DEBUG" => AppLogLevel.Debug,
        "WARN" => AppLogLevel.Warn,
        "ERROR" => AppLogLevel.Error,
        _ => AppLogLevel.Info,
    };

    /// <summary>Убрать из текста всё, что ломает правило «одна строка — одно событие». Обратная
    /// косая черта экранируется ПЕРВОЙ, иначе развернуть написанное обратно было бы нельзя: путь
    /// <c>C:\new</c> и перевод строки выглядели бы одинаково.</summary>
    public static string Escape(string? text) => (text ?? "")
        .Replace("\\", "\\\\")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n")
        .Replace("\t", "\\t");

    /// <summary>Развернуть обратно — тем же кодом, которым сворачивали.</summary>
    public static string Unescape(string? text)
    {
        var src = text ?? "";
        var sb = new StringBuilder(src.Length);
        for (var i = 0; i < src.Length; i++)
        {
            if (src[i] != '\\' || i + 1 >= src.Length) { sb.Append(src[i]); continue; }
            switch (src[++i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(src[i]); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Подсистема в том виде, в каком она попадает в колонку: латиница, нижний регистр,
    /// без пробелов. Пробел внутри колонки разъехался бы с разбором по <c>awk</c>, а кириллица —
    /// с набором этого слова в командной строке по SSH.</summary>
    public static string Area(string? area)
    {
        var raw = (area ?? "").Trim().ToLowerInvariant();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' ? ch : '-');
        var cleaned = sb.ToString().Trim('-');
        return cleaned.Length == 0 ? "app" : cleaned;
    }

    /// <summary>Собрать строку целиком. Без перевода строки на конце — его ставит тот, кто пишет
    /// в файл.</summary>
    public static string Line(DateTime at, AppLogLevel level, string area, string? text) =>
        at.ToString(TimeFormat, CultureInfo.InvariantCulture) + " " +
        LevelText(level) + " " +
        Area(area).PadRight(AreaWidth) + " " +
        Escape(text);
}

/// <summary>РОТАЦИЯ ЖУРНАЛА: по размеру и по сроку.
///
/// Журнал, который растёт вечно, — это не журнал, а бомба замедленного действия на диске у
/// человека, который о нём не знает. Поэтому правил два и они разные по смыслу:
///
/// • <b>по размеру</b> — текущий файл закрывается и откладывается в архив, как только перерос
///   потолок. Это защита от одного плохого дня: цикл, который пишет ошибку по десять раз в
///   секунду, не должен съесть диск;
/// • <b>по числу и сроку архивов</b> — старое удаляется. Срок важнее числа: жалоба приходит
///   «на прошлой неделе было», и неделя должна лежать целиком, сколько бы файлов она ни заняла.
///
/// Текущий файл не удаляется НИКОГДА, каким бы старым он ни был: журнал без сегодняшней строки
/// выглядит как «программа не запускалась».</summary>
public static class AppLogRotation
{
    /// <summary>Потолок текущего файла. Мегабайт — это примерно десять тысяч событий: хватает на
    /// неделю обычной работы и влезает в один <c>less</c>.</summary>
    public const long DefaultMaxBytes = 1_000_000;

    /// <summary>Сколько архивов держать сверх текущего.</summary>
    public const int DefaultKeepFiles = 8;

    /// <summary>Сколько суток держать архивы. Месяц: горизонт, на котором ещё помнят «когда всё
    /// было нормально».</summary>
    public const int DefaultKeepDays = 30;

    public const string CurrentName = "app.log";
    public const string ArchivePattern = "app-*.log";

    public static bool NeedsRoll(long currentBytes, long maxBytes) =>
        maxBytes > 0 && currentBytes >= maxBytes;

    /// <summary>Имя архива. Время в имени, а не порядковый номер: при номерах (app.1, app.2)
    /// каждая ротация переименовывает ВСЕ файлы, и любой сбой посередине оставляет дыру — а по
    /// времени имя пишется один раз и больше не трогается.</summary>
    public static string RolledName(DateTime at) =>
        $"app-{at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log";

    /// <summary>Время из имени архива. null — имя не наше, трогать файл нельзя.</summary>
    public static DateTime? StampOf(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        if (!name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return null;
        var middle = name[4..^4];
        return DateTime.TryParseExact(middle, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at) ? at : null;
    }

    /// <summary>Что из архивов пора удалить. Возвращает имена в том виде, в каком их передали.
    ///
    /// Чужие файлы в папке (имя не разбирается) не удаляются вовсе: папка журналов — не наша
    /// собственность, в неё может что-нибудь положить человек.</summary>
    public static IReadOnlyList<string> ToDelete(
        IEnumerable<string> archiveNames, DateTime now,
        int keepFiles = DefaultKeepFiles, int keepDays = DefaultKeepDays)
    {
        var ours = (archiveNames ?? Array.Empty<string>())
            .Select(n => (Name: n, At: StampOf(n)))
            .Where(x => x.At.HasValue)
            .OrderByDescending(x => x.At!.Value)
            .ToList();

        var doomed = new List<string>();
        for (var i = 0; i < ours.Count; i++)
        {
            var tooMany = keepFiles >= 0 && i >= keepFiles;
            var tooOld = keepDays > 0 && now - ours[i].At!.Value > TimeSpan.FromDays(keepDays);
            if (tooMany || tooOld) doomed.Add(ours[i].Name);
        }
        return doomed;
    }
}

/// <summary>ЕДИНЫЙ ЖУРНАЛ ПРИЛОЖЕНИЯ — то, по чему баг находят без жалобы.
///
/// <b>Зачем.</b> Диагностика в программе была точечной: журнал заливки в ПЛК
/// (<see cref="LoaderDiagnostics"/>) и записи «не нашлась панель». Каждая такая выросла из
/// КОНКРЕТНОЙ беды, которую до того искали вслепую, и каждая окупилась (по «не найден HMI» за
/// неделю приехал 51 случай с шести машин, и все оказались дырой в данных). Но заводить новый
/// канал под каждую следующую беду — значит всегда отставать на одну жалобу. Этот журнал пишет
/// обычную жизнь программы постоянно: старт и выход, версию, машину, пользователя, необработанные
/// исключения со стеком, ошибки работы с сетевой папкой и базой, долгие операции и их исход,
/// неудачи синхронизации конфига и выкладки в хранилище.
///
/// <b>Чего здесь нет.</b> Секретов. Журнал уезжает наружу сам (см. <see cref="AppLogDrop"/>), и
/// перед отправкой проверяется тем же кодом, что и слепок диска
/// (<see cref="DiskSnapshotSafety"/>): нашлось — не отправляем и называем причину. Но проверка
/// перед отправкой — последний рубеж, а не разрешение писать в журнал что попало: пароль, попавший
/// в текст ошибки, заткнёт ВЕСЬ журнал этой машины, потому что отказ касается всей пачки.
///
/// <b>Ничего не бросает и ничего не показывает.</b> Журнал, который мешает работать, хуже
/// отсутствующего: все операции с файлом обёрнуты и глушатся. Правило проекта «службы работают
/// молча» относится к нему в полной мере.</summary>
public static class AppLog
{
    // ── Подсистемы ───────────────────────────────────────────────────────────────────────────
    //
    // Латиницей и коротко: это колонка, по которой отбирают строки («grep ' disk '»). Список
    // открытый — строка, которой здесь нет, попадёт в журнал как есть; константы заведены ради
    // того, чтобы одна и та же подсистема не называлась в трёх местах тремя словами.

    public const string AreaApp = "app";
    public const string AreaDisk = "disk";
    public const string AreaDb = "db";
    public const string AreaSync = "sync";
    public const string AreaStore = "store";
    public const string AreaSnapshot = "snap";
    public const string AreaOperation = "oper";
    public const string AreaLog = "log";
    public const string AreaBackup = "backup";

    /// <summary>Поиск и его разбор (см. SearchTraceDrop).</summary>
    public const string AreaSearch = "search";

    private static readonly object Gate = new();
    private static string? _folder;

    /// <summary>Папка журналов. Отдельная подпапка в профиле, а не корень: рядом лежат база,
    /// локальные копии прошивок и очередь диагностики, и «удалить журналы» должно быть одним
    /// движением, которое ничего больше не заденет.</summary>
    public static string Folder
    {
        get => _folder ??= Path.Combine(ConfigService.AppData, "logs");
        set { lock (Gate) _folder = value; }
    }

    public static string CurrentPath => Path.Combine(Folder, AppLogRotation.CurrentName);

    public static long MaxBytes { get; set; } = AppLogRotation.DefaultMaxBytes;
    public static int KeepFiles { get; set; } = AppLogRotation.DefaultKeepFiles;
    public static int KeepDays { get; set; } = AppLogRotation.DefaultKeepDays;

    /// <summary>Выключатель на случай, когда писать нельзя вовсе (разовые прогоны в тестах).
    /// Настройкой человека НЕ является: журнал — это не услуга, а то, чем потом чинят.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Debug(string area, string text) => Write(AppLogLevel.Debug, area, text);
    public static void Info(string area, string text) => Write(AppLogLevel.Info, area, text);
    public static void Warn(string area, string text) => Write(AppLogLevel.Warn, area, text);
    public static void Error(string area, string text) => Write(AppLogLevel.Error, area, text);

    /// <summary>Ошибка со стеком. Стек едет ЦЕЛИКОМ и в одной строке (см.
    /// <see cref="AppLogFormat.Escape"/>): обрезанный стек отвечает на вопрос «где упало» ровно
    /// наполовину, а половина ответа здесь хуже, чем ничего, — по ней чинят не то.</summary>
    public static void Error(string area, string text, Exception? ex)
    {
        if (ex is null) { Error(area, text); return; }
        Write(AppLogLevel.Error, area, $"{text}: {ex.GetType().Name}: {ex.Message} | {ex}");
    }

    public static void Write(AppLogLevel level, string area, string? text)
    {
        if (!Enabled) return;
        var line = AppLogFormat.Line(DateTime.Now, level, area, text);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                RollIfNeeded();
                File.AppendAllText(CurrentPath, line + "\n", Utf8NoBom);
            }
        }
        catch (Exception)
        {
            // Журнал, который мешает работать, хуже отсутствующего.
        }
    }

    /// <summary>Заголовок сеанса: версия, машина, пользователь. Отдельной записью, а не колонками
    /// в каждой строке, — иначе 40 знаков машины и пользователя повторялись бы в каждом событии,
    /// а меняются они ровно раз за сеанс.</summary>
    public static void Session(string appVersion, string machine, string user, string what = "старт")
        => Info(AreaApp, $"{what}: версия {appVersion}, машина {machine}, пользователь {user}, " +
                         $"ОС {Environment.OSVersion.VersionString}, процесс {Environment.ProcessId}");

    /// <summary>Закрыть текущий файл и отложить его в архив. Возвращает полный путь архива или
    /// null, если откладывать было нечего. Нужно не только ротации по размеру: отправка наружу
    /// тоже начинается с этого — слать файл, в который прямо сейчас пишут, значит однажды отправить
    /// его обрезанным посередине события.</summary>
    public static string? Roll()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(CurrentPath)) return null;
                if (new FileInfo(CurrentPath).Length == 0) return null;

                var target = Path.Combine(Folder, AppLogRotation.RolledName(DateTime.Now));
                // Один архив в секунду — потолок имени. Совпадение возможно (две ротации подряд
                // при отправке), и перезаписать чужой архив нельзя: дописываем в него.
                if (File.Exists(target))
                {
                    File.AppendAllText(target, File.ReadAllText(CurrentPath, Encoding.UTF8), Utf8NoBom);
                    File.Delete(CurrentPath);
                }
                else File.Move(CurrentPath, target);
                return target;
            }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Архивы, от свежих к старым.</summary>
    public static IReadOnlyList<string> Archives()
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<string>();
            return Directory.EnumerateFiles(Folder, AppLogRotation.ArchivePattern)
                .Where(f => AppLogRotation.StampOf(f) is not null)
                .OrderByDescending(f => AppLogRotation.StampOf(f)!.Value)
                .ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>Удалить то, что переросло срок и число. Зовётся после каждой ротации и после
    /// отправки.</summary>
    public static void Prune()
    {
        try
        {
            foreach (var doomed in AppLogRotation.ToDelete(Archives(), DateTime.Now, KeepFiles, KeepDays))
                try { File.Delete(doomed); } catch (IOException) { /* занят — уберётся позже */ }
        }
        catch (Exception) { /* уборка журнала не повод падать */ }
    }

    /// <summary>Прочитать журнал целиком (текущий плюс архивы) — для страницы настроек и тестов.
    /// Зовётся редко и руками, поэтому читается прямо с диска, без кэша.</summary>
    public static IReadOnlyList<string> Tail(int lines = 200)
    {
        try
        {
            if (!File.Exists(CurrentPath)) return Array.Empty<string>();
            var all = File.ReadAllLines(CurrentPath);
            return all.Length <= lines ? all : all[^lines..];
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static void RollIfNeeded()
    {
        try
        {
            if (!File.Exists(CurrentPath)) return;
            if (!AppLogRotation.NeedsRoll(new FileInfo(CurrentPath).Length, MaxBytes)) return;

            // Тем же кодом, что и ротация перед отправкой: имя архива даётся с точностью до
            // секунды, и две ротации в одну секунду (мелкий потолок, быстрый цикл записи) обязаны
            // сложиться в ОДИН архив, а не потерять вторую — иначе текущий файл продолжил бы расти
            // как ни в чём не бывало, то есть ровно вопреки ротации.
            Roll();
            foreach (var doomed in AppLogRotation.ToDelete(Archives(), DateTime.Now, KeepFiles, KeepDays))
                try { File.Delete(doomed); } catch (IOException) { }
        }
        catch (Exception) { /* не вышло отложить — пишем дальше в текущий */ }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);
}
