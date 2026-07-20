using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

namespace AntarusPoFinder.App.ViewModels;

public class QuickAppItem
{
    public string Name { get; }
    public string Path { get; }
    public ImageSource? IconSource { get; }
    public RelayCommand LaunchCommand { get; }

    public QuickAppItem(string name, string path)
    {
        Name = name;
        Path = path;
        IconSource = ExtractIcon(path);
        LaunchCommand = new RelayCommand(Launch);
    }

    private static ImageSource? ExtractIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var bmp = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void Launch()
    {
        try { Process.Start(new ProcessStartInfo(Path) { UseShellExecute = true }); }
        catch { /* missing/broken shortcut — nothing sensible to surface here, matches Python's silent os.startfile failures elsewhere */ }
    }
}
