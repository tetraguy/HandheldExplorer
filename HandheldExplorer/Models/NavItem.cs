namespace HandheldExplorer.Models;

/// <summary>An entry in the sidebar: quick-access folder, pinned folder or drive.</summary>
public sealed class NavItem
{
    public string Glyph { get; init; } = "";
    public string Label { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsDrive { get; init; }
    public bool IsPinned { get; init; }
    public string Sub { get; init; } = "";
    public double Fraction { get; init; }
}
