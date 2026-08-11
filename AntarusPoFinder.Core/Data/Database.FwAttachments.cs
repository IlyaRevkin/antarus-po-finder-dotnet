using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    /// <summary>Вид доп. материала — такой же плоский список-справочник, как производители ПЧ/УПП и
    /// теги, и синхронизируется тем же LWW-механизмом (см. Database.FlatLists.cs): удаление и возврат
    /// вида — события с отметкой времени, а не вывод из отсутствия в чужом списке.</summary>
    public const string FlatKindAttachmentKind = "attachment_kind";

    // ── Справочник видов ──────────────────────────────────────────────────────

    public List<string> GetFwAttachmentKinds()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT name FROM fw_attachment_kinds ORDER BY sort_order, name");
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Добавить вид (или подтвердить, что он живой). Отметка в flat_list_state — чтобы
    /// заведённый здесь вид не стёрло импортом конфига с машины, которая о нём ещё не знает.
    ///
    /// <b>Регистр сворачиваем в .NET, а не полагаемся на COLLATE NOCASE.</b> В SQLite NOCASE сворачивает
    /// ТОЛЬКО латиницу, а виды здесь кириллические: «Прочее» и «прочее» стали бы двумя разными
    /// строками таблицы — и первый же словарь по именам с StringComparer.OrdinalIgnoreCase
    /// (ImportFlatList в Database.ConfigExchange.cs строит именно такой) упал бы на дубликате ключа.
    /// Поэтому имя, уже известное списку в другом регистре, здесь не заводится второй раз — берётся
    /// существующее написание.</summary>
    public void AddFwAttachmentKind(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;

        var existing = GetFwAttachmentKinds().FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            MarkFlatListAlive(FlatKindAttachmentKind, existing);
            return;
        }

        var order = Convert.ToInt32(ExecuteScalar("SELECT COALESCE(MAX(sort_order), 0) + 1 FROM fw_attachment_kinds") ?? 1);
        ExecuteNonQuery("INSERT OR IGNORE INTO fw_attachment_kinds(name, sort_order) VALUES(@n, @s)", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@s", order);
        });
        MarkFlatListAlive(FlatKindAttachmentKind, name);
    }

    /// <summary>Убрать вид из справочника. Уже приложенные файлы этот вид СОХРАНЯЮТ (в отличие от
    /// удаления тега, которое вычищает тег и из самих записей): вид описывает, что это за файл, и
    /// потерять эту подпись из-за чистки справочника значило бы обесценить сам файл. Ровно как у
    /// производителей ПЧ/УПП (param_files.manufacturer).</summary>
    public void DeleteFwAttachmentKind(string name)
    {
        name = (name ?? "").Trim();
        ExecuteNonQuery("DELETE FROM fw_attachment_kinds WHERE name=@n COLLATE NOCASE",
            cmd => cmd.Parameters.AddWithValue("@n", name));
        MarkFlatListDeleted(FlatKindAttachmentKind, name);
    }

    /// <summary>Стартовый набор видов (см. FwAttachmentKinds.Defaults). Разовый флаг, а не «сеем,
    /// пока таблица пуста»: сид применяется только к НОВОЙ базе, а таблица появляется и у давно
    /// установленных копий — им набор нужен ровно один раз. Если кто-то потом осознанно удалит вид,
    /// миграция его не воскресит (тот же приём, что у AddNewDefaultManufacturersOnce).</summary>
    private void SeedFwAttachmentKindsOnce()
    {
        const string doneFlag = "migration_fw_attachment_kinds_seeded";
        if (GetSetting(doneFlag) == "true") return;

        foreach (var kind in FwAttachmentKinds.Defaults)
            AddFwAttachmentKind(kind);
        SetSetting(doneFlag, "true");
    }

    // ── Сами вложения ─────────────────────────────────────────────────────────

    private static FwAttachment ReadFwAttachment(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = GetInt(r, "id"),
        FwVersionId = GetInt(r, "fw_version_id"),
        Filename = GetString(r, "filename"),
        DiskPath = GetString(r, "disk_path"),
        Kind = GetString(r, "kind"),
        Comment = GetString(r, "comment"),
        AddedBy = GetString(r, "added_by"),
        AddedAt = GetString(r, "added_at"),
        DeletedAt = GetString(r, "deleted_at"),
        SyncId = GetString(r, "sync_id"),
        UpdatedAt = GetString(r, "updated_at"),
    };

    private const string FwAttachmentColumns =
        "id, fw_version_id, filename, disk_path, kind, comment, added_by, added_at, deleted_at, sync_id, updated_at";

    /// <summary>Живые вложения одной версии, в порядке добавления.</summary>
    public List<FwAttachment> GetFwAttachments(int fwVersionId)
    {
        var result = new List<FwAttachment>();
        using var reader = ExecuteReader(
            $"SELECT {FwAttachmentColumns} FROM fw_attachments WHERE fw_version_id=@id AND deleted_at='' ORDER BY id",
            cmd => cmd.Parameters.AddWithValue("@id", fwVersionId));
        while (reader.Read())
            result.Add(ReadFwAttachment(reader));
        return result;
    }

    /// <summary>Сколько живых вложений у версии — карточке выдачи нужен только счётчик, тянуть ради
    /// значка на карточке весь список по каждой строке выдачи незачем.</summary>
    public int CountFwAttachments(int fwVersionId) =>
        Convert.ToInt32(ExecuteScalar("SELECT COUNT(*) FROM fw_attachments WHERE fw_version_id=@id AND deleted_at=''",
            cmd => cmd.Parameters.AddWithValue("@id", fwVersionId)) ?? 0);

    public FwAttachment? GetFwAttachment(int id)
    {
        using var reader = ExecuteReader($"SELECT {FwAttachmentColumns} FROM fw_attachments WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", id));
        return reader.Read() ? ReadFwAttachment(reader) : null;
    }

    internal FwAttachment? FindFwAttachmentBySyncId(string? syncId)
    {
        if (string.IsNullOrWhiteSpace(syncId)) return null;
        using var reader = ExecuteReader($"SELECT {FwAttachmentColumns} FROM fw_attachments WHERE sync_id=@sy",
            cmd => cmd.Parameters.AddWithValue("@sy", syncId));
        return reader.Read() ? ReadFwAttachment(reader) : null;
    }

    /// <summary>Заводит вложение. sync_id/updated_at проставляются ПРЯМО ЗДЕСЬ, а не откладываются до
    /// следующего старта приложения: строка может уехать в общий конфиг в ту же минуту, а без них
    /// получатель не соотнесёт её ни с чем и не сможет ни обновить, ни снять тумбстоуном (та же
    /// причина, что у AddParamFile). Готовые SyncId/AddedAt/UpdatedAt уважаются — ими пользуется
    /// импорт конфига, перенося чужую строку вместе с её отметками.</summary>
    public int AddFwAttachment(FwAttachment a)
    {
        var syncId = string.IsNullOrEmpty(a.SyncId) ? Guid.NewGuid().ToString() : a.SyncId;
        var addedAt = string.IsNullOrEmpty(a.AddedAt) ? NowIso() : a.AddedAt;
        var updatedAt = string.IsNullOrEmpty(a.UpdatedAt) ? NowIsoPrecise() : a.UpdatedAt;

        ExecuteNonQuery("""
            INSERT INTO fw_attachments
                (fw_version_id, filename, disk_path, kind, comment, added_by, added_at, deleted_at, sync_id, updated_at)
            VALUES(@fw, @fn, @path, @kind, @comment, @by, @at, @del, @sy, @upd)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@fw", a.FwVersionId);
            cmd.Parameters.AddWithValue("@fn", a.Filename);
            cmd.Parameters.AddWithValue("@path", a.DiskPath);
            cmd.Parameters.AddWithValue("@kind", a.Kind);
            cmd.Parameters.AddWithValue("@comment", a.Comment);
            cmd.Parameters.AddWithValue("@by", a.AddedBy);
            cmd.Parameters.AddWithValue("@at", addedAt);
            cmd.Parameters.AddWithValue("@del", a.DeletedAt);
            cmd.Parameters.AddWithValue("@sy", syncId);
            cmd.Parameters.AddWithValue("@upd", updatedAt);
        });

        a.SyncId = syncId;
        a.AddedAt = addedAt;
        a.UpdatedAt = updatedAt;
        a.Id = Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));
        return a.Id.Value;
    }

    /// <summary>Правка вида/комментария. <paramref name="updatedAt"/> задаётся только импортом конфига
    /// (там нужна ЧУЖАЯ отметка, иначе применённая правка коллеги выглядела бы как наша собственная,
    /// только что сделанная, и поехала бы обратно как более свежая).</summary>
    public void UpdateFwAttachment(int id, string kind, string comment, string? updatedAt = null)
    {
        ExecuteNonQuery("UPDATE fw_attachments SET kind=@k, comment=@c, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@k", kind ?? "");
            cmd.Parameters.AddWithValue("@c", comment ?? "");
            cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(updatedAt) ? NowIsoPrecise() : updatedAt);
            cmd.Parameters.AddWithValue("@id", id);
        });
    }

    /// <summary>Снятие вложения — отметкой, а не DELETE: строка обязана продолжать ездить по машинам
    /// как положительный сигнал «это удалили» (см. док FwAttachment). Файл на диске здесь не
    /// трогается: чем и когда его убирать, решает FirmwareExtraFilesService — приехавшее по
    /// синхронизации удаление файлы коллеги не сносит.</summary>
    public void TombstoneFwAttachment(int id, string? deletedAt = null)
    {
        var stamp = string.IsNullOrEmpty(deletedAt) ? NowIsoPrecise() : deletedAt;
        ExecuteNonQuery("UPDATE fw_attachments SET deleted_at=@d, updated_at=@d WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", stamp);
            cmd.Parameters.AddWithValue("@id", id);
        });
    }

    /// <summary>Ссылается ли на тот же файл ещё какое-нибудь ЖИВОЕ вложение (кроме
    /// <paramref name="exceptId"/>). У неперестроенной версии доп. материалы лежат в общей папке
    /// контроллера, одной на все его версии, — там один и тот же файл вполне может быть приложен к
    /// нескольким версиям, и удаление вложения у одной из них не должно уносить файл у остальных.</summary>
    public bool IsAttachmentFileSharedByOthers(string diskPath, int exceptId)
    {
        if (string.IsNullOrWhiteSpace(diskPath)) return false;
        return ExecuteScalar("""
            SELECT 1 FROM fw_attachments
            WHERE deleted_at='' AND id<>@id AND disk_path=@p COLLATE NOCASE LIMIT 1
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@id", exceptId);
            cmd.Parameters.AddWithValue("@p", diskPath);
        }) is not null;
    }

    /// <summary>Вид + комментарий + имя файла всех живых вложений, по версиям — одним чтением для
    /// снимка поиска (см. Database.Search.cs). Отдельным запросом, а не JOIN'ом к самому снимку:
    /// вложений у версии может быть сколько угодно, и JOIN размножил бы строки прошивок.</summary>
    internal Dictionary<int, string> GetFwAttachmentSearchText()
    {
        var byVersion = new Dictionary<int, List<string>>();
        using (var reader = ExecuteReader(
                   "SELECT fw_version_id, kind, comment, filename FROM fw_attachments WHERE deleted_at='' ORDER BY id"))
        {
            while (reader.Read())
            {
                var id = GetInt(reader, "fw_version_id");
                if (!byVersion.TryGetValue(id, out var parts)) byVersion[id] = parts = new List<string>();
                foreach (var value in new[] { GetString(reader, "kind"), GetString(reader, "comment"), GetString(reader, "filename") })
                    if (!string.IsNullOrWhiteSpace(value)) parts.Add(value);
            }
        }
        return byVersion.ToDictionary(kv => kv.Key, kv => string.Join(" ", kv.Value));
    }
}
