using System;

namespace AntarusPoFinder.Core.Loader;

/// <summary>ЧТО ДЕЛАТЬ, КОГДА LOADER НЕ УВИДЕЛ ПЛК.
///
/// <b>Откуда задача.</b> С 21.10.2026 журналы заливок приходят сами (LoaderDiagnostics), и первое,
/// что в них видно, — не окирпичивание, а вот это: наладчик жмёт «Повторить попытку» по пятнадцать
/// раз подряд, иногда по четыре раза за две секунды, и каждый раз получает ОДНУ И ТУ ЖЕ ошибку.
/// Пример живого прогона 22.10.2026, машина NALADKA1: 14 отказов «Активный USB/RNDIS Ethernet
/// адаптер ПЛК не найден» за две минуты, после чего та же прошивка ушла с первого раза — по
/// Ethernet.
///
/// Прежняя подсказка была одна на все случаи («оставьте только шнур загрузки, снимите питание»).
/// Она верная, но не отвечает на вопрос «а что именно сейчас не так», и поэтому читается как общие
/// слова: человек жмёт повтор, ничего не меняя.
///
/// <b>Что делает этот разбор.</b> По тексту отказа Loader определяет, в чём дело, и называет
/// ДЕЙСТВИЕ: переключить подключение, проверить кабель, подать питание. Текст один на всё
/// приложение и проверяется тестами — формулировка здесь часть решения, а не оформление.</summary>
public static class LoaderFailureHint
{
    /// <summary>Что предложить сделать.</summary>
    public enum Remedy
    {
        /// <summary>Ничего конкретного — общая подсказка про обвязку.</summary>
        None,

        /// <summary>Loader настроен на USB, адаптера ПЛК нет: предложить Ethernet.</summary>
        SwitchToEthernet,

        /// <summary>Loader настроен на Ethernet, ПЛК не отвечает: предложить USB.</summary>
        SwitchToUsb,
    }

    public sealed record Advice(string Text, Remedy Remedy)
    {
        /// <summary>Есть ли что предложить кнопкой. Пусто — показываем только текст.</summary>
        public bool HasButton => Remedy != Remedy.None;
    }

    /// <summary>Общая подсказка — когда причина не опознана. Остаётся прежней: она не про
    /// конкретную ошибку, а про обвязку, и за год подтвердилась на практике.</summary>
    public const string Generic =
        "Если Loader не увидел ПЛК: оставьте подключённым только шнур загрузки — панель, модем и " +
        "прочие кабели на время заливки лучше отсоединить, — снимите и подайте питание на " +
        "контроллер и повторите. Режим подключения (USB/Ethernet, адрес, адаптер) проверяется в " +
        "«Настройки → Лоадер».";

    /// <summary>Разобрать отказ и сказать, что делать.
    ///
    /// <paramref name="repeatCount"/> — сколько раз подряд пришла ОДНА И ТА ЖЕ ошибка. С третьего
    /// раза текст прямо говорит, что повтор сам по себе ничего не изменит: по журналам именно так
    /// и теряют по две минуты, нажимая кнопку.</summary>
    public static Advice For(string? failure, PlcConnectionMode mode, int repeatCount = 1)
    {
        var text = (failure ?? "").ToLowerInvariant();

        var insist = repeatCount >= 3
            ? "Эта ошибка повторяется подряд — повтор сам по себе её не изменит. "
            : "";

        // USB-режим и нет RNDIS-адаптера: ПЛК не подключён шнуром, не запитан, либо драйвер не
        // поднялся. Самый частый отказ в журналах.
        if (text.Contains("usb") && (text.Contains("rndis") || text.Contains("адаптер")))
            return new Advice(
                insist +
                "Loader настроен на USB, но сетевой адаптер ПЛК в системе не появился. Проверьте, что " +
                "шнур загрузки подключён напрямую к контроллеру и на контроллер подано питание: " +
                "адаптер появляется через несколько секунд после включения. Если ПЛК в щите и до " +
                "него дотянута сеть — переключитесь на Ethernet.",
                Remedy.SwitchToEthernet);

        // Ethernet-режим и ПЛК не отвечает (DHCP или стабильный адрес).
        if (text.Contains("dhcp") || text.Contains("ethernet") || text.Contains("не отвеча"))
            return new Advice(
                insist +
                "Loader настроен на Ethernet, но ПЛК не ответил. Проверьте кабель и питание, а также " +
                "что выбран тот сетевой адаптер компьютера, в который воткнут шнур. Если контроллер " +
                "лежит на столе и подключён шнуром загрузки — переключитесь на USB.",
                Remedy.SwitchToUsb);

        return new Advice(insist + Generic, Remedy.None);
    }

    /// <summary>Подпись кнопки под предложение.</summary>
    public static string ButtonCaption(Remedy remedy) => remedy switch
    {
        Remedy.SwitchToEthernet => "Переключить на Ethernet и повторить",
        Remedy.SwitchToUsb => "Переключить на USB и повторить",
        _ => "",
    };

    /// <summary>Режим, на который переключаем.</summary>
    public static PlcConnectionMode TargetMode(Remedy remedy) => remedy switch
    {
        Remedy.SwitchToEthernet => PlcConnectionMode.Ethernet,
        _ => PlcConnectionMode.Usb,
    };
}
