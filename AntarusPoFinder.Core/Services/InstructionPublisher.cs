using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AntarusPoFinder.Core.Services;

/// <summary>Кто выкладывает инструкцию на хостинг. Интерфейсом — чтобы загрузка версии не зависела
/// ни от сети, ни от HttpClient: в тестах подставляется запоминающая заглушка, а на машине без
/// заполненных ключей не подставляется вовсе (null), и весь этот слой просто не участвует.</summary>
public interface IInstructionPublisher
{
    /// <summary>Выложить файл (или папку постраничных сканов) по пути, который в БАЗЕ записан как
    /// путь на первом диске. Возвращает адрес выложенного файла или null, если выкладка не
    /// настроена; о неудачах сообщает через <paramref name="warnings"/> — загрузку версии они не
    /// отменяют.</summary>
    string? Publish(string actualPath, string pathOnFirstDisk, string firstDiskRoot, List<string> warnings);
}

/// <summary>Выкладка инструкции в бакет хостинга (см. <see cref="S3Settings"/>).
///
/// Ключ объекта считается от пути НА ПЕРВОМ ДИСКЕ, а не от того места, где файл физически лежит:
/// на третьем диске у него другой корень, а у коллеги третий диск вообще подключён под другой
/// буквой. Путь на первом диске — единственное, что одинаково у всех машин (именно он и пишется в
/// <c>fw_versions.instructions_path</c>), поэтому только от него и можно считать адрес, по которому
/// файл будет виден снаружи. Из того же пути строится ссылка под QR-кодом (LabelLinkBuilder) — так
/// наклейка, напечатанная на одной машине, ведёт на файл, выложенный с другой.
///
/// Инструкция ПАПКОЙ (постраничные сканы) выкладывается пофайлово, сохраняя вложенность: у бакета
/// нет каталогов, «папка» в нём — это общий префикс ключа, и ничего специально создавать не нужно.
///
/// Неудача выкладки — предупреждение, а не ошибка: файл к этому моменту уже лежит на диске, версия
/// уже создана, и отменять всё это из-за недоступного хостинга нельзя (ровно то же правило, что и
/// у недоступного третьего диска — см. InstructionDiskResolver).</summary>
public sealed class InstructionPublisher : IInstructionPublisher
{
    private readonly S3Settings _settings;
    private readonly S3Client _client;

    /// <summary>Сколько файлов из одной папки сканов выкладываем за раз — защита от того, чтобы
    /// случайно выбранная папка на тысячу файлов не превратила загрузку версии в получасовое
    /// ожидание. Превышение — предупреждение, а не молчаливое усечение.</summary>
    public const int MaxFilesPerFolder = 200;

    public InstructionPublisher(S3Settings settings, S3Client? client = null)
    {
        _settings = settings;
        _client = client ?? new S3Client();
    }

    /// <summary>Выкладчик по текущим настройкам — или null, если выкладывать некуда (ключи ещё не
    /// выданы, выкладка выключена, адрес не заполнен). Одно место, где принимается это решение:
    /// иначе каждый вызывающий проверял бы «настроено ли» по-своему, и рано или поздно один из них
    /// проверил бы не то. null здесь — штатное состояние, а не отсутствие возможности.</summary>
    public static IInstructionPublisher? For(S3Settings settings) =>
        settings.CanPublish ? new InstructionPublisher(settings) : null;

    public string? Publish(string actualPath, string pathOnFirstDisk, string firstDiskRoot, List<string> warnings)
    {
        if (!_settings.CanPublish) return null;

        var relative = LabelLinkBuilder.RelativeTo(firstDiskRoot, pathOnFirstDisk);
        if (relative is null)
        {
            // Файл лежит вне диска прошивок — считать его адрес на хостинге не от чего, и
            // выкладывать «куда-нибудь» нельзя: ссылка под QR всё равно указывала бы не туда.
            warnings.Add("Инструкция: файл вне диска прошивок, на хостинг не выложен");
            return null;
        }

        if (Directory.Exists(actualPath)) return PublishFolder(actualPath, relative, warnings);
        return PublishFile(actualPath, relative, warnings);
    }

    private string? PublishFile(string filePath, string relative, List<string> warnings)
    {
        var key = _settings.KeyFor(relative);
        var result = _client.PutFileAsync(_settings, key, filePath, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (result.Ok) return result.Url;
        warnings.Add($"Инструкция: на хостинг не выложена — {result.Error}");
        return null;
    }

    private string? PublishFolder(string folderPath, string relative, List<string> warnings)
    {
        string[] files;
        try { files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories); }
        catch (Exception ex)
        {
            warnings.Add($"Инструкция: папку не прочитать — {ex.Message}");
            return null;
        }

        if (files.Length > MaxFilesPerFolder)
        {
            warnings.Add($"Инструкция: в папке {files.Length} файлов — на хостинг не выкладываем " +
                         $"(предел {MaxFilesPerFolder}), проверьте, ту ли папку выбрали");
            return null;
        }

        var failed = 0;
        foreach (var file in files)
        {
            var inside = LabelLinkBuilder.RelativeTo(folderPath, file);
            if (inside is null) continue;
            var key = _settings.KeyFor(relative + "/" + inside.Replace('\\', '/'));
            var result = _client.PutFileAsync(_settings, key, file, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!result.Ok)
            {
                if (failed == 0) warnings.Add($"Инструкция: на хостинг не выложена — {result.Error}");
                failed++;
            }
        }

        if (failed > 0) return null;
        return S3Client.PublicUrl(_settings, _settings.KeyFor(relative));
    }
}
