using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>Генерирует вспомогательный скрипт самоподмены .exe при обновлении приложения: он ждёт
/// завершения текущего процесса (пока exe залочен — переписать его нельзя), переносит
/// скачанную/подготовленную копию <c>*.update</c> на место оригинала и перезапускает программу. Вынесено
/// из <c>AppUpdateService.InstallAndRestart</c> отдельным классом в Core именно затем, чтобы генерацию
/// можно было проверить тестом БЕЗ настоящего Shutdown/перезапуска (см. UpdateRestartScriptTests):
/// правильное экранирование путей с пробелами и кириллицей, ожидание PID и — главное —
/// перезапуск даже в ветке ошибки переноса, чтобы приложение не осталось закрытым.
///
/// <b>Почему .cmd, а не PowerShell.</b> Прежняя реализация запускала .ps1 через
/// <c>powershell -File</c>, и это зависело от <c>ExecutionPolicy</c>. В корпоративном домене групповая
/// политика часто ставит <c>Restricted</c>/<c>AllSigned</c>, и тогда <c>powershell -File script.ps1</c>
/// молча отказывается исполнять скрипт. Приложение к этому моменту уже закрылось
/// (<c>Application.Current.Shutdown()</c>), а скрипт, который должен был подменить exe и снова запустить
/// программу, не отрабатывал — отсюда «скачалось, закрылось, обратно не открылось, exe остался старым».
/// Виден баг был только у части людей: на машинах с мягкой политикой (RemoteSigned) всё работало.
/// <c>cmd.exe</c> ExecutionPolicy НЕ подчиняется и исполняется в любой заблокированной среде — это
/// снимает проблему в корне, а не обходит её (<c>-ExecutionPolicy Bypass</c> жёсткая GPO тоже может
/// запретить).</summary>
public static class UpdateRestartScript
{
    /// <summary>Кодовая страница, в которой .cmd-файл должен быть записан на диск и в которой затем
    /// читается лог ошибки (<c>AppUpdateService.TakeLastUpdateError</c>). Скрипт первой же строкой
    /// делает <c>chcp 866</c>, поэтому кириллица в <c>echo</c> и локализованные сообщения самого
    /// <c>move</c> печатаются той же однобайтовой DOS-кодировкой независимо от того, какая кодовая
    /// страница консоли стоит на машине по умолчанию. Однобайтовая (в отличие от UTF-8/65001) — чтобы
    /// не нарваться на известную привычку cmd терять символы при чтении UTF-8-батника со сдвигом.</summary>
    public const int CodePage = 866;

    /// <summary>Собирает текст .cmd-скрипта. Возвращается обычная строка (UTF-16); на диск её пишет
    /// вызывающая сторона в кодировке <see cref="CodePage"/> — см. AppUpdateService.InstallAndRestart.
    /// <paramref name="processId"/> — PID текущего процесса, которого скрипт дожидается; переносить
    /// файл раньше нельзя, пока живой процесс держит exe заблокированным.
    /// <paramref name="stagedExePath"/> — подготовленная копия новой версии (обычно <c>currentExe + ".update"</c>).
    /// <paramref name="currentExePath"/> — путь к текущему exe, поверх которого ставится новая версия.
    /// <paramref name="errorLogPath"/> — фиксированный путь лога, который читает
    /// <c>AppUpdateService.TakeLastUpdateError</c> при следующем запуске.</summary>
    public static string BuildCmd(int processId, string stagedExePath, string currentExePath, string errorLogPath)
    {
        var staged = QuoteCmdPath(stagedExePath);
        var current = QuoteCmdPath(currentExePath);
        var log = QuoteCmdPath(errorLogPath);
        // Временный файл для перехвата собственного текста ошибки move (в него уходит его stderr) —
        // рядом с логом, детерминированный путь, чтобы его можно было и дописать в лог, и подчистить.
        var moveErr = QuoteCmdPath(errorLogPath + ".move.txt");

        // CRLF — родные переводы строк для .bat/.cmd. Управляющие слова (chcp, tasklist, move, start,
        // goto) — ASCII и от кодовой страницы не зависят; кириллица есть только в сообщении echo.
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        // Пиннинг кодовой страницы: сообщение echo и локализованные ошибки move печатаются 866-й,
        // а лог мы 866-й же и читаем — см. CodePage.
        sb.Append("chcp 866 >nul\r\n");
        // Ждать смерти процесса. tasklist с фильтром печатает либо строку с PID, либо "INFO: No tasks
        // are running..." без PID; find по номеру и различает эти два случая. "|| goto" — уходим на
        // подмену, как только PID пропал (find вернул ненулевой код). Пауза ping'ом, а не timeout'ом:
        // timeout читает консольный ввод и в скрытом окне иногда падает "input redirection is not
        // supported", а ping 127.0.0.1 ждёт безусловно и есть на любой Windows.
        sb.Append(":AntarusWait\r\n");
        sb.Append($"tasklist /FI \"PID eq {processId}\" /NH 2>nul | find \"{processId}\" >nul || goto AntarusReplace\r\n");
        sb.Append("ping -n 2 127.0.0.1 >nul\r\n");
        sb.Append("goto AntarusWait\r\n");
        // Подмена. "&& goto" перескакивает запись лога, только если move завершился успехом; иначе —
        // пишем причину в лог (перезапись, а не дозапись: >), добавляем собственный текст ошибки move
        // и ВСЁ РАВНО запускаем current — при провале переноса current остался старой версией, но
        // приложение хотя бы не останется закрытым (прямое требование постановки).
        sb.Append(":AntarusReplace\r\n");
        sb.Append($"move /y {staged} {current} >nul 2>{moveErr} && goto AntarusLaunch\r\n");
        sb.Append($">{log} echo Автообновление не установилось: не удалось заменить старый файл программы новой версией. Возможно, файл занят антивирусом или программой, либо нет прав на запись в папку установки.\r\n");
        sb.Append($"type {moveErr} >>{log} 2>nul\r\n");
        sb.Append(":AntarusLaunch\r\n");
        sb.Append($"del {moveErr} >nul 2>nul\r\n");
        sb.Append($"start \"\" {current}\r\n");
        // Самоудаление батника последней строкой — читать из него больше нечего, стандартный приём.
        sb.Append("del \"%~f0\" >nul 2>nul\r\n");
        return sb.ToString();
    }

    /// <summary>Оборачивает путь в кавычки для cmd. В именах файлов Windows кавычка запрещена, так что
    /// экранировать саму кавычку не нужно; внутри кавычек литеральны и пробел, и <c>&amp; ^ ! &lt; &gt; |</c>
    /// (delayed expansion мы не включаем, поэтому <c>!</c> безопасен). Единственный спецсимвол, который
    /// раскрывается и в кавычках, — <c>%</c> (в имени пользователя он допустим, значит может быть и в
    /// <c>%LocalAppData%\Programs\AntarusPoFinder</c>); в .cmd-файле литеральный процент — это <c>%%</c>.</summary>
    public static string QuoteCmdPath(string path) => "\"" + path.Replace("%", "%%") + "\"";
}
