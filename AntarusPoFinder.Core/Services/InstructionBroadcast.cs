using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Версия шкафа, которой можно отдать один и тот же документ-инструкцию.</summary>
/// <param name="Version">Сама запись версии.</param>
/// <param name="Label">Как версия называется для человека — то, что он увидит в списке с галочками.</param>
/// <param name="IsSelf">Это та версия, из карточки которой открыли редактор.</param>
/// <param name="IsEarlier">Версия СТАРШЕ выбранной по возрасту, то есть «более ранняя».</param>
/// <param name="HasInstruction">У версии уже есть своя инструкция — галочка на ней означает «заменить».</param>
public sealed record InstructionTarget(FwVersionRecord Version, string Label, bool IsSelf, bool IsEarlier,
    bool HasInstruction);

/// <summary>Итог раздачи. <paramref name="Applied"/> — подписи версий, которым документ лёг;
/// <paramref name="Replaced"/> — из них те, у кого инструкция была и уехала в «Прежние редакции».</summary>
public sealed record InstructionBroadcastResult(List<string> Applied, List<string> Replaced, List<string> Warnings)
{
    public bool AnythingChanged => Applied.Count > 0;
}

/// <summary>Один документ-инструкция — СРАЗУ НЕСКОЛЬКИМ версиям прошивки.
///
/// <b>Откуда это.</b> Прямое слово Ильи: «по инструкции надо проверить, чтобы можно было старые
/// заменить. Вдруг ошибку нашли, а QR ведёт на старую с ошибкой и нужно перезалить новую без
/// ошибки» — и следом: «мб галочки применить инструкцию ко всем прошивкам более ранней версии».
/// Документ обычно описывает ШКАФ, а не конкретную сборку программы, и исправленная опечатка
/// касается всех версий сразу; до сих пор же инструкцию приходилось прикладывать к каждой версии
/// руками, по одной, открывая модерацию столько раз, сколько версий у шкафа.
///
/// <b>Что здесь важно не потерять.</b>
/// <list type="number">
/// <item><description>Документ КЛАДЁТСЯ КАЖДОЙ версии отдельно, в её собственную папку и под её
/// собственным каноническим именем (<see cref="InstructionNaming"/>). Не ярлык, не общий файл: у
/// каждой версии свой постоянный адрес на хостинге, и наклеенный на шкаф QR обязан вести именно по
/// нему. Дорого по месту, зато ссылка не зависит от того, что потом случится с соседней
/// версией.</description></item>
/// <item><description>Прежняя инструкция версии НЕ ИСЧЕЗАЕТ — она уезжает в «Прежние редакции»
/// (<see cref="InstructionStorage.ArchiveDocument"/>). Это второе требование Ильи дословно: «если
/// изменения были именно в прошивке относительно предыдущей версии и инструкция менялась, то надо
/// сохранить старую».</description></item>
/// <item><description>Выкладка на хостинг идёт тем же путём, что у одиночной загрузки
/// (<see cref="IInstructionPublisher"/>), поэтому по старому QR открывается НОВЫЙ документ: адрес
/// строится из пути на диске, а путь у версии не менялся.</description></item>
/// <item><description>Ничего не решается за человека. Набор версий приходит снаружи готовым
/// списком — «применить ко всем более ранним» это ГАЛОЧКА в окне, и по умолчанию она снята: тихо
/// переписать десяток версий одним кликом опаснее, чем лишний раз щёлкнуть.</description></item>
/// </list></summary>
public static class InstructionBroadcast
{
    /// <summary>Все версии ЭТОГО ЖЕ шкафа (подтип + контроллер), которым документ можно отдать, —
    /// от свежих к старым, тем же порядком, каким их показывает история версий. Конфигурации и
    /// удалённые сюда не попадают: у конфигурации файлы общие с её прошивкой (см.
    /// <see cref="Database.GetFwVersionConfigs"/>), и отдельной инструкции у неё быть не может.
    ///
    /// Архивные ВКЛЮЧЕНЫ намеренно: исправленная инструкция нужна ровно тем версиям, что уже стоят
    /// в шкафах у заказчиков, а туда попадают и те, что у нас давно убраны из выдачи.</summary>
    public static List<InstructionTarget> Targets(Database db, FwVersionRecord record, string root)
    {
        var result = new List<InstructionTarget>();
        if (record.Id is not int selfId) return result;

        foreach (var version in db.GetFwVersionsHistory(record.SubtypeId, record.ControllerId, includeArchived: true))
        {
            if (version.Id is not int id) continue;
            var isSelf = id == selfId;
            result.Add(new InstructionTarget(version, Label(version), isSelf,
                IsEarlier: !isSelf && Order(version).CompareTo(Order(record)) < 0,
                HasInstruction: HasInstruction(version, root)));
        }
        return result;
    }

