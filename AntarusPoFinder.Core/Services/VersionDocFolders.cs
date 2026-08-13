using System.IO;

namespace AntarusPoFinder.Core.Services;

/// <summary>Где лежат документы (инструкция, карты, HMI) КОНКРЕТНОЙ записи версии — с поправкой на
/// записи-ссылки.
///
/// <b>Что такое запись-ссылка.</b> Одна и та же прошивка может быть привязана к нескольким подтипам
/// шкафа (<see cref="FirmwareSubtypeLinkService"/>): файлы на диске лежат ОДИН раз, в папке основного
/// подтипа, а каждому дополнительному заводится своя запись <c>fw_versions</c> с ТЕМ ЖЕ
/// <c>disk_path</c> плюс ярлык в его папке контроллера. Для прошивки это правильно — файл
/// действительно один. Для документов — нет: руководство по эксплуатации пишется на ШКАФ, а «ПЖ 2.0» и
/// «ПЖ FD» — разные шкафы.
///
/// <b>Что было сломано.</b> Читающая сторона (карточка, «Хранилище», удаление инструкции) считала папку
/// документа от <c>disk_path</c> — то есть от папки ЧУЖОГО подтипа, — а пишущая
/// (<see cref="FirmwareAttachmentsService"/>) от собственных имён иерархии записи. У обычной версии это
/// одна и та же папка, и разницы никто не видел. У записи-ссылки они расходились, и получалось ровно
/// то, на что была жалоба: «у меня есть ПЖ FD SMH5, а там вместо прошивки ссылка на ПЖ 2.0 SMH5,
/// и инструкция туда прицепилась некорректно». Приложенная к «ПЖ FD» инструкция ложилась в папку
/// «ПЖ\FD\SMH5\Инструкция», а карточка, QR-код и адрес на хостинге продолжали показывать документ из
/// «ПЖ\2.0\SMH5» — то есть чужой. У перестроенной версии выходило иначе, но не лучше: документ уезжал
/// внутрь общей папки версии и становился общим на оба шкафа.
///
/// <b>Правило.</b> Читаем и пишем ОДНО И ТО ЖЕ место. У записи-ссылки это папка документа её
/// СОБСТВЕННОГО контроллера; у всех остальных ничего не меняется — работает прежняя развилка
/// <see cref="VersionLayout"/> «своя папка перестроенной версии / общая папка контроллера».
///
/// Чтение при этом с запасным вариантом: своя папка выбирается, только если в ней ЕСТЬ файлы, иначе
/// читаем оттуда же, откуда читали раньше. Поэтому обновление ничего не прячет — у тех, кто уже живёт с
/// общим документом, он и остаётся общим, — а как только у подтипа появляется свой документ, он
/// начинает выигрывать, и адрес на хостинге у него становится собственным.</summary>
public static class VersionDocFolders
{
    /// <summary>Папка контроллера ЭТОЙ записи, построенная по её именам иерархии, а не по пути на
    /// диске. Пусто на входе — null: гадать не о чем, вызывающий останется с прежним поведением.
    ///
    /// Собирается ровно так же, как её собирает пишущая сторона (см. FirmwareAttachmentsService), — от
    /// этого зависит весь смысл класса: разойдись эти две сборки, чтение снова смотрело бы не туда,
    /// куда пишет запись.</summary>
    public static string? OwnControllerFolder(string? root, string? groupName, string? subtypeName,
        string? controllerName)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(groupName)
            || string.IsNullOrWhiteSpace(subtypeName) || string.IsNullOrWhiteSpace(controllerName))
            return null;

        try
        {
            return Path.Combine(HierarchyService.GroupSubFolder(root, groupName, subtypeName), controllerName);
        }
        catch (Exception) { return null; }
    }

    /// <summary>То же самое, когда корня диска под рукой нет, а есть путь папки версии: корень
    /// вычисляется из него по опорной папке «ПО» — тем же приёмом, каким
    /// <see cref="FirmwarePathLocalizer"/> переносит чужой путь на нашу форму диска. Нужно карточке
    /// поиска: она ходит по документам из статических методов фонового обхода, где корня нет, а лишний
    /// поход в настройки на каждую карточку — это тот самый «фон, от которого программа задумывается».
    ///
    /// Опорной папки в пути нет (путь вообще не из иерархии) — null: выдумывать корень нельзя.</summary>
    public static string? OwnControllerFolderNear(string? versionDir, string? groupName, string? subtypeName,
        string? controllerName) =>
        RootOf(versionDir) is { } root ? OwnControllerFolder(root, groupName, subtypeName, controllerName) : null;

    private static string? RootOf(string? versionDir)
    {
        if (string.IsNullOrWhiteSpace(versionDir)) return null;

        var parts = versionDir.Split('\\', '/');
        var anchor = Array.FindIndex(parts, p => string.Equals(p, HierarchyService.FolderPo, StringComparison.OrdinalIgnoreCase));
        if (anchor <= 0) return null;

        // Префикс отрезаем от ИСХОДНОЙ строки, а не собираем из кусков: у сетевого пути
        // (\\сервер\шара\ПО\…) первые два куска пустые, и склейка потеряла бы двойную косую черту.
        // Разделитель на конце оставляем намеренно: Path.Combine("Z:", "ПО") даёт «Z:ПО» — путь
        // относительно текущей папки диска, а не его корня.
        var length = anchor; // разделители между кусками
        for (var i = 0; i < anchor; i++) length += parts[i].Length;
        return versionDir[..length];
    }

    /// <summary>Запись указывает на файлы ЧУЖОГО подтипа — то есть это привязка прошивки к
    /// дополнительному подтипу шкафа.
    ///
    /// Признаков два, и второй здесь не перестраховка. «Папка версии лежит не в папке контроллера этой
    /// записи» — само по себе слишком широкое условие: так же выглядит и ОПЦ прежней раскладки
    /// (<c>&lt;подтип&gt;\ОПЦ\&lt;версия&gt;</c>, папки контроллера над ней нет вовсе), и версия,
    /// чью папку на диске переименовали мимо программы, и вообще любой путь, ведущий не туда, куда
    /// программа ожидает. Переложить документы ТАКОЙ версии в другое место — это не починка, а вторая
    /// поломка вдобавок к первой.
    ///
    /// Поэтому второй признак: файлы лежат у ТОГО ЖЕ контроллера, только в папке другого подтипа. Это
    /// точная примета копии — она заводится с тем же <c>controller_id</c> и тем же <c>disk_path</c>
    /// (см. <see cref="FirmwareSubtypeLinkService.LinkExtras"/>), меняется у неё ровно подтип. У ОПЦ
    /// прежней раскладки папки контроллера над версией нет, у переехавшей папки имя контроллера
    /// другое — и обе остаются с прежним поведением.
    ///
    /// Неизвестна хотя бы одна из папок — тоже «запись обычная»: по умолчанию поведение должно быть
    /// прежним, а не «на всякий случай новым».</summary>
    public static bool IsLinkedCopy(string? versionDir, string? ownControllerFolder)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || string.IsNullOrWhiteSpace(ownControllerFolder))
            return false;

        try
        {
            var own = Path.GetFullPath(ownControllerFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(versionDir).StartsWith(own, StringComparison.OrdinalIgnoreCase)) return false;

            var actual = VersionLayout.ControllerFolderOf(versionDir);
            if (string.IsNullOrWhiteSpace(actual)) return false;

            return string.Equals(FolderNameOf(actual), FolderNameOf(ownControllerFolder),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Путь с недопустимыми знаками — не повод менять раскладку: ведём себя как раньше.
            return false;
        }
    }

    private static string FolderNameOf(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Куда ПИСАТЬ документ этой записи.
    ///
    /// У записи-ссылки — в папку документа её собственного контроллера, и намеренно НЕ внутрь папки
    /// версии: своей папки версии у такой записи нет вовсе (на её месте лежит ярлык), а класть документ
    /// в чужую папку — это и есть та самая поломка. Значит документ у неё общий на контроллер, как у
    /// всех неперестроенных версий; точнее без собственной папки версии всё равно не выйдет.</summary>
    public static string WriteFolder(string? versionDir, string ownControllerFolder, string slot) =>
        IsLinkedCopy(versionDir, ownControllerFolder)
            ? Path.Combine(ownControllerFolder, slot)
            : VersionLayout.SlotWriteFolder(versionDir, ownControllerFolder, slot);

    /// <summary>Откуда ЧИТАТЬ документ этой записи. У обычной версии — в точности прежний ответ
    /// <see cref="VersionLayout.SlotBestReadFolder"/>; у записи-ссылки сначала её собственная папка и
    /// только если в ней ничего нет — прежнее место.
    ///
    /// «Ничего нет» здесь строже, чем у <see cref="VersionLayout.HasFiles"/>: заглушка
    /// «Инструкция в разработке» своей папке победы не даёт. Она заводится сама, стоит появиться папке
    /// документа, — и, считайся она документом, у подтипа, живущего с общим руководством, оно назавтра
    /// сменилось бы на «в разработке». Обещание «обновление ничего не прячет» этого не переживёт.
    /// Своя папка выигрывает, когда в ней лежит НАСТОЯЩИЙ документ, — и тогда же адрес на хостинге
    /// становится собственным.</summary>
    public static string? BestReadFolder(string? versionDir, string? ownControllerFolder, string slot)
    {
        var asBefore = VersionLayout.SlotBestReadFolder(versionDir, VersionLayout.ControllerFolderOf(versionDir), slot);
        if (!IsLinkedCopy(versionDir, ownControllerFolder)) return asBefore;

        var own = Path.Combine(ownControllerFolder!, slot);
        if (InstructionStub.HasRealInstruction(own)) return own;

        // Своего документа ещё нет — читаем оттуда же, откуда читали всегда. Собственная папка
        // возвращается лишь тогда, когда прежнего места не существует вовсе: пусть путь ведёт туда,
        // куда эта запись пишет, — там и появится заглушка «Инструкция в разработке».
        return asBefore ?? own;
    }
}
