using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.App;

/// <summary>Одна тема оформления: что показать в списке и какой словарь подменить.</summary>
/// <param name="Id">Как хранится в настройках (ConfigService.Theme). Менять нельзя — у людей уже
/// выбрано.</param>
/// <param name="IsDark">Тёмная ли. От этого зависят не только цвета: заголовок окна красится через
/// DWM, а наведение и фон выделения считаются от акцента по-разному (см. AccentPalette).</param>
/// <param name="Beta">Тема, которую ещё не предлагают человеку: она есть в списке, её словарь
/// проверяется тестом на полноту ключей, но в настройках её не видно. До 2.2 так были закрыты
/// «Material» и «Стекло», пока их обкатывали в бете; сейчас таких тем нет ни одной, признак
/// оставлен как заготовка под следующую недоделанную.</param>
/// <param name="Backdrop">Нужно ли этой теме РАЗМЫТИЕ за окном (DWM acrylic). Только цветом стекло
/// не делается: стекло — это размытие того, что под окном, и рисует его система (см.
/// WindowBackdrop).</param>
/// <param name="Tonal">Мягкие поверхности вместо сплошного акцента: вторичные кнопки и выбранный
/// пункт меню заливаются ОТТЕНКОМ текущего акцента, а подпись на них остаётся обычного цвета
/// текста. Так устроены Material 3 (tonal button, secondary container) и интерфейсы Apple
/// (подсвеченная строка вместо плашки). Оттенок считает ThemeManager от выбранного акцента —
/// только так он остаётся верным при любом цвете, который человек поставил себе сам.</param>
public sealed record AppTheme(string Id, string Label, string Dictionary, bool IsDark, bool Beta = false,
    bool Backdrop = false, bool Tonal = false);

/// <summary>СПИСОК ТЕМ — одно место вместо «если dark, то Dark.xaml, иначе Light.xaml».
///
/// Пока тем было две, развилка в ThemeManager читалась нормально. Просьба Ильи (бета 2.0): «добавь
/// темы типа матиреал дизайн, ликвид Гласс и тп» — и с четырьмя вариантами тернарник превращается в
/// место, где забывают поправить одну из трёх веток.
///
/// Словари ОБЯЗАНЫ объявлять один и тот же набор ключей: стили ссылаются на них по имени, и
/// недостающий ключ — это не «цвет по умолчанию», а исключение при разборе разметки. За этим следит
/// тест ThemeCatalogTests.</summary>
public static class ThemeCatalog
{
    public static readonly AppTheme[] All =
    {
        new("light", "Светлая", "Light", IsDark: false),
        new("dark", "Тёмная", "Dark", IsDark: true),
        // Material — плотные поверхности и спокойный контраст, привычные по Android и вебу.
        new("material", "Material", "Material", IsDark: false, Tonal: true),
        // «Стекло» — тёмная основа с полупрозрачными карточками: модно и, что важнее, даёт глубину
        // там, где у нас много вложенных панелей.
        new("glass", "Стекло", "Glass", IsDark: true, Backdrop: true, Tonal: true),
    };

    /// <summary>Что предлагать человеку в настройках — всё, что доделано. С 2.2 это все четыре
    /// темы: «Material» и «Стекло» выпущены вместе с остальной переделкой 2.x, и сборка на отбор
    /// больше не влияет.</summary>
    public static IReadOnlyList<AppTheme> Available =>
        All.Where(t => !t.Beta).ToList();

    /// <summary>Тема по сохранённому имени. Неизвестное имя — светлая: так ведёт себя и старый код
    /// (всё, что не «dark», считалось светлым), и настройка могла приехать с машины, где стоит
    /// более новая сборка с темой, которой здесь ещё нет.</summary>
    public static AppTheme Resolve(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
