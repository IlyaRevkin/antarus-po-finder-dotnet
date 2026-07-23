using System.Collections.Generic;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>Одна запись журнала изменений в маркере ревизии — см. <see cref="SyncRevisionMarker"/>.
/// Строго человекочитаемая, не машинный diff: описание пишет вызывающий код (например
/// MainWindowViewModel.PushCatalogChange), маркер их только хранит и ограничивает количество.</summary>
public class SyncChangeEntry
{
    public string Ts { get; set; } = "";
    public string Author { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>Содержимое Конфиг\revision.json — маленький незашифрованный файл-маркер рядом с
/// основным (зашифрованным) po_finder_config.json. Существует ровно для одной цели: дать клиенту
/// дешёвый способ понять «было ли что-то новое» БЕЗ чтения и расшифровки всего конфига — который на
/// медленной сетевой шаре может быть заметно тяжелее одного маленького JSON. Revision — монотонно
/// растущий счётчик, не связанный со стенными часами машины-экспортёра (в отличие от прежнего
/// сравнения exported_at по строкам), поэтому не ломается, если у одной из машин разошлось время.</summary>
public class SyncRevisionMarker
{
    public int Revision { get; set; }
    public string ExportedAt { get; set; } = "";
    public string ExportedBy { get; set; } = "";

    /// <summary>Последние N изменений (см. ConfigSyncService.MaxChangelogEntries), самые новые
    /// первыми — то, что показывает плашка «Поступили изменения» на принимающей машине.</summary>
    public List<SyncChangeEntry> Changes { get; set; } = new();
}

/// <summary>Абстракция над каналом обмена общим конфигом — сегодня единственная реализация это
/// файловая сетевая шара (см. FileShareTransport), в будущем может стать HTTPS/WebDAV/обратный
/// прокси без изменений в ConfigSyncService: он работает только через этот интерфейс, конкретная
/// реализация подставляется через ConfigSyncService.TransportFactory. Единица обмена — сырые байты:
/// шифрование/расшифровка самого конфига остаётся заботой ConfigSyncService (ConfigFileCrypto),
/// транспорт про это ничего не знает.</summary>
public interface ISyncTransport
{
    /// <summary>Дёшево — не читает сам конфиг, только проверяет достижимость канала (для файловой
    /// шары — существование корневой папки).</summary>
    Task<bool> IsAvailableAsync();

    /// <summary>Читает маркер ревизии. Null означает одно из двух: маркера ещё нет (общий диск, на
    /// который ещё никто не экспортировал через версию приложения с этой функцией) либо канал
    /// недоступен/маркер повреждён — оба случая клиент обязан трактовать одинаково: «надёжных
    /// сведений о ревизии нет, работаем по старой схеме сравнения exported_at».</summary>
    Task<SyncRevisionMarker?> ReadRevisionAsync();

    /// <summary>Перезаписывает маркер целиком (не патчит отдельные поля) — вызывающий сам отвечает
    /// за монотонность revision и склейку журнала изменений (см.
    /// ConfigSyncService.BumpRevisionMarkerCas — оттуда же и best-effort compare-and-swap).</summary>
    Task WriteRevisionAsync(SyncRevisionMarker marker);

    /// <summary>Сырые (зашифрованные — расшифровка на стороне ConfigSyncService) байты основного
    /// файла конфига. Null — файла ещё нет.</summary>
    Task<byte[]?> ReadConfigAsync();

    /// <summary>Записывает сырые байты основного файла конфига (создаёт папку при необходимости).</summary>
    Task WriteConfigAsync(byte[] bytes);
}
