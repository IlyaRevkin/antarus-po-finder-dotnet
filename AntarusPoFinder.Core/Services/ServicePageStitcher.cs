using System.Collections.Generic;
using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AntarusPoFinder.Core.Services;

/// <summary>Дописывает страницу «обратитесь в сервис» ПОСЛЕДНЕЙ страницей в сам PDF инструкции.
///
/// <b>Почему внутрь документа, а не файлом рядом.</b> Заказчик открывает по QR один файл — саму
/// инструкцию. Файл-спутник в папке на диске он не увидит никогда: до него доберётся только тот, кто
/// смотрит сетевую шару в проводнике. Плюс спутник — это ещё и сотни одинаковых файлов на шаре, по
/// одному на каждую инструкцию. Поэтому страница живёт внутри того же документа, и решение это
/// прямое: «страницу по сервису, если есть инструкция, надо вшивать в тот же файл последней».
///
/// <b>Оригинал на диске не трогаем.</b> Сшивка идёт при ВЫКЛАДКЕ, во временный файл, и наверх уезжает
/// он. На сетевой шаре остаётся ровно тот документ, который туда положил человек: переписывать чужой
/// файл ради нашей страницы нельзя — его правят, пересылают и сверяют с бумажной копией.
///
/// <b>Идемпотентность.</b> Главный риск здесь — вторая, третья, десятая одинаковая страница в конце
/// документа после каждой перезаливки. Он закрыт двумя независимыми способами:
/// <list type="number">
/// <item><description>сшивка всегда начинается с ЧИСТОГО оригинала с диска, у которого нашей страницы
/// нет по построению;</description></item>
/// <item><description>и всё же перед добавлением из документа удаляются ВСЕ страницы с нашей меткой
/// (<see cref="MarkerKey"/>). Это не перестраховка: сшитый документ легко попадает обратно в оборот —
/// его скачивают с хостинга и прикладывают к версии как инструкцию, — и вот тогда без этой уборки
/// страницы начали бы накапливаться. Заодно правка макета не плодит вторую страницу, а заменяет
/// прежнюю.</description></item>
/// </list>
///
/// Неудача сшивки — не ошибка выкладки: документ уезжает наверх как есть, а причина ложится в
/// предупреждения. Инструкция без страницы сервиса лучше, чем ненайденная инструкция.</summary>
public static class ServicePageStitcher
{
    /// <summary>Ключ в словаре страницы, которым помечена наша вставка. Нестандартный ключ в словаре
    /// страницы — законная часть формата: читалки чужие ключи игнорируют.</summary>
    public const string MarkerKey = "/ANTARUSServicePage";

    /// <summary>Что вышло. <paramref name="Path"/> — что отправлять на хостинг: сшитый временный файл
    /// либо исходный, если сшивать не понадобилось или не удалось.</summary>
    public sealed record Result(string Path, bool Stitched, int Replaced);

    /// <summary>Приписать страницу к документу. <paramref name="servicePagePdf"/> — одностраничный PDF
    /// с обращением в сервис (его рисует тот же макет, что и обычные заглушки).</summary>
    public static Result Append(string documentPdf, string servicePagePdf, string targetPdf, string stamp,
        List<string>? warnings = null)
    {
        try
        {
            using var doc = PdfReader.Open(documentPdf, PdfDocumentOpenMode.Modify);

            // Сначала убрать ранее вшитые наши страницы — иначе повторная выкладка дописывала бы
            // вторую такую же. Идём с конца: удаление сдвигает индексы.
            var replaced = 0;
            for (var i = doc.PageCount - 1; i >= 0; i--)
            {
                if (!doc.Pages[i].Elements.ContainsKey(MarkerKey)) continue;
                doc.Pages.RemoveAt(i);
                replaced++;
            }

            using (var page = PdfReader.Open(servicePagePdf, PdfDocumentOpenMode.Import))
            {
                if (page.PageCount == 0)
                {
                    warnings?.Add("Страница обращения в сервис пуста — документ выложен без неё.");
                    return new Result(documentPdf, false, 0);
                }
                var added = doc.AddPage(page.Pages[0]);
                added.Elements[MarkerKey] = new PdfString(stamp);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPdf)!);
            // PageCount читается ДО Save: после сохранения документ считается отработанным и на любой
            // вопрос отвечает исключением.
            doc.Save(targetPdf);
            return new Result(targetPdf, true, replaced);
        }
        catch (Exception ex)
        {
            // Битый, зашифрованный или просто нестандартный PDF — выкладываем как есть.
            warnings?.Add($"«{Path.GetFileName(documentPdf)}»: страницу с телефоном сервиса вшить не удалось " +
                          $"({ex.Message}) — документ выложен без неё.");
            return new Result(documentPdf, false, 0);
        }
    }

    /// <summary>Сколько в документе наших вшитых страниц. Нужно тестам и разбору «почему их две».</summary>
    public static int CountStitchedPages(string pdf)
    {
        try
        {
            using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
            var count = 0;
            for (var i = 0; i < doc.PageCount; i++)
                if (doc.Pages[i].Elements.ContainsKey(MarkerKey)) count++;
            return count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Сколько всего страниц. Ноль — прочитать не вышло.</summary>
    public static int PageCount(string pdf)
    {
        try
        {
            using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
            return doc.PageCount;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
