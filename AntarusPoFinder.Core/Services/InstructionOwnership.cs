using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Чья инструкция лежит в папке «Инструкция» ВЕРСИИ.
///
/// <b>Откуда вопрос.</b> Прямое слово Ильи: «мы тут сталкивались с пропаданием прошивок, по новой
/// загружали и нумерация по новой начиналась. Вот чо с инструкцией в таком случае, старая не должна
/// же исчезать». Разберём, что при этом происходит на диске.
///
/// Версия опознаётся ПАПКОЙ: <c>ПО\&lt;тип&gt;\&lt;подтип&gt;\&lt;контроллер&gt;\&lt;версия&gt;\</c>, и имя
/// этой папки — сама строка версии (см. <see cref="VersionLayout"/>). Никакого скрытого
/// идентификатора у папки нет: номер начали считать заново — и новая, совершенно другая прошивка
/// получает РОВНО ТОТ ЖЕ путь, что носила прежняя. А внутри, в «Инструкция\», под каноническим именем
/// <c>инструкция_&lt;версия&gt;.pdf</c> (<see cref="InstructionNaming"/>) лежит документ прежней жизни
/// этого номера. Без вмешательства выходит худшее из двух зол сразу: новая загрузка либо затрёт
/// чужой документ, либо — и это тише и опаснее — не затрёт ничего, и карточка новой версии покажет
/// ЧУЖУЮ инструкцию как свою (она смотрит на файлы в папке, а не в базу), а QR с наклейки откроет её
/// же.
///
/// <b>Что делает этот класс.</b> Кладёт рядом с документом файл-метку <see cref="MarkerFileName"/> —
/// кто именно хозяин папки сейчас. Пометка позволяет при следующей загрузке в эту же папку ответить
/// на вопрос «документ в ней от ЭТОЙ версии или от прежней жизни номера» не гаданием.
///
/// <b>Правила, и все четыре — не молчащие:</b>
/// <list type="number">
/// <item><description>Папка не СОБСТВЕННАЯ папка версии (общая «Инструкция» контроллера у версий,
/// не переехавших на новую раскладку) — не трогаем вообще ничего. Там документ общий ПО ЗАМЫСЛУ, и
/// «прибрать чужое» означало бы унести инструкцию у всех версий контроллера разом.</description></item>
/// <item><description>Метка есть и называет ДРУГОГО хозяина — это прежняя жизнь номера. Документ
/// уезжает в «Прежние редакции» (<see cref="InstructionStorage.PreviousFolderName"/>), на его место
/// встанет заглушка «Инструкция в разработке», человеку говорится, что произошло.</description></item>
/// <item><description>Метки нет, но имя документа называет ДРУГУЮ версию — то же самое: документ
/// явно не от той прошивки, что сюда грузят.</description></item>
/// <item><description>Метки нет, документ назван этой же версией (или не назван никак) — НИЧЕГО не
/// трогаем: это с равной вероятностью обычная перезаливка той же версии, а стереть человеку
/// инструкцию из-за догадки нельзя. Выдаётся предупреждение, метка ставится — и следующая
/// перенумерация попадёт уже в пункт 2, где ответ точный.</description></item>
/// </list>
///
/// Обрати внимание: прежняя редакция НЕ УДАЛЯЕТСЯ никогда и никем — ни здесь, ни в
/// <see cref="InstructionStorage.ArchiveDocument"/>. Она переезжает в подпапку, остаётся на диске и
/// открывается руками; резолверы документов её не видят намеренно (см.
/// <see cref="InstructionDocResolver"/>), иначе архив начал бы показываться как действующая
/// инструкция.</summary>
public static class InstructionOwnership
{
    /// <summary>Файл-метка. Имя с точки и атрибут «скрытый»: в папке, которую наладчик открывает,
    /// чтобы взять инструкцию, служебному файлу мозолить глаза незачем. Расширения нет намеренно —
    /// ни один резолвер документов не примет его за документ.</summary>
    public const string MarkerFileName = ".instruction-owner";

