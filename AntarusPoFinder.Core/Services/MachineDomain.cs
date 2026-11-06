namespace AntarusPoFinder.Core.Services;

/// <summary>ЧТО ЗА ДОМЕН У ЭТОГО КОМПЬЮТЕРА — ответ, собранный из того, что Windows знает и так,
/// без единого вопроса человеку.
///
/// <b>Зачем.</b> Кнопка «Обновить из домена» и окно входа спрашивали домен текстовым полем. Это
/// вопрос, на который администратор честно не обязан знать ответ: имя домена ему никто не
/// называл, а уж из какого домена входят коллеги — тем более. Пустое поле (или угаданный дефолт
/// «Elita» из ConfigService) превращало любую сверку в гадание. Здесь — то, что про домен можно
/// узнать у самой Windows.
///
/// <b>Почему запись, а не просто строка.</b> Имён у домена два: короткое (NetBIOS, «ELITA») и
/// полное (DNS, «elita.local»). В поля ввода и в PrincipalContext годятся оба, но показывать
/// человеку надо оба сразу — иначе «а почему в настройках написано другое» становится новым
/// вопросом вместо снятого старого. <see cref="Source"/> хранится ровно за этим же: чтобы в
/// интерфейсе можно было честно написать, откуда взялось значение.</summary>
/// <param name="Joined">Компьютер (или текущий сеанс) состоит в домене. False — локальная учётная
/// запись: домен не выдумываем, так и говорим.</param>
/// <param name="NetBios">Короткое имя: Environment.UserDomainName / USERDOMAIN.</param>
/// <param name="Dns">Полное имя: Domain.GetComputerDomain() или USERDNSDOMAIN. Может быть пустым,
/// если машина в домене, но полное имя недоступно (нет связи с контроллером домена).</param>
/// <param name="LogonServer">Контроллер домена, подтвердивший вход (LOGONSERVER), без «\\».
/// Диагностика: по нему видно, что сеанс действительно доменный.</param>
/// <param name="Source">Откуда взято значение — человеческим текстом, для подписи в интерфейсе.</param>
/// <param name="Error">Почему не удалось спросить полное имя (контроллер домена недоступен и
/// т.п.). Не ошибка приложения: короткое имя при этом обычно известно и работает.</param>
public record MachineDomainInfo(
    bool Joined, string NetBios, string Dns, string LogonServer, string Source, string? Error)
{
    public static MachineDomainInfo NotJoined(string machineName) =>
        new(false, "", "", "", $"локальная учётная запись компьютера «{machineName}»", null);

    /// <summary>Что подставлять в поле «Домен». Полное имя предпочтительнее короткого: в сетях с
    /// несколькими доменами и доверительными отношениями короткое имя разрешается не всегда, а
    /// полное — всегда. Пусто — подставлять нечего, и это тоже ответ.</summary>
    public string Best => Dns.Length > 0 ? Dns : NetBios;

    /// <summary>Одна строка под поле ввода: «этот компьютер в домене таком-то». Машина вне домена
    /// обязана получить СЛОВА, а не пустое поле — иначе администратор снова остаётся наедине с
    /// вопросом, которого не понимает.</summary>
    public string Describe()
    {
        if (!Joined)
            return "Этот компьютер не в домене — вы работаете под локальной учётной записью. " +
                   "Имя домена придётся спросить у того, кто заводил доменные учётные записи.";

        var name = NetBios.Length > 0 && Dns.Length > 0 && !MachineDomain.Same(NetBios, Dns)
            ? $"{NetBios} / {Dns}"
            : (Dns.Length > 0 ? Dns : NetBios);
        var text = $"Этот компьютер в домене {name}.";
        if (LogonServer.Length > 0) text += $" Вход подтвердил контроллер {LogonServer}.";
        if (Error is not null) text += $" Полное имя домена спросить не удалось: {Error}";
        return text;
    }
}

/// <summary>Разбор того, что отдала Windows, и сравнение имён доменов. Чистая часть — поход за
/// самими значениями живёт в App (MachineDomainProbe): System.DirectoryServices.ActiveDirectory
/// есть только под Windows, а Core собирается под чистый net8.0 и обязан проверяться тестами без
/// домена вообще.</summary>
public static class MachineDomain
{
    /// <summary>Собрать ответ из сырых значений. Главный приём — сравнение
    /// <paramref name="userDomain"/> с <paramref name="machineName"/>: на машине ВНЕ домена
    /// Windows подставляет в USERDOMAIN имя самого компьютера, и без этой проверки приложение
    /// бодро отрапортовало бы «вы в домене WS-BUH-07» и ушло бы искать там группы.</summary>
    public static MachineDomainInfo FromProbe(string userDomain, string machineName,
        string dnsDomain, string logonServer, string computerDomain, string? error)
    {
        userDomain = Clean(userDomain);
        machineName = Clean(machineName);
        dnsDomain = Clean(dnsDomain);
        computerDomain = Clean(computerDomain);
        logonServer = Clean(logonServer).TrimStart('\\');

        var dns = computerDomain.Length > 0 ? computerDomain : dnsDomain;
        var sessionInDomain = userDomain.Length > 0 &&
                              !string.Equals(userDomain, machineName, StringComparison.OrdinalIgnoreCase);

        if (!sessionInDomain && dns.Length == 0) return MachineDomainInfo.NotJoined(machineName);

        var source = computerDomain.Length > 0
            ? "членство компьютера в домене (Active Directory)"
            : dnsDomain.Length > 0
                ? "текущий сеанс Windows (USERDNSDOMAIN)"
                : "текущий сеанс Windows (USERDOMAIN)";

        return new MachineDomainInfo(true, userDomain, dns, logonServer, source, error);
    }

    /// <summary>Приведение имени домена к сравнимому виду: без пробелов, без «\\» в начале, без
    /// точки в конце (так DNS-имена пишут в некоторых настройках), без регистра.</summary>
    public static string Normalize(string? domain) =>
        (domain ?? "").Trim().TrimStart('\\').TrimEnd('.').ToLowerInvariant();

    /// <summary>Один ли это домен. Сравнивать строки в лоб нельзя: «ELITA» и «elita.local» — одно и
    /// то же место, записанное коротким и полным именем, и человек в разных полях пишет то одно,
    /// то другое. Поэтому при разной длине сравниваем ПЕРВУЮ метку DNS-имени с коротким именем:
    /// в подавляющем большинстве установок AD они и совпадают. Это эвристика, и используется она
    /// только там, где ошибка безобидна — чтобы НЕ показать лишнее предупреждение о чужом домене;
    /// ни одного решения о роли на ней не строится.</summary>
    public static bool Same(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        return FirstLabel(x) == FirstLabel(y);
    }

    private static string FirstLabel(string normalized)
    {
        var dot = normalized.IndexOf('.');
        return dot < 0 ? normalized : normalized[..dot];
    }

    private static string Clean(string? value) => (value ?? "").Trim();
}