    /// <summary>Возраст версии одним числом — «более ранняя» это меньшая пара (hw, sw). Сравнивать
    /// строку версии нельзя: «2.1.0004.0010» и «2.1.0004.0002» сравниваются как текст неправильно, а
    /// дата загрузки у перезалитой версии врёт (см. FwHistoryStatus — там же правило «текущая =
    /// максимальный sw»).</summary>
    private static (int Hw, int Sw) Order(FwVersionRecord v) => (v.HwVersion, v.SwVersion);

    private static string Label(FwVersionRecord v)
    {
        var label = v.VersionRaw;
        if (!string.IsNullOrWhiteSpace(v.Execution)) label += $" ({v.Execution})";
        if (v.Archived) label += " — в архиве";
        return label;
    }

    /// <summary>У версии уже лежит инструкция. Спрашиваем диск, а не базу: карточка и QR смотрят
    /// именно на файлы (см. <see cref="InstructionStub"/>), и «в базе пусто, а в папке документ» —
    /// обычное состояние после ручной укладки мимо программы.</summary>
    private static bool HasInstruction(FwVersionRecord v, string root)
    {
        var versionDir = FirmwarePathLocalizer.Localize(v.DiskPath, root);
        var folder = VersionLayout.SlotBestReadFolder(versionDir, VersionLayout.ControllerFolderOf(versionDir),
            HierarchyFolders.Instructions);
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Any(f => !DocFileResolver.IsNotADocument(f));
        }
        catch (Exception) { return false; }
    }

    /// <summary>Положить документ выбранным версиям. Ходит на диск и на хостинг — звать из фонового
    /// потока, как и обычную догрузку вложений (см. EditFirmwareDialog.Save_Click).
    ///
    /// Каждая версия обрабатывается независимо: упавшая на одной шара не отменяет остальных, причина
    /// ложится в предупреждения. Половина разданного документа — состояние неприятное, но честное и
    /// повторяемое (повторный запуск доложит оставшимся), а прерывание на середине оставило бы
    /// человека гадать, кому досталось.</summary>
    public static InstructionBroadcastResult Apply(Database db, string root, string sourcePath,
        IReadOnlyList<FwVersionRecord> targets, IInstructionPublisher? publisher = null)
    {
        var applied = new List<string>();
        var replaced = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(sourcePath) || (!File.Exists(sourcePath) && !Directory.Exists(sourcePath)))
        {
            warnings.Add($"Инструкция не найдена: {sourcePath}");
            return new InstructionBroadcastResult(applied, replaced, warnings);
        }

        foreach (var version in targets)
        {
            if (version.Id is not int id) continue;
            try
            {
                var versionDir = FirmwarePathLocalizer.Localize(version.DiskPath, root);
                var names = db.GetFwVersionNames(id);
                if (names is not { } n)
                {
                    warnings.Add($"{Label(version)}: не нашли, где лежит шкаф этой версии — пропущена.");
                    continue;
                }

                var ownControllerFolder = VersionDocFolders.OwnControllerFolder(root, n.GroupName, n.SubtypeName,
                    n.ControllerName);
                if (string.IsNullOrWhiteSpace(ownControllerFolder))
                {
                    warnings.Add($"{Label(version)}: не нашли папку контроллера — пропущена.");
                    continue;
                }

                // Куда писать документ, решает ровно одно место — раскладка версии. У перестроенной
                // версии это её собственная папка «Инструкция», у прежней — общая папка контроллера
                // (см. VersionDocFolders.WriteFolder и VersionLayout.SlotWriteFolder).
                var folder = VersionDocFolders.WriteFolder(versionDir, ownControllerFolder!,
                    HierarchyFolders.Instructions);
                var had = HasInstruction(version, root);

                var placed = InstructionStorage.Copy(sourcePath, folder, root, warnings, version.VersionRaw,
                    publisher);
                if (string.IsNullOrEmpty(placed.StoredPath))
                {
                    warnings.Add($"{Label(version)}: документ не лёг — пропущена.");
                    continue;
                }

                // Ссылку получают и записи, делящие с версией файлы: копии под другие подтипы шкафа и
                // конфигурации (см. Database.GetFwVersionsSharingFiles). Документ у них физически один
                // и тот же, и разъехавшиеся ссылки означали бы «у конфигурации инструкции нет».
                foreach (var sharing in SharingIds(db, version, id))
                    db.UpdateFwVersionAttachments(sharing, instructionsPath: placed.StoredPath);
                version.InstructionsPath = placed.StoredPath;

                applied.Add(Label(version));
                if (had) replaced.Add(Label(version));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Label(version)}: {ex.Message}");
            }
        }

        return new InstructionBroadcastResult(applied, replaced, warnings);
    }

    private static IEnumerable<int> SharingIds(Database db, FwVersionRecord version, int id)
    {
        var ids = new List<int> { id };
        foreach (var sharing in db.GetFwVersionsSharingFiles(version.DiskPath, version.VersionRaw))
            if (sharing.Id is int sid && !ids.Contains(sid)) ids.Add(sid);
        return ids;
    }
}
