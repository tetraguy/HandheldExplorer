# Handheld Explorer

<img width="1283" height="808" alt="image" src="https://github.com/user-attachments/assets/8d26d9aa-d773-469a-896c-e6f62f18fe4c" />

A touch- and controller-first file explorer for Windows handhelds (ROG Ally, Legion Go, MSI Claw,
Steam Deck running Windows, or any PC with an Xbox-style pad). WPF, .NET 8, no NuGet dependencies.

## Build

1. Open `HandheldExplorer.sln` in Visual Studio 2022 or newer (".NET desktop development" workload).
2. Press F5.

Command line: `dotnet build -c Release`, or for a single portable exe:
`dotnet publish HandheldExplorer -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`

The project targets `net8.0-windows` and rolls forward to newer installed runtimes.

## Controls

| Input | Action |
|---|---|
| D-pad / left stick | Move |
| A | Open / press |
| B | Back, cancel, clear selection or filter |
| X | Actions for the item (copy, move, rename, delete, zip, pin, properties...) |
| Y | Select / deselect (multi-select) |
| LB / RB | Back / forward |
| LT / RT | Page up / down |
| View | Grid or list |
| Menu | Main menu |
| Left stick click | Show / hide sidebar |
| Right stick click | Filter the current folder |
| Touch | Tap to open, press and hold for actions, drag to scroll |
| Keyboard | Arrows, Enter, Backspace, Space, F2, Del, Ctrl+C/X/V/A/F/H, Ctrl+Shift+N, F5, F10, F11, Alt+Enter |

## Layout of the code

- `MainWindow.xaml(.cs)` - UI, navigation, input routing, menus
- `Controls/OnScreenKeyboard.cs` - controller-friendly text entry
- `Services/GamepadService.cs` - XInput polling with auto-repeat
- `Services/ThemeManager.cs` - dark/light + Windows accent colour, live updates
- `Services/FileOps.cs` - copy / move / recycle / zip
- `Services/ShellThumbnail.cs` - Explorer icons and thumbnails
- `Themes/` - colour palettes and control styles

Settings are stored in `%AppData%\HandheldExplorer\settings.json`.
