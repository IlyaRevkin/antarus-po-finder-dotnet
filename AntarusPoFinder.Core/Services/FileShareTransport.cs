using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

/// <summary>Единственная сегодня реализация <see cref="ISyncTransport"/> — тот же общий сетевой
/// диск, что и раньше, только файловый I/O вынесен за интерфейс. Поведение не отличается от того,
/// что раньше делал ConfigSyncService напрямую (File.ReadAllBytes/WriteAllBytes на
/// Конфиг\po_finder_config.json) — это чистый рефакторинг существующей логики, задел на будущий
/// транспорт (HTTPS/WebDAV/обратный прокси), который подключится ещё одной реализацией этого же
/// интерфейса без изменений в ConfigSyncService.</summary>
public class FileShareTransport : ISyncTransport
{
    private readonly string _root;
    private readonly string _configDir;

    /// <summary>Публично — нужен ConfigSyncService для построения SharedConfigSnapshot.Path/
    /// ConfigUpdateInfo.ConfigPath (существующие поля, которые внешний код — NetworkSyncView,
    /// тесты — читает как обычный путь на диске).</summary>
    public string ConfigPath { get; }
    public string RevisionPath { get; }

    public FileShareTransport(string root)
    {
        _root = root;
        _configDir = Path.Combine(root, "Конфиг");
        ConfigPath = Path.Combine(_configDir, "po_finder_config.json");
        RevisionPath = Path.Combine(_configDir, "revision.json");
    }

    public Task<bool> IsAvailableAsync() => Task.FromResult(Directory.Exists(_root));

    public Task<SyncRevisionMarker?> ReadRevisionAsync()
    {
        try
        {
            if (!File.Exists(RevisionPath)) return Task.FromResult<SyncRevisionMarker?>(null);
            var text = File.ReadAllText(RevisionPath);
            var marker = JsonSerializer.Deserialize<SyncRevisionMarker>(text);
            return Task.FromResult(marker);
        }
        catch
        {
            // Повреждённый/недописанный маркер (гонка записи на шаре, экспорт с двух машин почти
            // одновременно) — клиент откатывается на старую схему сравнения exported_at, а не падает.
            return Task.FromResult<SyncRevisionMarker?>(null);
        }
    }

    public Task WriteRevisionAsync(SyncRevisionMarker marker)
    {
        Directory.CreateDirectory(_configDir);
        var json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
        // Маркер сознательно НЕ шифруется (см. класс-doc ISyncTransport) — крошечный файл, секретных
        // данных не несёт, а дешёвая читаемость снаружи (диагностика «какая ревизия на диске» без
        // запуска самого приложения) важнее.
        File.WriteAllText(RevisionPath, json);
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadConfigAsync()
    {
        if (!File.Exists(ConfigPath)) return Task.FromResult<byte[]?>(null);
        return Task.FromResult<byte[]?>(File.ReadAllBytes(ConfigPath));
    }

    public Task WriteConfigAsync(byte[] bytes)
    {
        Directory.CreateDirectory(_configDir);
        FileSystemHelpers.UnprotectForOwnWrite(ConfigPath);
        File.WriteAllBytes(ConfigPath, bytes);
        FileSystemHelpers.ProtectFromExternalEdits(ConfigPath);
        return Task.CompletedTask;
    }
}
