using System.IO;
using System.Text.Json;

namespace HandheldExplorer.Services;

public enum AppTheme { Auto, Dark, Light }
public enum SortKey { Name, Date, Size, Type }

/// <summary>User settings, stored in %AppData%\HandheldExplorer\settings.json.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Auto;
    public bool GridView { get; set; } = true;
    public SortKey SortBy { get; set; } = SortKey.Name;
    public bool SortDescending { get; set; }
    public bool ShowHidden { get; set; }
    public bool SingleTapOpen { get; set; } = true;
    public bool StartFullscreen { get; set; }
    public bool ShowSidebar { get; set; } = true;
    public List<string> Pinned { get; set; } = new();
    public string? LastPath { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HandheldExplorer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt or unreadable settings: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }
}