    /// <summary>Это наша метка, а не документ. Спрашивают и резолверы, и
    /// <see cref="VersionLayout"/>: без этой проверки метка сделала бы пустую папку «Инструкция»
    /// версии НЕПУСТОЙ — и версия перестала бы показывать инструкцию контроллера, в точности как это
    /// было с <c>Thumbs.db</c> (см. <see cref="JunkFiles"/>).</summary>
    public static bool IsMarker(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return string.Equals(Path.GetFileName(path), MarkerFileName, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>Новый ключ хозяина. Случайный, а не «номер версии»: номер — ровно то, что в разбираемом
    /// случае повторяется, и ключом быть не может.</summary>
    public static string NewOwnerKey() => Guid.NewGuid().ToString("N");

    /// <summary>Что написано в метке. null — метки нет, она пуста или нечитаема (недоступная шара —
    /// это «не знаем», а не ошибка).</summary>
    public static InstructionOwner? Read(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var path = Path.Combine(folder, MarkerFileName);
            if (!File.Exists(path)) return null;

            string? key = null, version = null, stamped = null;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var split = line.IndexOf('=');
                if (split <= 0) continue;
                var name = line[..split].Trim();
                var value = line[(split + 1)..].Trim();
                if (string.Equals(name, "owner", StringComparison.OrdinalIgnoreCase)) key = value;
                else if (string.Equals(name, "version", StringComparison.OrdinalIgnoreCase)) version = value;
                else if (string.Equals(name, "stamped", StringComparison.OrdinalIgnoreCase)) stamped = value;
            }
            return string.IsNullOrWhiteSpace(key) ? null : new InstructionOwner(key!, version ?? "", stamped ?? "");
        }
        catch (Exception) { return null; }
    }

