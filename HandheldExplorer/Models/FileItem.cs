using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using HandheldExplorer.Services;

namespace HandheldExplorer.Models;

/// <summary>One file or folder shown in the list.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _iconRequested;
    private bool _isChecked;

    public FileItem(FileSystemInfo info)
    {
        FullPath = info.FullName;
        Name = info.Name;
        IsFolder = (info.Attributes & FileAttributes.Directory) != 0;
        IsHidden = (info.Attributes & FileAttributes.Hidden) != 0;
        Modified = info.LastWriteTime;
        Size = !IsFolder && info is FileInfo f ? f.Length : 0;
        Extension = IsFolder ? "" : info.Extension;
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsFolder { get; }
    public bool IsHidden { get; }
    public DateTime Modified { get; }
    public long Size { get; }
    public string Extension { get; }

    public string SizeText => IsFolder ? "" : Format.Bytes(Size);
    public string DateText => Modified.ToString("g");
    public string TypeText => IsFolder ? "Folder"
        : Extension.Length > 1 ? Extension[1..].ToUpperInvariant() + " file" : "File";

    /// <summary>Multi-select check mark (independent of the focus cursor).</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; Raise(nameof(IsChecked)); } }
    }

    /// <summary>Shell icon / thumbnail. Loaded lazily the first time the UI asks for it.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                _ = LoadIconAsync();
            }
            return _icon;
        }
    }

    private async Task LoadIconAsync()
    {
        var image = await ShellThumbnail.GetAsync(FullPath, 128, iconOnly: IsFolder);
        if (image != null)
        {
            _icon = image;
            Raise(nameof(Icon));
        }
    }

    /// <summary>Reads a folder. Runs on a background thread.</summary>
    public static List<FileItem> Enumerate(string path, bool showHidden)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? (FileAttributes)0 : FileAttributes.Hidden | FileAttributes.System
        };
        var result = new List<FileItem>();
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            try { result.Add(new FileItem(info)); }
            catch { /* skip entries that vanish or can't be read */ }
        }
        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
