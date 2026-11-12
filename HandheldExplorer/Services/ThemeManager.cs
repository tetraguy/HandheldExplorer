using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace HandheldExplorer.Services;

/// <summary>Dark / light theming that follows the Windows app-mode setting and accent colour.</summary>
public static class ThemeManager
{
    private static AppTheme _theme = AppTheme.Auto;
    private static bool _hooked;

    public static bool IsDark { get; private set; } = true;

    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        if (!_hooked)
        {
            _hooked = true;
            // Fires when the user flips dark/light mode or changes the accent colour.
            SystemEvents.UserPreferenceChanged += (_, _) =>
                Application.Current?.Dispatcher.InvokeAsync(() => Apply(_theme));
        }

        IsDark = theme == AppTheme.Dark || (theme == AppTheme.Auto && SystemUsesDarkMode());

        var app = Application.Current;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(IsDark ? "Dark" : "Light")}.xaml")
        };
        app.Resources.MergedDictionaries[0] = dictionary;

        var accent = SystemAccent();
        app.Resources["AccentBrush"] = Frozen(accent);
        app.Resources["AccentSoftBrush"] = Frozen(Color.FromArgb(0x48, accent.R, accent.G, accent.B));
        double luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        app.Resources["AccentTextBrush"] = Frozen(luminance > 0.6 ? Colors.Black : Colors.White);

        foreach (Window window in app.Windows) ApplyTitleBar(window);
    }

    /// <summary>Makes the native title bar match the theme.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        try { Native.DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int)); }
        catch { /* older Windows builds */ }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static bool SystemUsesDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return true; }
    }

    private static Color SystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }
        catch { /* fall through */ }
        return Color.FromRgb(0x3B, 0x82, 0xF6);
    }
}