    /// <summary>Поставить метку. Не бросает: метка — подспорье следующей загрузке, и ронять из-за неё
    /// копирование инструкции незачем.</summary>
    public static void Mark(string? folder, string ownerKey, string versionRaw)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(ownerKey)) return;
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, MarkerFileName);
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);

            var text = new StringBuilder()
                .AppendLine("# Служебная метка Antarus ПО Finder. Отвечает на один вопрос: документ в этой")
                .AppendLine("# папке — от ЭТОЙ версии прошивки или остался от прежней, носившей тот же номер")
                .AppendLine("# (так бывает, когда прошивки потеряли и нумерацию начали заново).")
                .AppendLine("# Удалять не нужно; удалили — программа просто спросит человека вместо того,")
                .AppendLine("# чтобы ответить самой.")
                .Append("owner=").AppendLine(ownerKey)
                .Append("version=").AppendLine(versionRaw ?? "")
                .Append("stamped=").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .ToString();
            File.WriteAllText(path, text, Encoding.UTF8);
            File.SetAttributes(path, FileAttributes.Hidden);
        }
        catch (Exception) { /* метка необязательна, см. summary */ }
    }

    /// <summary>Папка — СОБСТВЕННАЯ папка документов этой версии, а не общая папка контроллера. Только
    /// в своей папке у версии есть право прибирать чужое (пункт 1 правил в summary). Вопрос задаётся
    /// <see cref="VersionLayout"/> — единственному месту, знающему раскладку на диске.</summary>
    public static bool IsOwnFolder(string? versionDir, string? folder)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || string.IsNullOrWhiteSpace(folder)) return false;
        return PathsEqual(folder!, VersionLayout.SlotFolder(versionDir!, HierarchyFolders.Instructions));
    }

    /// <summary>Загрузка ЗАВОДИТ версию в папке, которая на диске уже есть, а записи о такой версии в
    /// базе нет (<paramref name="folderOutlivedItsVersion"/>) — то есть номер пошёл по второму кругу.
    /// Разбираемся с документом, который остался в папке от прежней жизни номера, по правилам из
    /// summary.
    ///
    /// Зовётся ТОЛЬКО загрузкой новой версии. Обычная догрузка инструкции через модерацию сюда не
    /// ходит: там хозяин папки не меняется, а прежняя редакция и так переезжает в архив при
    /// перезаписи (<see cref="InstructionStorage.ArchiveDocument"/>).</summary>
    public static InstructionClaim Claim(string? versionDir, string? folder, string versionRaw,
        bool folderOutlivedItsVersion, List<string> warnings)
    {
        var empty = new InstructionClaim(null, null, null);
        if (!IsOwnFolder(versionDir, folder)) return empty;

        var ownerKey = NewOwnerKey();
        var marker = Read(folder);
        var document = DocumentIn(folder);

        if (document is null)
        {
            Mark(folder, ownerKey, versionRaw);
            return empty;
        }

        var documentVersion = InstructionNaming.VersionFromFileName(document);
        var foreignByMarker = marker is not null && !string.IsNullOrWhiteSpace(marker.VersionRaw)
                              && !string.Equals(marker.VersionRaw, versionRaw, StringComparison.OrdinalIgnoreCase);
        var foreignByName = marker is null && !string.IsNullOrWhiteSpace(documentVersion)
                            && !string.Equals(documentVersion, versionRaw, StringComparison.OrdinalIgnoreCase);

        // Тот же номер и ничего, что доказывало бы чужое происхождение. Это с равной вероятностью
        // обычная перезаливка той же версии — трогать документ нельзя, но и промолчать нельзя:
        // «инструкция оказалась от прежней прошивки» человек заметит на шкафу, а не здесь.
        if (!foreignByMarker && !foreignByName)
        {
            if (folderOutlivedItsVersion)
                warnings.Add($"В папке «{HierarchyFolders.Instructions}» уже лежал документ " +
                             $"«{Path.GetFileName(document)}», а записи об этой версии в базе не было. " +
                             "Он оставлен на месте и будет показан как инструкция этой версии — если он от " +
                             "прежней прошивки с тем же номером, замените его через «Редактировать».");
            Mark(folder, ownerKey, versionRaw);
            return empty;
        }

        var archived = InstructionStorage.ArchiveDocument(document);
        Mark(folder, ownerKey, versionRaw);
        if (archived is null)
        {
            warnings.Add($"Инструкция «{Path.GetFileName(document)}» осталась от прежней прошивки с этим же " +
                         "номером, но убрать её в архив не вышло. Проверьте папку руками.");
            return empty;
        }

        var whose = marker is { VersionRaw.Length: > 0 } ? marker.VersionRaw : documentVersion;
        warnings.Add($"Инструкция от прежней версии {whose} убрана в «{InstructionStorage.PreviousFolderName}» — " +
                     "она не пропала, но к этой прошивке отношения не имеет. " +
                     "Положите инструкцию этой версии через «Редактировать».");
        return new InstructionClaim(document, archived, whose);
    }

    /// <summary>Документ в папке — настоящий, не заглушка, не ярлык, не метка и не мусор. Смотрим
    /// только верхний уровень: архив прежних редакций лежит подпапкой, и считать его документом
    /// нельзя ни здесь, ни где-либо ещё.</summary>
    private static string? DocumentIn(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !DocFileResolver.IsNotADocument(f) && !IsMarker(f));
        }
        catch (Exception) { return null; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>Кто хозяин папки «Инструкция» версии. <paramref name="Key"/> — случайный ключ жизни
/// номера, <paramref name="VersionRaw"/> — строка версии, которой папка принадлежала,
/// <paramref name="Stamped"/> — когда пометили (только для человека, читающего файл глазами).</summary>
public sealed record InstructionOwner(string Key, string VersionRaw, string Stamped);

/// <summary>Что сделали с документом прежней жизни номера. Все поля null — ничего не делали (и это
/// обычный исход).</summary>
public sealed record InstructionClaim(string? ArchivedFrom, string? ArchivedTo, string? PreviousVersionRaw)
{
    public bool Archived => ArchivedTo is not null;
}
