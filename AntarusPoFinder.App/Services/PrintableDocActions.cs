using System.Diagnostics;
using System.IO;
using System.Windows;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Открыть / собрать PDF / отправить на печать документ, у которого есть исходник Word и
/// собираемый из него PDF (см. InstructionDocResolver). Таких документов в программе два —
/// инструкция к прошивке и шаблон паспорта шкафа, — и работают они с точки зрения оператора
/// одинаково: «править docx», «открыть PDF для печати», «печать». Поэтому логика живёт здесь одна,
/// а не копией в каждой странице: разъехавшиеся копии этого кода означали бы, что паспорт печатается
/// не так, как инструкция, без единой причины.</summary>
public static class PrintableDocActions
{
    /// <summary>Собирает PDF рядом с docx (в папке самого документа). Если папка недоступна для
    /// записи (сетевая шара только на чтение) — во временную, чтобы хотя бы напечатать. null —
    /// конвертера на машине нет или документ не преобразовался.</summary>
    /// <param name="tempFolderName">Имя подпапки в %TEMP% для запасного варианта — своё у каждого
    /// вида документа, чтобы одноимённые файлы разных сущностей не затирали друг друга.</param>
    public static string? ConvertToPdf(InstructionDoc doc, string tempFolderName)
    {
        var made = DocxToPdfConverter.Convert(doc.Docx!, doc.ExpectedPdfPath!);
        if (made is not null) return made;
        try
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), tempFolderName);
            Directory.CreateDirectory(tmpDir);
            var tmp = Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(doc.Docx!) + ".pdf");
            return DocxToPdfConverter.Convert(doc.Docx!, tmp);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Готовый к печати PDF: если docx правили после последней сборки (или PDF ещё нет),
    /// пересобирает его, иначе отдаёт уже лежащий. Конвертация небыстрая (поднимает Word/LibreOffice) —
    /// уводится в фон под постоянным индикатором занятости: без него оператору кажется, что по клику
    /// «ничего не происходит». null — печатать нечего, сообщение об этом уже показано.</summary>
    /// <param name="title">Заголовок сообщений: «Инструкция», «Паспорт».</param>
    /// <param name="genitive">Тот же документ в родительном падеже — он подставляется в текст
    /// («PDF инструкции готов», «файл паспорта не найден»), поэтому отдельным параметром: склеивать
    /// русскую фразу из именительного падежа не выходит.</param>
    /// <param name="editHint">Чем открыть исходник вручную, если конвертера на машине нет.</param>
    public static async Task<string?> EnsurePdfAsync(InstructionDoc doc, IAppHost host, string title,
        string genitive, string tempFolderName, string editHint)
    {
        if (doc.Pdf is not null && !doc.PdfStale) return doc.Pdf;
        if (doc.Docx is null)
        {
            if (doc.Pdf is not null) return doc.Pdf;
            AppMessageBox.Show($"Печатать нечего: файл {genitive} не найден.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        // Сборка PDF поднимает Word/LibreOffice — на сетевом диске это легко несколько секунд, а то и
        // десятки. Одной мелькающей строки статуса мало: оператор жаловался, что после клика «ничего
        // не происходит» и кажется, что программа зависла. Показываем ПОСТОЯННЫЙ индикатор фоновой
        // работы внизу окна — он виден, пока using не закрыт, а не гаснет, как ShowStatus.
        string? made;
        using (host.BeginBusy($"Готовим PDF {genitive} для печати — открывается Word/LibreOffice, это может занять несколько секунд…"))
        {
            host.ShowStatus($"Готовим PDF {genitive} для печати…", 8000);
            made = await Task.Run(() => ConvertToPdf(doc, tempFolderName));
        }
        host.ShowStatus(made is not null ? $"PDF {genitive} готов" : "");
        if (made is not null) return made;

        // Конвертация не удалась. Устаревший PDF лучше, чем ничего; иначе честно говорим, что нужно.
        if (doc.Pdf is not null) return doc.Pdf;
        AppMessageBox.Show(
            "Не удалось собрать PDF из документа Word.\n\nДля автоматической конвертации нужен установленный " +
            $"Microsoft Word или LibreOffice. Пока их нет — откройте и распечатайте исходный документ вручную {editHint}.",
            title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    /// <summary>Отправить файл на принтер по умолчанию через ассоциированное приложение (verb «print»).
    /// Нет ассоциации/принтера — открываем файл, дальше оператор печатает сам.</summary>
    public static void Print(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { Verb = "print", UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Open(path);
        }
    }

    /// <summary>Открыть проводник на этом файле и ВЫДЕЛИТЬ его. Не то же, что «открыть файл»: файл,
    /// который нужен другой программе (прошивка ПЛК от поставщика — её кладут в среду разработки
    /// контроллера перетаскиванием), открывать нечем и незачем, а вот дотянуться до него мышью
    /// нужно — из выделенного в проводнике файла его и перетаскивают.
    ///
    /// Папку тоже принимает (тогда просто открывает её), а у исчезнувшего файла открывает
    /// родительскую папку: «проводник не открылся вообще» — худший ответ из возможных.</summary>
    public static void Reveal(string path)
    {
        try
        {
            var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(path);
                if (parent is null || !Directory.Exists(parent)) return;
                args = $"\"{parent}\"";
            }
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Проводник не запустился (крайне редкий случай) — показывать окно с ошибкой ради этого
            // незачем: человек нажмёт ещё раз или откроет папку сам.
        }
    }

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            var reply = AppMessageBox.Show(
                $"Не удалось открыть файл:\n{path}\n\nВозможно, не установлена программа для этого типа файлов.\n\nОткрыть папку с файлом?",
                "Открыть файл", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (reply != MessageBoxResult.Yes) return;
            try
            {
                var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (folder is not null) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch { /* дальше уже нечего предложить */ }
        }
    }
}
