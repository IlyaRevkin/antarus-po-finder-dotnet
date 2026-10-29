using System.Collections.Generic;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Core.Data;

/// <summary>Таблица QR-кодов прошивок: по строке на версию — что зашито в её код и от каких входных
/// данных это посчитано. Почему хранится ссылка с отпечатком, а не готовая картинка, и почему
/// перерисовка ленивая, а не «по событиям», разобрано в <see cref="FirmwareQrCodes"/>.
///
/// <b>Таблица строго локальная, в общий конфиг не уезжает.</b> Она целиком выводится из того, что и
/// так синхронизируется (instructions_path версии, базовый адрес, корень диска, справочник
/// написаний), — то есть везти её по сети значило бы возить производное от уже увезённого, да ещё и
/// завязанное на корень диска, который у каждой машины свой. Приехав с чужой машины, такая строка
/// ломала бы ровно то, ради чего таблица заведена: в коде оказался бы чужой путь.
///
/// <b>Жизненный цикл записи — «в соответствии с логикой действия»:</b>
/// <list type="bullet">
/// <item><description><b>добавление</b> — вышла новая прошивка: ближайший проход
/// <see cref="FirmwareQrSweep.Run"/> видит версию без строки и заводит её;</description></item>
/// <item><description><b>обновление</b> — поменялась ссылка (перезалили инструкцию, привязали
/// документ, правили адрес диска или справочник написаний): отпечаток разошёлся, строка
/// перестраивается;</description></item>
/// <item><description><b>удаление</b> — версию откатили, архивировали или отключили: она выпадает из
/// списка «живых», и её строка убирается тем же проходом. Версию удалили из базы совсем — строку
/// уносит ON DELETE CASCADE, не дожидаясь прохода.</description></item>
/// </list></summary>
public partial class Database
{
    /// <summary>Сохранённый код одной версии.</summary>
    public sealed record FwQrCode(
        int FwVersionId,
        string SyncId,
        string Payload,
        string Kind,
        string Hole,
        string SourcePath,
        string Fingerprint,
        string BuiltAt)
    {
        public FirmwareQrCodes.PayloadKind PayloadKind => FirmwareQrCodes.KindFrom(Kind);

        /// <summary>Годится ли сохранённое при сегодняшних входных данных. Пустой отпечаток
        /// сравнения не проходит никогда: это строка, записанная до появления отпечатков, и верить
        /// ей на слово нельзя — проще перестроить.</summary>
        public bool MatchesFingerprint(string fingerprint) =>
            Fingerprint.Length > 0 && string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal);
    }

    /// <summary>Версия прошивки глазами построителя QR: всё, из чего считается ссылка, и ничего
    /// больше. Берётся одним запросом — страниц с кодами может быть сколько угодно, а ходить в базу
    /// на каждую версию отдельно было бы квадратично (та же причина, что у GetHostingChecks).</summary>
    public sealed record FwQrSubject(int FwVersionId, string SyncId, string InstructionPath);

    /// <summary>Живые версии, которым положен свой QR: не архивные и не откаченные. Именно этот
    /// список и задаёт, каким строкам в таблице место, — всё, чего в нём нет, из таблицы уходит.
    ///
    /// Условие по status повторяет то, которым пользуется выдача поиска (status IS NULL OR
    /// status='active'): откаченная версия с наклейки больше не ведётся, и держать её код значило бы
    /// предлагать напечатать наклейку на снятую прошивку.</summary>
    public List<FwQrSubject> GetFwQrSubjects()
    {
        var result = new List<FwQrSubject>();
        using var reader = ExecuteReader("""
            SELECT id, sync_id, instructions_path
            FROM fw_versions
            WHERE archived = 0 AND (status IS NULL OR status = 'active')
            """);
        while (reader.Read())
            result.Add(new FwQrSubject(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    /// <summary>Сохранённый код одной версии. null — про эту версию мы ещё ничего не считали.</summary>
    public FwQrCode? GetFwQrCode(int fwVersionId)
    {
        if (fwVersionId <= 0) return null;
        using var reader = ExecuteReader("""
            SELECT fw_version_id, sync_id, payload, kind, hole, source_path, fingerprint, built_at
            FROM fw_qr_codes WHERE fw_version_id = @id
            """, cmd => cmd.Parameters.AddWithValue("@id", fwVersionId));
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Все сохранённые коды разом — для прохода синхронизации и для диагностики
    /// («сколько версий уже посчитано»).</summary>
    public Dictionary<int, FwQrCode> GetFwQrCodes()
    {
        var result = new Dictionary<int, FwQrCode>();
        using var reader = ExecuteReader("""
            SELECT fw_version_id, sync_id, payload, kind, hole, source_path, fingerprint, built_at
            FROM fw_qr_codes
            """);
        while (reader.Read())
        {
            var row = Read(reader);
            result[row.FwVersionId] = row;
        }
        return result;
    }

    private static FwQrCode Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7));

    /// <summary>Завести или перестроить код версии. Одним INSERT … ON CONFLICT: «есть ли уже строка»
    /// — вопрос, на который база отвечает сама, а проверка в коде оставляла бы зазор между SELECT и
    /// INSERT (проход синхронизации и открытое окно наклейки пишут сюда независимо).</summary>
    public void SaveFwQrCode(int fwVersionId, string syncId, string payload, string kind, string hole,
        string sourcePath, string fingerprint)
    {
        if (fwVersionId <= 0) return;

        ExecuteNonQuery("""
            INSERT INTO fw_qr_codes(fw_version_id, sync_id, payload, kind, hole, source_path,
                                    fingerprint, built_at)
            VALUES(@id, @sync, @payload, @kind, @hole, @src, @fp, @at)
            ON CONFLICT(fw_version_id) DO UPDATE SET
                sync_id = @sync, payload = @payload, kind = @kind, hole = @hole,
                source_path = @src, fingerprint = @fp, built_at = @at
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@id", fwVersionId);
            cmd.Parameters.AddWithValue("@sync", syncId ?? "");
            cmd.Parameters.AddWithValue("@payload", payload ?? "");
            cmd.Parameters.AddWithValue("@kind", kind ?? "");
            cmd.Parameters.AddWithValue("@hole", hole ?? "");
            cmd.Parameters.AddWithValue("@src", sourcePath ?? "");
            cmd.Parameters.AddWithValue("@fp", fingerprint ?? "");
            cmd.Parameters.AddWithValue("@at", NowIso());
        });
    }

    /// <summary>Версии, у которых код УЖЕ посчитан и непустой. Для выдачи поиска: она рисует десятки
    /// карточек за раз и решает по каждой, показывать ли кнопку наклейки, — спрашивать базу на
    /// каждую было бы квадратично (та же причина, что у GetHostingChecks).</summary>
    public HashSet<int> GetFwQrCoveredIds()
    {
        var result = new HashSet<int>();
        using var reader = ExecuteReader("SELECT fw_version_id FROM fw_qr_codes WHERE payload <> ''");
        while (reader.Read()) result.Add(reader.GetInt32(0));
        return result;
    }

    public void DeleteFwQrCode(int fwVersionId) =>
        ExecuteNonQuery("DELETE FROM fw_qr_codes WHERE fw_version_id = @id",
            cmd => cmd.Parameters.AddWithValue("@id", fwVersionId));

    /// <summary>Убрать коды версий, которым они больше не положены — откаченных, архивных и тех,
    /// чьей записи в базе уже нет. Возврат — сколько строк унесено (для журнала прохода).
    ///
    /// Отдельным запросом, а не перебором в коде: условие «кому положено» одно и то же и здесь, и в
    /// <see cref="GetFwQrSubjects"/>, и разъехаться им нельзя.</summary>
    public int PruneFwQrCodes() => ExecuteNonQuery("""
        DELETE FROM fw_qr_codes WHERE fw_version_id NOT IN (
            SELECT id FROM fw_versions WHERE archived = 0 AND (status IS NULL OR status = 'active')
        )
        """);
}
