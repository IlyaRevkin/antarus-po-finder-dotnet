using System;
using System.Net.Http;
using System.Security.Authentication;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Как хранилище объясняет сетевые беды.
///
/// Жалоба Ильи 16.09.2026: «при отправке тикетов выдаёт ошибку SSL какую-то». Ровно так .NET и
/// говорит — «The SSL connection could not be established, see inner exception»; понять по этой
/// строке нечего, а причина почти всегда не в программе, и чинится она не здесь. Сообщение обязано
/// называть причину и говорить, что делать и к кому идти.</summary>
public class StorageErrorWordingTests
{
    [Fact]
    public void TlsFailure_IsExplainedAndSaysWhatToDo()
    {
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure."));

        var text = S3Client.Explain(ex);

        Assert.Contains("корпоративный фильтр", text, StringComparison.Ordinal);
        Assert.Contains("сертификат", text, StringComparison.Ordinal);
        // Не «обратитесь к администратору» вообще, а конкретно куда смотреть.
        Assert.Contains("Доверенные корневые", text, StringComparison.Ordinal);
        Assert.DoesNotContain("see inner exception", text, StringComparison.Ordinal);
    }

    /// <summary>Настоящая причина лежит во ВНУТРЕННЕМ исключении: у верхнего сообщение
    /// бессодержательное. Если разворачивать цепочку перестанут, признак «SSL» найдётся всё равно —
    /// поэтому проверяем и случай, где наверху вообще ничего похожего нет.</summary>
    [Fact]
    public void CauseIsTakenFromTheInnerException()
    {
        var ex = new HttpRequestException("Ошибка при отправке запроса.",
            new AuthenticationException("The remote certificate is invalid."));

        var text = S3Client.Explain(ex);
        Assert.Contains("сертификат", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NameResolutionFailure_IsCalledByItsName()
    {
        var ex = new HttpRequestException("No such host is known. (fs.elitacompany.ru:443)");
        Assert.Contains("не разрешается", S3Client.Explain(ex), StringComparison.Ordinal);
    }

    /// <summary>Всё прочее пересказывать не выдумываем: чужое сообщение лучше выдуманного.</summary>
    [Fact]
    public void UnknownFailure_IsPassedThroughAsIs()
    {
        Assert.Equal("что-то своё", S3Client.Explain(new InvalidOperationException("что-то своё")));
    }
}
