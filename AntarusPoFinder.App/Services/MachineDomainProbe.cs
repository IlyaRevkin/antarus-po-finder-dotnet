using System.DirectoryServices.ActiveDirectory;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>СПРОСИТЬ У WINDOWS, В КАКОМ ДОМЕНЕ ЭТОТ КОМПЬЮТЕР — вместо того чтобы спрашивать у
/// человека.
///
/// Жалоба Ильи дословно: «я не знаю, как проверить, к какому конкретно я домену отношусь, и
/// другие». Поля «Домен» в окне входа и в «Обновить из домена» требовали ответа, которого
/// администратор не обязан знать. Всё нужное Windows знает сама:
/// <list type="bullet">
/// <item><c>Environment.UserDomainName</c> — короткое (NetBIOS) имя домена текущего сеанса; вне
///   домена там лежит имя самого компьютера (на этом и ловим «машина не в домене», см.
///   <see cref="MachineDomain.FromProbe"/>);</item>
/// <item><c>USERDNSDOMAIN</c> — полное DNS-имя, его выставляет сам вход в домен;</item>
/// <item><c>LOGONSERVER</c> — контроллер домена, подтвердивший вход: доказательство, что сеанс
///   действительно доменный, а не «логин похож на доменный»;</item>
/// <item><see cref="Domain.GetComputerDomain"/> — домен, в который ВВЕДЁН компьютер. Самый точный
///   источник и единственный, который ходит в сеть, поэтому он же единственный, который умеет
///   подвиснуть на недоступном контроллере — отсюда кэш и отдельная ветка с ошибкой.</item>
/// </list>
///
/// <b>Почему результат кэшируется на всё время работы.</b> Компьютер не переезжает между доменами
/// посреди сеанса, а вот дёргать сетевой вызов на каждое открытие окна настроек — верный способ
/// получить подвисший интерфейс там, где связи с доменом нет (ровно тот случай, ради которого всё
/// и делается). Нужна свежая проверка — <see cref="Invalidate"/>.</summary>
public static class MachineDomainProbe
{
    private static MachineDomainInfo? _cached;
    private static readonly object Gate = new();

    /// <summary>Что известно про домен этого компьютера. Сеть трогается максимум один раз за
    /// запуск приложения; любой отказ превращается в текст в <see cref="MachineDomainInfo.Error"/>,
    /// а не в исключение: «не смогли спросить домен» никогда не повод ронять окно.</summary>
    public static MachineDomainInfo Current()
    {
        lock (Gate)
        {
            if (_cached is not null) return _cached;

            string computerDomain = "";
            string? error = null;
            try
            {
                computerDomain = Domain.GetComputerDomain().Name;
            }
            catch (ActiveDirectoryObjectNotFoundException)
            {
                // Штатный ответ «компьютер не введён в домен» — не ошибка и показывать её незачем:
                // вывод об отсутствии домена сделает FromProbe по USERDOMAIN/имени машины.
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _cached = MachineDomain.FromProbe(
                userDomain: SafeEnv(() => Environment.UserDomainName),
                machineName: SafeEnv(() => Environment.MachineName),
                dnsDomain: Environment.GetEnvironmentVariable("USERDNSDOMAIN") ?? "",
                logonServer: Environment.GetEnvironmentVariable("LOGONSERVER") ?? "",
                computerDomain: computerDomain,
                error: error);
            return _cached;
        }
    }

    /// <summary>Домен, который надо подставить в поле ввода: сначала то, что задано в настройках
    /// (администратор мог указать другой домен осознанно — например, доверенный), иначе —
    /// определённый автоматически. Ровно этот порядок убирает вопрос «а почему программа
    /// переписала то, что я вписал».</summary>
    public static string Suggest(string configured)
    {
        var cfg = (configured ?? "").Trim();
        return cfg.Length > 0 ? cfg : Current().Best;
    }

    /// <summary>Забыть кэш — для кнопки «проверить заново» и для тестов.</summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    private static string SafeEnv(Func<string> read)
    {
        try { return read(); }
        catch { return ""; }
    }
}
