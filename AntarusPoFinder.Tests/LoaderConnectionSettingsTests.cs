using System.IO;
using System.Text.Json.Nodes;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Перенос выбора «USB / Ethernet + адрес + адаптер» в настройки самого Segnetics Loader.
/// Файл ЧУЖОЙ и его схема нами не документирована, поэтому главное, что здесь проверяется, — что мы
/// физически не можем его испортить: правим только уже существующие строковые поля, ничего не
/// придумываем, при непонятном содержимом не трогаем вовсе.</summary>
public class LoaderConnectionSettingsTests
{
    private static string WriteSettings(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Apply_UpdatesExistingFields_AndKeepsBackup()
    {
        using var root = new TempRoot();
        var path = WriteSettings(root.Path,
            """{ "connectionMode": "usb", "ipAddress": "10.0.0.1", "networkAdapter": "Ethernet", "other": 5 }""");

        var result = LoaderConnectionSettings.Apply(PlcConnectionMode.Ethernet, "192.168.1.50", "USB-LAN", path);

        Assert.True(result.Applied);
        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("ethernet", (string?)json["connectionMode"]);
        Assert.Equal("192.168.1.50", (string?)json["ipAddress"]);
        Assert.Equal("USB-LAN", (string?)json["networkAdapter"]);
        Assert.Equal(5, (int?)json["other"]);           // чужие поля не тронуты
        Assert.True(File.Exists(path + ".antarus-backup"));
    }

    [Fact]
    public void Apply_FindsFieldsInNestedSection()
    {
        using var root = new TempRoot();
        var path = WriteSettings(root.Path,
            """{ "connection": { "mode": "usb", "host": "10.0.0.1" } }""");

        var result = LoaderConnectionSettings.Apply(PlcConnectionMode.Ethernet, "192.168.1.7", adapter: null, path);

        Assert.True(result.Applied);
        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("ethernet", (string?)json["connection"]!["mode"]);
        Assert.Equal("192.168.1.7", (string?)json["connection"]!["host"]);
    }

    [Fact]
    public void Apply_NoMatchingFields_ChangesNothing_AndExplains()
    {
        using var root = new TempRoot();
        var original = """{ "recentFiles": ["a.psl"], "windowWidth": 900 }""";
        var path = WriteSettings(root.Path, original);

        var result = LoaderConnectionSettings.Apply(PlcConnectionMode.Usb, "192.168.1.7", "USB-LAN", path);

        Assert.False(result.Applied);
        Assert.Contains("выберите режим", result.Message);
        Assert.Equal(original, File.ReadAllText(path));   // файл не тронут вовсе
        Assert.False(File.Exists(path + ".antarus-backup"));
    }

    [Fact]
    public void Apply_NoSettingsFile_IsNotAnError()
    {
        using var root = new TempRoot();
        var result = LoaderConnectionSettings.Apply(PlcConnectionMode.Usb, null, null,
            Path.Combine(root.Path, "settings.json"));

        Assert.False(result.Applied);
        Assert.Contains("ещё не созданы", result.Message);
    }

    [Fact]
    public void Apply_NothingChosen_DoesNotTouchSettingsAtAll()
    {
        using var root = new TempRoot();
        var original = """{ "connectionMode": "usb" }""";
        var path = WriteSettings(root.Path, original);

        var result = LoaderConnectionSettings.Apply(PlcConnectionMode.Unspecified, "", "", path);

        Assert.False(result.Applied);
        Assert.Null(result.Message);                      // «как в Loader» — молча ничего не делаем
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Apply_UsbMode_DoesNotWriteIpAddress()
    {
        using var root = new TempRoot();
        var path = WriteSettings(root.Path, """{ "connectionMode": "ethernet", "ipAddress": "10.0.0.1" }""");

        var result = LoaderConnectionSettings.Apply(PlcConnectionMode.Usb, "192.168.1.50", adapter: null, path);

        Assert.True(result.Applied);
        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("usb", (string?)json["connectionMode"]);
        // Адрес для USB не используется — записывать его туда было бы враньём про подключение.
        Assert.Equal("10.0.0.1", (string?)json["ipAddress"]);
    }

    [Theory]
    [InlineData("usb", PlcConnectionMode.Usb)]
    [InlineData("Ethernet", PlcConnectionMode.Ethernet)]
    [InlineData("lan", PlcConnectionMode.Ethernet)]
    [InlineData("", PlcConnectionMode.Unspecified)]
    [InlineData("что-то ещё", PlcConnectionMode.Unspecified)]
    public void ParseMode_UnderstandsStoredValues(string raw, PlcConnectionMode expected) =>
        Assert.Equal(expected, LoaderConnectionSettings.ParseMode(raw));
}
