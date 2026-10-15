using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>КНОПКА СО ВЛОЖЕННЫМ ЭЛЕМЕНТОМ ПЕЧАТАЕТ «System.Windows.Controls.TextBlock».
///
/// Что случилось. В 1.75.0.0 состояние диска в нижней строке сделали кликабельным, обернув надпись в
/// Button. В окне вместо «Диск: ✓» появилось «System.Windows.Controls.TextBlock» — Илья прислал
/// фотографию экрана.
///
/// Почему. У кнопок по умолчанию в Themes/Styles.xaml задан ContentTemplate с
/// <c>Text="{Binding}"</c> — он рассчитан СТРОГО на строковый Content (так лечили тёмный текст на
/// синей кнопке в светлой теме). Любой вложенный элемент такой шаблон печатает через ToString().
/// В комментарии к самому стилю это написано прямо: «a composite icon+text button would need its own
/// explicit Style». Предупреждение было — и его всё равно пропустили.
///
/// Поэтому правило теперь проверяется машиной, а не вниманием: кнопка со вложенным содержимым
/// обязана либо иметь свой Style, либо явно отменить шаблон содержимого
/// (<c>ContentTemplate="{x:Null}"</c>). Собственный Template этого НЕ отменяет — ContentTemplate
/// живёт отдельно, и ровно на этом здесь и обожглись.</summary>
public class ButtonContentTemplateTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static IEnumerable<string> XamlFiles()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AntarusPoFinder.App"));
        return Directory.EnumerateFiles(dir, "*.xaml", SearchOption.AllDirectories)
            // Темы — это сами стили, а не разметка окон: там Button встречается только в шаблонах.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}"));
    }

    /// <summary>Содержимое кнопки — то, что лежит внутри неё самой, без служебных свойств
    /// (Button.Template, Button.Resources, Button.ToolTip и прочих «Button.*»), которые задаются
    /// элементами-свойствами и содержимым не являются.</summary>
    private static bool HasElementContent(XElement button) =>
        button.Elements().Any(e => !e.Name.LocalName.StartsWith("Button.", StringComparison.Ordinal));

    [Fact]
    public void Кнопка_со_вложенным_элементом_обязана_отменить_шаблон_содержимого()
    {
        var offenders = new List<string>();

        foreach (var file in XamlFiles())
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch { continue; }   // не разобрался как XML — это забота компилятора, не наша

            foreach (var button in doc.Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                if (!HasElementContent(button)) continue;

                var hasOwnStyle = button.Attribute("Style") is not null;
                var cancelsTemplate = button.Attribute("ContentTemplate")?.Value is "{x:Null}";
                // Шаблон содержимого можно задать и элементом-свойством — это тоже «свой».
                var hasOwnContentTemplate = button.Elements()
                    .Any(e => e.Name.LocalName == "Button.ContentTemplate");

                if (hasOwnStyle || cancelsTemplate || hasOwnContentTemplate) continue;

                var line = (button as IXmlLineInfo).HasLineInfo() ? ((IXmlLineInfo)button).LineNumber : 0;
                offenders.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Кнопка со вложенным содержимым напечатает «System.Windows.Controls.…» вместо него: "
            + "задайте ей свой Style или ContentTemplate=\"{x:Null}\". Найдено: " + string.Join(", ", offenders));
    }

    /// <summary>Сам тест обязан что-то видеть: если разметку перестанут находить, он замолчит и
    /// будет зелёным на пустом месте.</summary>
    [Fact]
    public void Разметка_приложения_находится()
    {
        Assert.True(XamlFiles().Count() > 10, "не нашлось XAML-файлов приложения — тест смотрит не туда");
    }
}
