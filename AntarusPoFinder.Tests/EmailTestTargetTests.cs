using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Куда уходит пробное письмо.
///
/// Жалоба Ильи дословно: «я не могу тестовое письмо отправить конкретному email, надо добавить
/// такой функционал». Раньше адрес был жёстко равен «От кого» — проверить можно было только то, что
/// сервер принимает письмо сам себе, а самый частый вопрос при настройке («а до Иванова дойдёт?»)
/// не проверялся вообще.
///
/// Проверяется тестом, потому что развилок три и каждая ломается при правке соседней: пусто → себе,
/// мусор → сказать словами, группа AD → адресом письма быть не может.</summary>
public class EmailTestTargetTests
{
    /// <summary>Собственно просьба: вписали чужой адрес — письмо уходит ему.</summary>
    [Fact]
    public void Вписанный_адрес_перебивает_от_кого()
    {
        var цель = EmailTestTarget.Resolve("ivanov@company.ru", "noreply@company.ru");

        Assert.True(цель.Ok);
        Assert.Equal("ivanov@company.ru", цель.Address);
        Assert.False(цель.ToSelf);
    }

    /// <summary>Пустое поле обязано работать как до его появления — письмо себе. Иначе привычная
    /// кнопка вдруг начинает требовать ввода, и это выглядит поломкой, а не новой возможностью.</summary>
    [Fact]
    public void Пустое_поле_шлёт_себе()
    {
        var цель = EmailTestTarget.Resolve("", "noreply@company.ru");

        Assert.True(цель.Ok);
        Assert.Equal("noreply@company.ru", цель.Address);
        Assert.True(цель.ToSelf);
    }

    [Fact]
    public void Лишние_пробелы_вокруг_адреса_не_мешают()
        => Assert.Equal("ivanov@company.ru", EmailTestTarget.Resolve("  ivanov@company.ru  ", "x@y.ru").Address);

    /// <summary>Группа AD адресом одиночного письма быть не может — её раскрывает почтовый сервер
    /// (см. EmailNotifier.Send). Молча отправить «себе» вместо неё нельзя: человек решил бы, что
    /// группа проверена и работает.</summary>
    [Fact]
    public void Группа_AD_в_пробном_письме_отвергается_словами()
    {
        var цель = EmailTestTarget.Resolve("Программисты", "noreply@company.ru");

        Assert.False(цель.Ok);
        Assert.Equal("", цель.Address);
        Assert.Contains("группу AD", цель.Error);
    }

    [Fact]
    public void Без_адреса_и_без_от_кого_слать_некуда()
    {
        var цель = EmailTestTarget.Resolve("", "");

        Assert.False(цель.Ok);
        Assert.Contains("От кого", цель.Error);
    }
}
