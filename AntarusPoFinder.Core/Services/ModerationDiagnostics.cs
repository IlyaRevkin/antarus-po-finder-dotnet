using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntarusPoFinder.Core.Services;

/// <summary>Почему цифра на «Модерации» не сходится со списком — собрать доказательства на той
/// машине, где это видно.
///
/// <b>Зачем это существует.</b> Жалоба наладчика дословно: «в модерации прошивок не реальное
/// значение их количества, у кого-то 2–3, у меня 20+». Чинить это вслепую я уже пробовал: предположил
/// сиротские записи, написал тест — и тест доказал, что такого быть не может (внешние ключи
/// включены). То есть догадка была неверной, а причина осталась неизвестной. Данных с той машины
/// нет и не будет, пока кто-то не сядет за неё и не перепишет цифры руками, — поэтому их собирает
/// сама программа и отправляет тем же молчаливым каналом, что и журналы заливок.
///
/// <b>Что именно собирается.</b> Одно и то же число, посчитанное РАЗНЫМИ путями, плюс срезы по
/// каждому условию отбора. Расхождение между путями и покажет, какое условие виновато: именно
/// поэтому здесь не одна итоговая цифра, а несколько — одна ничего не объясняет.
///
/// <b>Что НЕ собирается.</b> Ни имён файлов, ни описаний, ни тегов, ни путей на диске — только
/// числа и идентификаторы. Это диагностика расхождения счётчика, а не выгрузка чужой базы: лишнее
/// здесь пришлось бы объяснять тому, чью машину читают.</summary>
public static class ModerationDiagnostics
{
    public sealed record Snapshot
    {
        [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
        [JsonPropertyName("at")] public string At { get; init; } = "";
        [JsonPropertyName("machine")] public string Machine { get; init; } = "";
        [JsonPropertyName("user")] public string User { get; init; } = "";
        [JsonPropertyName("role")] public string Role { get; init; } = "";
        [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";

        /// <summary>Число, которое показывает значок рядом с «Модерацией».</summary>
        [JsonPropertyName("badge")] public int Badge { get; init; }

        /// <summary>Сколько строк реально отдаёт список страницы.</summary>
        [JsonPropertyName("listRows")] public int ListRows { get; init; }

        /// <summary>Срезы по отдельным условиям отбора: где числа разойдутся, там и причина.</summary>
        [JsonPropertyName("slices")] public Dictionary<string, int> Slices { get; init; } = new();

        /// <summary>Номера версий, стоящих в очереди. Нужны, чтобы сравнить ДВЕ машины: если наборы
        /// разные, дело не в счёте, а в том, что отметка «выведена из модерации» не доехала.</summary>
        [JsonPropertyName("versions")] public List<string> Versions { get; init; } = new();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(Snapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    /// <summary>Есть ли о чём сообщать. Отправлять снимок с каждой машины, где всё сходится, значит
    /// утопить настоящий случай в сотне пустых — поэтому отправка привязана к расхождению.</summary>
    public static bool WorthSending(Snapshot snapshot) => snapshot.Badge != snapshot.ListRows;

    /// <summary>Имя файла снимка. Машина в имени нужна, чтобы при разборе не открывать каждый файл:
    /// жалоба звучит как «у кого-то 2–3, у меня 20+», и сравнивать придётся именно по машинам.</summary>
    public static string FileNameFor(Snapshot snapshot)
    {
        var machine = snapshot.Machine;
        foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) machine = machine.Replace(bad, '-');
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        return $"moderation_{stamp}_{machine}.json";
    }
}
