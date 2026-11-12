using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandheldExplorer.Controls;
using HandheldExplorer.Models;
using HandheldExplorer.Services;

namespace HandheldExplorer;

public partial class MainWindow : Window
{
    /// <summary>Folders with more items than this always use the virtualized list view.</summary>
    private const int GridItemLimit = 800;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly GamepadService _pad = new();
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly DispatcherTimer _refreshDebounce;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _driveTimer;

    private List<FileItem> _all = new();       // everything in the current folder
    private List<FileItem> _visible = new();   // after filter + sort
    private string _path = "";
    private string _filter = "";
    private List<string> _clipboard = new();
    private bool _clipboardIsCut;
    private int _loadVersion;
    private bool _gridActive = true;
    private string _driveSignature = "";
    private FileSystemWatcher? _watcher;
    private OnScreenKeyboard? _keyboard;
    private bool _overlayDismissable = true;
    private CancellationTokenSource? _operation;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen;

    private record MenuEntry(string Glyph, string Label, Action Action);

    public MainWindow()
    {
        ThemeManager.Apply(_settings.Theme);
        InitializeComponent();

        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _refreshDebounce.Tick += async (_, _) =>
        {
            // Don't rebuild the list under an open menu or a running copy; try again later.
            if (OverlayOpen || _operation != null) return;
            _refreshDebounce.Stop();
            await RefreshAsync();
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

        _driveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _driveTimer.Tick += async (_, _) => await BuildDrivesAsync();

        _pad.Pressed += OnPad;
        _pad.ConnectionChanged += connected => ShowToast(connected ? "Controller connected" : "Controller disconnected");

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _pad.Stop();
            _watcher?.Dispose();
            _settings.Save();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Sidebar.Visibility = _settings.ShowSidebar ? Visibility.Visible : Visibility.Collapsed;
        ApplyView(_settings.GridView);
        BuildQuickAccess();
        BuildPinned();
        _ = BuildDrivesAsync();
        _driveTimer.Start();
        _pad.Start();

        if (_settings.StartFullscreen) ToggleFullscreen();

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string start = _settings.LastPath is { Length: > 0 } last && Directory.Exists(last) ? last : home;
        await NavigateAsync(start, addHistory: false);
    }

    // =====================================================================================
    //  Navigation and folder loading
    // =====================================================================================

    private async Task NavigateAsync(string path, bool addHistory = true, string? selectName = null, bool focusFiles = true)
    {
        int version = ++_loadVersion;
        List<FileItem> items;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            bool showHidden = _settings.ShowHidden;
            items = await Task.Run(() => FileItem.Enumerate(path, showHidden));
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) ShowToast("Can't open this location: " + ex.Message);
            return;
        }
        if (version != _loadVersion) return; // a newer navigation won

        bool samePath = string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);
        if (samePath)
        {
            // Refresh: keep check marks on items that still exist.
            var keep = _all.Where(i => i.IsChecked).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items) item.IsChecked = keep.Contains(item.Name);
        }
        else
        {
            if (addHistory && _path.Length > 0)
            {
                _backStack.Push(_path);
                _forwardStack.Clear();
            }
            _filter = "";
        }

        _path = path;
        _all = items;
        _settings.LastPath = path;

        ShowItems(selectName, focusFiles);
        if (!samePath)
        {
            BuildCrumbs();
            Watch(path);
        }
    }

    private Task RefreshAsync(string? selectName = null)
    {
        if (_path.Length == 0) return Task.CompletedTask;
        selectName ??= (FileList.SelectedItem as FileItem)?.Name;
        return NavigateAsync(_path, addHistory: false, selectName, focusFiles: FileList.IsKeyboardFocusWithin || OverlayOpen);
    }

    /// <summary>Applies filter + sort and pushes the result into the list.</summary>
    private void ShowItems(string? selectName = null, bool focusFiles = true)
    {
        IEnumerable<FileItem> query = _all;
        if (_filter.Length > 0)
            query = query.Where(i => i.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));

        var list = query.ToList();
        list.Sort(CompareItems);
        _visible = list;

        ApplyView(_settings.GridView && list.Count <= GridItemLimit);
        FileList.ItemsSource = list;

        EmptyText.Text = _filter.Length > 0 ? "Nothing matches the filter" : "This folder is empty";
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (list.Count > 0)
        {
            int index = selectName == null ? 0
                : list.FindIndex(i => string.Equals(i.Name, selectName, StringComparison.OrdinalIgnoreCase));
            FileList.SelectedIndex = Math.Max(0, index);
            FileList.ScrollIntoView(FileList.SelectedItem);
        }

        UpdateStatus();
        if (focusFiles) Dispatcher.InvokeAsync(() => { if (!OverlayOpen) FocusFiles(); }, DispatcherPriority.Loaded);
    }

    private int CompareItems(FileItem a, FileItem b)
    {
        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1; // folders first

        int result = _settings.SortBy switch
        {
            SortKey.Date => a.Modified.CompareTo(b.Modified),
            SortKey.Size => a.Size.CompareTo(b.Size),
            SortKey.Type => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            _ => 0
        };
        if (result == 0) result = Native.StrCmpLogicalW(a.Name, b.Name);
        return _settings.SortDescending ? -result : result;
    }

    private void ApplyView(bool grid)
    {
        ViewButton.Content = _settings.GridView ? "" : ""; // shows what the button switches to
        if (_gridActive == grid && FileList.ItemsSource != null) return;
        _gridActive = grid;
        FileList.ItemsPanel = (ItemsPanelTemplate)FindResource(grid ? "GridPanel" : "ListPanel");
        FileList.ItemTemplate = (DataTemplate)FindResource(grid ? "TileTemplate" : "RowTemplate");
    }

    private void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            void Changed(object sender, FileSystemEventArgs e) => Dispatcher.InvokeAsync(() =>
            {
                _refreshDebounce.Stop();
                _refreshDebounce.Start();
            });
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Changed += Changed;
            watcher.Renamed += (s, e) => Changed(s, e);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch { /* some locations (e.g. certain network shares) can't be watched */ }
    }

    private void GoBack()
    {
        if (_backStack.Count == 0) { GoUp(); return; }
        string previous = _path;
        string target = _backStack.Pop();
        _forwardStack.Push(previous);
        _ = NavigateAsync(target, addHistory: false, selectName: ChildNameIfParent(target, previous));
    }

    private void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        _backStack.Push(_path);
        _ = NavigateAsync(_forwardStack.Pop(), addHistory: false);
    }

    private void GoUp()
    {
        var parent = Directory.GetParent(_path);
        if (parent == null) { ShowToast("You're at the top of this drive"); return; }
        _ = NavigateAsync(parent.FullName, selectName: Path.GetFileName(_path));
    }

    /// <summary>When going back to a parent folder, re-select the folder we just left.</summary>
    private static string? ChildNameIfParent(string parent, string child)
    {
        var actualParent = Directory.GetParent(child)?.FullName;
        return actualParent != null &&
               string.Equals(Path.TrimEndingDirectorySeparator(actualParent), parent, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(child) : null;
    }

    private void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        string root = Path.GetPathRoot(_path) ?? "";
        var parts = new List<(string Label, string Target)>();
        if (root.Length > 0) parts.Add((root.TrimEnd('\\'), root));

        string current = root;
        foreach (var segment in _path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            parts.Add((segment, current));
        }

        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                var chevron = new TextBlock
                {
                    Text = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = (FontFamily)FindResource("IconFont")
                };
                chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                Crumbs.Children.Add(chevron);
            }
            string target = parts[i].Target;
            var button = new Button { Content = parts[i].Label, Style = (Style)FindResource("CrumbButton") };
            if (i == parts.Count - 1) button.FontWeight = FontWeights.SemiBold;
            button.Click += (_, _) => _ = NavigateAsync(target);
            Crumbs.Children.Add(button);
        }
        Dispatcher.InvokeAsync(CrumbScroll.ScrollToRightEnd, DispatcherPriority.Loaded);
    }

    // =====================================================================================
    //  Sidebar
    // =====================================================================================

    private void BuildQuickAccess()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new (string Glyph, string Label, string Path)[]
        {
            ("", "Home", profile),
            ("", "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("", "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("", "Downloads", Path.Combine(profile, "Downloads")),
            ("", "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("", "Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("", "Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };
        QuickList.ItemsSource = candidates
            .Where(c => c.Path.Length > 0 && Directory.Exists(c.Path))
            .Select(c => new NavItem { Glyph = c.Glyph, Label = c.Label, Path = c.Path })
            .ToList();
    }

    private void BuildPinned()
    {
        var items = _settings.Pinned
            .Select(p => new NavItem { Glyph = "", Label = DisplayName(p), Path = p, IsPinned = true })
            .ToList();
        PinnedList.ItemsSource = items;
        PinnedHeader.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DisplayName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }

    private async Task BuildDrivesAsync()
    {
        List<NavItem> drives;
        try
        {
            // Network or sleeping drives can be slow to answer, so stay off the UI thread.
            drives = await Task.Run(() =>
            {
                var list = new List<NavItem>();
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        string letter = drive.Name.TrimEnd('\\');
                        string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                        long total = drive.TotalSize, free = drive.AvailableFreeSpace;
                        list.Add(new NavItem
                        {
                            Glyph = drive.DriveType switch
                            {
                                DriveType.Removable => "",
                                DriveType.Network => "",
                                DriveType.CDRom => "",
                                _ => ""
                            },
                            Label = $"{label} ({letter})",
                            Path = drive.Name,
                            IsDrive = true,
                            Sub = $"{Format.Bytes(free)} free of {Format.Bytes(total)}",
                            Fraction = total > 0 ? 1.0 - (double)free / total : 0
                        });
                    }
                    catch { /* drive disappeared mid-query */ }
                }
                return list;
            });
        }
        catch { return; }

        // Only touch the UI when something changed, so sidebar focus isn't disturbed.
        string signature = string.Join("|", drives.Select(d => d.Label + d.Sub));
        if (signature == _driveSignature) return;
        _driveSignature = signature;
        bool hadFocus = DriveList.IsKeyboardFocusWithin;
        DriveList.ItemsSource = drives;
        if (hadFocus) Dispatcher.InvokeAsync(FocusSidebar, DispatcherPriority.Loaded);
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NavItem item) _ = NavigateAsync(item.Path);
    }

    private void Nav_RightUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NavItem { IsPinned: true } item)
        {
            ShowPinnedMenu(item);
            e.Handled = true;
        }
    }

    private void ShowPinnedMenu(NavItem item) => ShowMenu(item.Label, new[]
    {
        new MenuEntry("", "Open", () => _ = NavigateAsync(item.Path)),
        new MenuEntry("", "Unpin from sidebar", () => { _settings.Pinned.Remove(item.Path); _settings.Save(); BuildPinned(); }),
    }, item.Path);

    private void ToggleSidebar()
    {
        _settings.ShowSidebar = !_settings.ShowSidebar;
        Sidebar.Visibility = _settings.ShowSidebar ? Visibility.Visible : Visibility.Collapsed;
        if (!_settings.ShowSidebar) FocusFiles();
    }

    private void FocusSidebar()
    {
        if (Sidebar.Visibility != Visibility.Visible) return;
        Sidebar.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    // =====================================================================================
    //  Input: gamepad, keyboard, touch / mouse
    // =====================================================================================

    private void OnPad(PadButton button)
    {
        if (!IsActive) return; // never react while a game or another app has focus

        if (_keyboard != null)
        {
            switch (button)
            {
                case PadButton.X: _keyboard.Backspace(); return;
                case PadButton.Y: _keyboard.Append(" "); return;
                case PadButton.LS: _keyboard.ToggleShift(); return;
                case PadButton.Menu: _keyboard.Confirm(); return;
            }
        }

        switch (button)
        {
            case PadButton.Up: Nav(FocusNavigationDirection.Up); return;
            case PadButton.Down: Nav(FocusNavigationDirection.Down); return;
            case PadButton.Left: Nav(FocusNavigationDirection.Left); return;
            case PadButton.Right: Nav(FocusNavigationDirection.Right); return;
            case PadButton.A: Activate(); return;
            case PadButton.B: Back(); return;
        }

        if (OverlayOpen) return;

        switch (button)
        {
            case PadButton.X: ShowActionMenu(); break;
            case PadButton.Y: ToggleCheck(); break;
            case PadButton.LB: GoBack(); break;
            case PadButton.RB: GoForward(); break;
            case PadButton.LT: MovePage(-1); break;
            case PadButton.RT: MovePage(1); break;
            case PadButton.View: ToggleView(); break;
            case PadButton.Menu: ShowMainMenu(); break;
            case PadButton.LS: ToggleSidebar(); break;
            case PadButton.RS: PromptFilter(); break;
        }
    }

    /// <summary>D-pad / stick / arrow-key movement.</summary>
    private void Nav(FocusNavigationDirection direction)
    {
        var focused = Keyboard.FocusedElement as UIElement;

        if (OverlayOpen)
        {
            if (focused != null && focused.IsVisible && OverlayHost.IsKeyboardFocusWithin)
                focused.MoveFocus(new TraversalRequest(direction));
            else
                OverlayCard.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }

        if (FileList.IsKeyboardFocusWithin) { MoveSelection(direction); return; }
        if (focused == null || !focused.IsVisible) { FocusFiles(); return; }
        if (direction == FocusNavigationDirection.Right && Sidebar.IsKeyboardFocusWithin) { FocusFiles(); return; }
        if (direction == FocusNavigationDirection.Down && TopBar.IsKeyboardFocusWithin) { FocusFiles(); return; }
        focused.MoveFocus(new TraversalRequest(direction));
    }

    /// <summary>The A button: press the focused button or open the selected item.</summary>
    private void Activate()
    {
        if (Keyboard.FocusedElement is Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return;
        }
        if (OverlayOpen) return;
        if (FileList.IsKeyboardFocusWithin) OpenSelected();
        else FocusFiles();
    }

    /// <summary>The B button / Escape: cancel, close, clear, then go back.</summary>
    private void Back()
    {
        if (_operation != null) { _operation.Cancel(); return; }
        if (OverlayOpen)
        {
            if (_keyboard != null) _keyboard.Cancel();
            else if (_overlayDismissable) CloseOverlay();
            return;
        }
        if (_all.Any(i => i.IsChecked)) { ClearChecks(); return; }
        if (_filter.Length > 0) { _filter = ""; ShowItems(); return; }
        if (!FileList.IsKeyboardFocusWithin) { FocusFiles(); return; }
        GoBack();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;

        if (OverlayOpen)
        {
            // Menus and the on-screen keyboard use WPF's own arrow / Enter handling.
            if (key == Key.Escape) { Back(); e.Handled = true; }
            return;
        }

        bool inFiles = FileList.IsKeyboardFocusWithin;
        bool handled = true;

        if (mods == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.Up: Nav(FocusNavigationDirection.Up); break;
                case Key.Down: Nav(FocusNavigationDirection.Down); break;
                case Key.Left: Nav(FocusNavigationDirection.Left); break;
                case Key.Right: Nav(FocusNavigationDirection.Right); break;
                case Key.Escape: Back(); break;
                case Key.Back: GoBack(); break;
                case Key.BrowserBack: GoBack(); break;
                case Key.BrowserForward: GoForward(); break;
                case Key.F5: _ = RefreshAsync(); break;
                case Key.F11: ToggleFullscreen(); break;
                case Key.F2: Rename(); break;
                case Key.F10: ShowMainMenu(); break;
                case Key.Apps: ShowActionMenu(); break;
                case Key.Delete: ConfirmDelete(); break;
                case Key.Enter when inFiles: OpenSelected(); break;
                case Key.Space when inFiles: ToggleCheck(); break;
                case Key.PageUp when inFiles: MovePage(-1); break;
                case Key.PageDown when inFiles: MovePage(1); break;
                case Key.Home when inFiles: SelectIndex(0); break;
                case Key.End when inFiles: SelectIndex(_visible.Count - 1); break;
                default: handled = false; break;
            }
        }
        else if (mods == ModifierKeys.Alt)
        {
            switch (key)
            {
                case Key.Left: GoBack(); break;
                case Key.Right: GoForward(); break;
                case Key.Up: GoUp(); break;
                case Key.Enter: ShowProperties(Targets()); break;
                default: handled = false; break;
            }
        }
        else if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.C: SetClipboard(cut: false); break;
                case Key.X: SetClipboard(cut: true); break;
                case Key.V: _ = PasteAsync(); break;
                case Key.A: SelectAll(); break;
                case Key.F: PromptFilter(); break;
                case Key.H: ToggleHidden(); break;
                default: handled = false; break;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N)
        {
            NewFolder();
        }
        else
        {
            handled = false;
        }

        e.Handled = handled;
    }

    // ---- moving the cursor inside the file list -------------------------------------------

    private void FocusFiles() => SelectIndex(Math.Max(0, FileList.SelectedIndex));

    private void SelectIndex(int index)
    {
        if (_visible.Count == 0) { FileList.Focus(); return; }
        index = Math.Clamp(index, 0, _visible.Count - 1);
        FileList.SelectedIndex = index;
        FileList.ScrollIntoView(_visible[index]);
        FileList.UpdateLayout();
        if (FileList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container) container.Focus();
        else FileList.Focus();
    }

    /// <summary>How many tiles fit in one row right now (1 in list view).</summary>
    private int Columns()
    {
        if (!_gridActive || _visible.Count == 0) return 1;
        if (FileList.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement first || first.ActualWidth <= 0) return 1;
        if (VisualTreeHelper.GetParent(first) is not FrameworkElement panel) return 1;
        double itemWidth = first.ActualWidth + first.Margin.Left + first.Margin.Right;
        return Math.Max(1, (int)Math.Floor((panel.ActualWidth + 0.5) / itemWidth));
    }

    private void MoveSelection(FocusNavigationDirection direction)
    {
        int count = _visible.Count;
        if (count == 0)
        {
            if (direction == FocusNavigationDirection.Left) FocusSidebar();
            else if (direction == FocusNavigationDirection.Up) BackButton.Focus();
            return;
        }

        int columns = Columns();
        int index = Math.Max(0, FileList.SelectedIndex);
        int column = index % columns;

        switch (direction)
        {
            case FocusNavigationDirection.Left:
                if (column == 0) { FocusSidebar(); return; }
                index--;
                break;
            case FocusNavigationDirection.Right:
                if (column == columns - 1 || index == count - 1) return;
                index++;
                break;
            case FocusNavigationDirection.Up:
                if (index < columns) { BackButton.Focus(); return; }
                index -= columns;
                break;
            case FocusNavigationDirection.Down:
                if (index + columns < count) index += columns;
                else if (index / columns < (count - 1) / columns) index = count - 1; // drop into a short last row
                else return;
                break;
        }
        SelectIndex(index);
    }

    private void MovePage(int direction)
    {
        if (_visible.Count == 0) return;
        if (!FileList.IsKeyboardFocusWithin) FocusFiles();

        int index = Math.Max(0, FileList.SelectedIndex);
        double itemHeight = 60;
        if (FileList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container && container.ActualHeight > 0)
            itemHeight = container.ActualHeight + container.Margin.Top + container.Margin.Bottom;
        int rows = Math.Max(1, (int)(FileList.ActualHeight / itemHeight) - 1);
        SelectIndex(index + direction * rows * Columns());
    }

    // ---- touch / mouse on items ---------------------------------------------------------------

    private void Item_LeftUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: FileItem item }) return;

        if (_all.Any(i => i.IsChecked))
        {
            // Selection mode: taps toggle check marks.
            item.IsChecked = !item.IsChecked;
            UpdateStatus();
        }
        else if (_settings.SingleTapOpen)
        {
            Dispatcher.InvokeAsync(() => Open(item));
        }
    }

    private void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_settings.SingleTapOpen || e.ChangedButton != MouseButton.Left) return;
        if (sender is ListBoxItem { DataContext: FileItem item } && !_all.Any(i => i.IsChecked)) Open(item);
    }

    /// <summary>Right-click, or press-and-hold on a touch screen.</summary>
    private void Item_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: FileItem item }) return;
        FileList.SelectedItem = item;
        e.Handled = true;
        ShowActionMenu();
    }

    private void Overlay_BackgroundDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, OverlayHost)) Back();
    }

    /// <summary>Stops WPF from nudging the whole window when a touch scroll hits the end of a list.</summary>
    private void Window_ManipulationBoundaryFeedback(object sender, ManipulationBoundaryFeedbackEventArgs e) => e.Handled = true;

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();
    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();
    private void Search_Click(object sender, RoutedEventArgs e) => PromptFilter();
    private void View_Click(object sender, RoutedEventArgs e) => ToggleView();
    private void Menu_Click(object sender, RoutedEventArgs e) => ShowMainMenu();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    // =====================================================================================
    //  Selection and file actions
    // =====================================================================================

    /// <summary>Checked items if there are any, otherwise the item under the cursor.</summary>
    private List<FileItem> Targets()
    {
        var checkedItems = _all.Where(i => i.IsChecked).ToList();
        if (checkedItems.Count > 0) return checkedItems;
        return FileList.SelectedItem is FileItem item ? new List<FileItem> { item } : new List<FileItem>();
    }

    private void ToggleCheck()
    {
        if (FileList.SelectedItem is not FileItem item) return;
        item.IsChecked = !item.IsChecked;
        UpdateStatus();
    }

    private void SelectAll()
    {
        foreach (var item in _visible) item.IsChecked = true;
        UpdateStatus();
    }

    private void ClearChecks()
    {
        foreach (var item in _all) item.IsChecked = false;
        UpdateStatus();
    }

    private void OpenSelected()
    {
        if (FileList.SelectedItem is FileItem item) Open(item);
    }

    private void Open(FileItem item)
    {
        if (item.IsFolder) { _ = NavigateAsync(item.FullPath); return; }
        try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast("Can't open: " + ex.Message); }
    }

    private void SetClipboard(bool cut)
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        _clipboard = targets.Select(t => t.FullPath).ToList();
        _clipboardIsCut = cut;
        ClearChecks();
        ShowToast($"{Plural(targets.Count)} ready to {(cut ? "move" : "copy")}. Open the destination and choose Paste.");
    }

    private async Task PasteAsync()
    {
        if (_clipboard.Count == 0) { ShowToast("Nothing to paste"); return; }
        var sources = _clipboard.ToList();
        bool move = _clipboardIsCut;
        string destination = _path;

        bool ok = await RunOperationAsync(move ? "Moving…" : "Copying…",
            (progress, ct) => FileOps.TransferAsync(sources, destination, move, progress, ct));
        if (ok)
        {
            if (move) _clipboard.Clear();
            ShowToast($"{Plural(sources.Count)} {(move ? "moved" : "copied")}");
            UpdateStatus();
        }
    }

    private void ConfirmDelete()
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        string title = targets.Count == 1 ? $"Delete “{targets[0].Name}”?" : $"Delete {targets.Count} items?";
        ShowMenu(title, new[]
        {
            new MenuEntry("", "Cancel", () => { }),
            new MenuEntry("", "Delete", () => _ = DeleteAsync(targets)),
        }, "Items go to the Recycle Bin where the drive has one.");
    }

    private async Task DeleteAsync(List<FileItem> targets)
    {
        var paths = targets.Select(t => t.FullPath).ToList();
        if (await RunOperationAsync("Deleting…", (_, _) => FileOps.DeleteAsync(paths), cancellable: false))
            ShowToast($"{Plural(paths.Count)} deleted");
    }

    private void Rename()
    {
        if (FileList.SelectedItem is not FileItem item) return;
        Prompt("Rename", item.Name, async name =>
        {
            if (name.Length == 0 || name == item.Name) return;
            if (!IsValidName(name)) return;
            try
            {
                string target = Path.Combine(_path, name);
                if (item.IsFolder) Directory.Move(item.FullPath, target);
                else File.Move(item.FullPath, target);
            }
            catch (Exception ex) { ShowToast("Rename failed: " + ex.Message); }
            await RefreshAsync(name);
        });
    }

    private void NewFolder()
    {
        if (_path.Length == 0) return;
        Prompt("New folder", "New folder", async name =>
        {
            if (name.Length == 0 || !IsValidName(name)) return;
            string target = FileOps.UniquePath(_path, name);
            try { Directory.CreateDirectory(target); }
            catch (Exception ex) { ShowToast("Couldn't create the folder: " + ex.Message); }
            await RefreshAsync(Path.GetFileName(target));
        });
    }

    private bool IsValidName(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.Trim('.', ' ').Length > 0) return true;
        ShowToast("That name contains characters Windows doesn't allow ( \\ / : * ? \" < > | )");
        return false;
    }

    private async Task ZipAsync(List<FileItem> targets)
    {
        string baseName = targets.Count == 1 ? Path.GetFileNameWithoutExtension(targets[0].Name) : "Archive";
        string zipPath = FileOps.UniquePath(_path, baseName + ".zip");
        var sources = targets.Select(t => t.FullPath).ToList();
        if (await RunOperationAsync("Compressing…", (_, ct) => FileOps.ZipAsync(sources, zipPath, ct)))
        {
            ClearChecks();
            await RefreshAsync(Path.GetFileName(zipPath));
        }
    }

    private async Task UnzipAsync(FileItem zip)
    {
        string destination = FileOps.UniquePath(_path, Path.GetFileNameWithoutExtension(zip.Name));
        if (await RunOperationAsync("Extracting…", (_, _) => FileOps.UnzipAsync(zip.FullPath, destination), cancellable: false))
            await RefreshAsync(Path.GetFileName(destination));
    }

    private void TogglePin(string path)
    {
        var existing = _settings.Pinned.FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _settings.Pinned.Remove(existing);
        else _settings.Pinned.Add(path);
        _settings.Save();
        BuildPinned();
        ShowToast(existing != null ? "Unpinned" : "Pinned to the sidebar");
    }

    private bool IsPinned(string path) =>
        _settings.Pinned.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    private void CopyPathToClipboard(string path)
    {
        try { Clipboard.SetText(path); ShowToast("Path copied"); }
        catch { ShowToast("Couldn't reach the clipboard"); }
    }

    private void RunProcess(string file, string arguments)
    {
        try { Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast(ex.Message); }
    }

    /// <summary>Runs a long file operation behind a progress dialog. Returns true if it finished.</summary>
    private async Task<bool> RunOperationAsync(string title,
        Func<IProgress<TransferProgress>, CancellationToken, Task> work, bool cancellable = true)
    {
        if (_operation != null) return false;
        var cts = new CancellationTokenSource();
        _operation = cts;

        var bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 1, Height = 8, Margin = new Thickness(0, 14, 0, 8) };
        var detail = new TextBlock { Text = " ", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("MenuButton"), Margin = new Thickness(0, 12, 0, 0), IsEnabled = cancellable };
        cancel.Click += (_, _) => cts.Cancel();

        var panel = new StackPanel { Width = 380 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(bar);
        panel.Children.Add(detail);
        panel.Children.Add(cancel);
        ShowOverlay(panel, cancel, dismissable: false);

        var progress = new Progress<TransferProgress>(p =>
        {
            bar.IsIndeterminate = false;
            bar.Value = p.Fraction;
            detail.Text = p.Name;
        });

        bool completed = false;
        try
        {
            await work(progress, cts.Token);
            completed = true;
        }
        catch (OperationCanceledException) { ShowToast("Cancelled"); }
        catch (Exception ex) { ShowToast(ex.Message); }
        finally
        {
            _operation = null;
            cts.Dispose();
            CloseOverlay();
        }
        await RefreshAsync();
        return completed;
    }

    // =====================================================================================
    //  Menus
    // =====================================================================================

    private void ShowActionMenu()
    {
        // X on a pinned sidebar entry offers to unpin it.
        if ((Keyboard.FocusedElement as FrameworkElement)?.DataContext is NavItem { IsPinned: true } nav && Sidebar.IsKeyboardFocusWithin)
        {
            ShowPinnedMenu(nav);
            return;
        }

        var targets = Targets();
        if (targets.Count == 0) { ShowMainMenu(); return; }

        var entries = new List<MenuEntry>();
        bool single = targets.Count == 1;
        var first = targets[0];

        if (single)
        {
            entries.Add(new MenuEntry("", "Open", () => Open(first)));
            if (!first.IsFolder)
                entries.Add(new MenuEntry("", "Open with…", () => RunProcess("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + first.FullPath)));
            entries.Add(new MenuEntry("", first.IsChecked ? "Deselect" : "Select", () => { first.IsChecked = !first.IsChecked; UpdateStatus(); }));
        }
        else
        {
            entries.Add(new MenuEntry("", "Clear selection", ClearChecks));
        }

        entries.Add(new MenuEntry("", "Copy", () => SetClipboard(cut: false)));
        entries.Add(new MenuEntry("", "Cut (move)", () => SetClipboard(cut: true)));
        if (_clipboard.Count > 0)
            entries.Add(new MenuEntry("", $"Paste {Plural(_clipboard.Count)} here", () => _ = PasteAsync()));
        if (single) entries.Add(new MenuEntry("", "Rename", Rename));
        entries.Add(new MenuEntry("", "Delete", ConfirmDelete));
        entries.Add(new MenuEntry("", "Compress to ZIP", () => _ = ZipAsync(targets)));

        if (single && !first.IsFolder && first.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            entries.Add(new MenuEntry("", "Extract to a new folder", () => _ = UnzipAsync(first)));
        if (single && first.IsFolder)
            entries.Add(new MenuEntry(IsPinned(first.FullPath) ? "" : "",
                IsPinned(first.FullPath) ? "Unpin from sidebar" : "Pin to sidebar", () => TogglePin(first.FullPath)));
        if (single)
        {
            entries.Add(new MenuEntry("", "Copy path", () => CopyPathToClipboard(first.FullPath)));
            entries.Add(new MenuEntry("", "Show in Windows Explorer", () => RunProcess("explorer.exe", $"/select,\"{first.FullPath}\"")));
        }
        entries.Add(new MenuEntry("", "Properties", () => ShowProperties(targets)));

        ShowMenu(single ? first.Name : $"{targets.Count} items selected", entries);
    }

    private void ShowMainMenu()
    {
        var entries = new List<MenuEntry>
        {
            new("", "New folder", NewFolder),
        };
        if (_clipboard.Count > 0)
            entries.Add(new MenuEntry("", $"Paste {Plural(_clipboard.Count)} here", () => _ = PasteAsync()));
        entries.Add(new MenuEntry("", "Select all", SelectAll));
        if (_all.Any(i => i.IsChecked)) entries.Add(new MenuEntry("", "Clear selection", ClearChecks));
        entries.Add(new MenuEntry("", _filter.Length > 0 ? $"Change filter (“{_filter}”)" : "Filter this folder", PromptFilter));
        entries.Add(new MenuEntry("", $"Sort by: {_settings.SortBy}, {(_settings.SortDescending ? "descending" : "ascending")}", ShowSortMenu));
        entries.Add(new MenuEntry(_settings.GridView ? "" : "", _settings.GridView ? "Switch to list view" : "Switch to grid view", ToggleView));
        entries.Add(new MenuEntry("", _settings.ShowHidden ? "Hide hidden items" : "Show hidden items", ToggleHidden));
        entries.Add(new MenuEntry(IsPinned(_path) ? "" : "", IsPinned(_path) ? "Unpin this folder" : "Pin this folder to the sidebar", () => TogglePin(_path)));
        entries.Add(new MenuEntry("", "Refresh", () => _ = RefreshAsync()));
        entries.Add(new MenuEntry("", _fullscreen ? "Exit full screen" : "Full screen", ToggleFullscreen));
        entries.Add(new MenuEntry("", "Settings", ShowSettings));
        entries.Add(new MenuEntry("", "Close Explorer", Close));
        ShowMenu("Menu", entries, _path);
    }

    private void ShowSortMenu()
    {
        MenuEntry By(SortKey key, string label) => new(_settings.SortBy == key ? "" : "", label, () =>
        {
            _settings.SortBy = key;
            ShowItems((FileList.SelectedItem as FileItem)?.Name);
        });

        ShowMenu("Sort by", new[]
        {
            By(SortKey.Name, "Name"),
            By(SortKey.Date, "Date modified"),
            By(SortKey.Size, "Size"),
            By(SortKey.Type, "Type"),
            new MenuEntry("", _settings.SortDescending ? "Order: descending (switch to ascending)" : "Order: ascending (switch to descending)", () =>
            {
                _settings.SortDescending = !_settings.SortDescending;
                ShowItems((FileList.SelectedItem as FileItem)?.Name);
            }),
        });
    }

    private void ShowSettings()
    {
        string theme = _settings.Theme switch
        {
            AppTheme.Dark => "Dark",
            AppTheme.Light => "Light",
            _ => "Follow Windows"
        };

        ShowMenu("Settings", new[]
        {
            new MenuEntry("", $"Theme: {theme}", () =>
            {
                _settings.Theme = (AppTheme)(((int)_settings.Theme + 1) % 3);
                ThemeManager.Apply(_settings.Theme);
                _settings.Save();
                ShowSettings();
            }),
            new MenuEntry("", $"Open items with: {(_settings.SingleTapOpen ? "single tap" : "double tap")}", () =>
            {
                _settings.SingleTapOpen = !_settings.SingleTapOpen;
                _settings.Save();
                ShowSettings();
            }),
            new MenuEntry("", $"Start in full screen: {(_settings.StartFullscreen ? "on" : "off")}", () =>
            {
                _settings.StartFullscreen = !_settings.StartFullscreen;
                _settings.Save();
                ShowSettings();
            }),
            new MenuEntry("", "Controls", ShowControlsHelp),
            new MenuEntry("", "Close", () => { }),
        });
    }

    private void ShowControlsHelp()
    {
        var rows = new (string, string)[]
        {
            ("D-pad / left stick", "Move"),
            ("A", "Open / press"),
            ("B", "Back, cancel, clear selection"),
            ("X", "Actions for the item"),
            ("Y", "Select / deselect (multi-select)"),
            ("LB / RB", "Back / forward"),
            ("LT / RT", "Page up / page down"),
            ("View", "Grid or list"),
            ("Menu", "Main menu"),
            ("Left stick click", "Show or hide the sidebar"),
            ("Right stick click", "Filter this folder"),
            ("Touch", "Tap to open, press and hold for actions"),
            ("Keyboard", "Arrows, Enter, Backspace, Space, F2, Del, Ctrl+C/X/V/A/F/H, F5, F10, F11"),
        };
        ShowInfo("Controls", rows.Select(r => (r.Item1, new TextBlock { Text = r.Item2, TextWrapping = TextWrapping.Wrap })));
    }

    private void ShowProperties(List<FileItem> targets)
    {
        if (targets.Count == 0) return;

        var sizeText = new TextBlock { Text = "Calculating…" };
        var rows = new List<(string, TextBlock)>();
        TextBlock Value(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

        if (targets.Count == 1)
        {
            var item = targets[0];
            rows.Add(("Type", Value(item.TypeText)));
            rows.Add(("Location", Value(_path)));
            rows.Add(("Size", sizeText));
            rows.Add(("Modified", Value(item.Modified.ToString("f"))));
            try
            {
                rows.Add(("Created", Value(File.GetCreationTime(item.FullPath).ToString("f"))));
                rows.Add(("Attributes", Value(File.GetAttributes(item.FullPath).ToString())));
            }
            catch { /* item vanished */ }
        }
        else
        {
            rows.Add(("Items", Value($"{targets.Count(t => t.IsFolder)} folders, {targets.Count(t => !t.IsFolder)} files")));
            rows.Add(("Location", Value(_path)));
            rows.Add(("Total size", sizeText));
        }

        ShowInfo(targets.Count == 1 ? targets[0].Name : $"{targets.Count} items", rows);

        var paths = targets.Select(t => t.FullPath).ToList();
        _ = Task.Run(() => paths.Sum(p => FileOps.SizeOf(p))).ContinueWith(
            t => sizeText.Text = t.IsCompletedSuccessfully ? $"{Format.Bytes(t.Result)} ({t.Result:N0} bytes)" : "Unknown",
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ToggleView()
    {
        _settings.GridView = !_settings.GridView;
        ShowItems((FileList.SelectedItem as FileItem)?.Name);
        if (_settings.GridView && !_gridActive)
            ShowToast("This folder is very large, so it stays in list view");
    }

    private void ToggleHidden()
    {
        _settings.ShowHidden = !_settings.ShowHidden;
        _ = RefreshAsync();
    }

    private void PromptFilter() => Prompt("Filter this folder", _filter, text =>
    {
        _filter = text;
        ShowItems();
    });

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.Normal; // required so the borderless window covers the taskbar
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullscreen;
        }
    }

    // =====================================================================================
    //  Overlay plumbing (menus, dialogs, on-screen keyboard), toast, status
    // =====================================================================================

    private bool OverlayOpen => OverlayHost.Visibility == Visibility.Visible;

    private void ShowOverlay(UIElement content, UIElement? focus, bool dismissable = true)
    {
        _overlayDismissable = dismissable;
        OverlayContent.Content = content;
        OverlayHost.Visibility = Visibility.Visible;
        Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(OverlayContent.Content, content)) return; // already replaced or closed
            if (focus != null) focus.Focus();
            else OverlayCard.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, DispatcherPriority.Loaded);
    }

    private void CloseOverlay()
    {
        if (!OverlayOpen) return;
        OverlayHost.Visibility = Visibility.Collapsed;
        OverlayContent.Content = null;
        _keyboard = null;
        Dispatcher.InvokeAsync(() => { if (!OverlayOpen) FocusFiles(); }, DispatcherPriority.Loaded);
    }

    private double OverlayMaxContentHeight => Math.Max(160, ActualHeight - 200);

    private StackPanel Titled(string title, string? subtitle)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 6, subtitle == null ? 12 : 2)
        });
        if (subtitle != null)
        {
            var sub = new TextBlock { Text = subtitle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 6, 12) };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(sub);
        }
        return panel;
    }

    private void ShowMenu(string title, IEnumerable<MenuEntry> entries, string? subtitle = null)
    {
        var iconFont = (FontFamily)FindResource("IconFont");
        var list = new StackPanel();
        Button? first = null;

        foreach (var entry in entries)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = entry.Glyph, FontFamily = iconFont, FontSize = 18, Width = 36, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = entry.Label, VerticalAlignment = VerticalAlignment.Center });

            var button = new Button { Content = row, Style = (Style)FindResource("MenuButton") };
            button.Click += (_, _) =>
            {
                CloseOverlay();
                entry.Action();
            };
            list.Children.Add(button);
            first ??= button;
        }

        var panel = Titled(title, subtitle);
        panel.Width = Math.Min(420, Math.Max(280, ActualWidth - 80));
        panel.Children.Add(new ScrollViewer
        {
            Content = list,
            Focusable = false,
            MaxHeight = OverlayMaxContentHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly
        });
        ShowOverlay(panel, first);
    }

    /// <summary>A read-only label/value sheet with a Close button.</summary>
    private void ShowInfo(string title, IEnumerable<(string Label, TextBlock Value)> rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int index = 0;
        foreach (var (label, value) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Margin = new Thickness(6, 5, 18, 5) };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            value.Margin = new Thickness(0, 5, 6, 5);
            Grid.SetRow(name, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
            index++;
        }

        var close = new Button { Content = "Close", Style = (Style)FindResource("MenuButton"), Margin = new Thickness(0, 12, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => CloseOverlay();

        var panel = Titled(title, null);
        panel.Width = Math.Min(520, Math.Max(280, ActualWidth - 80));
        panel.Children.Add(new ScrollViewer
        {
            Content = grid,
            Focusable = false,
            MaxHeight = OverlayMaxContentHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly
        });
        panel.Children.Add(close);
        ShowOverlay(panel, close);
    }

    /// <summary>Asks for text with the built-in on-screen keyboard.</summary>
    private void Prompt(string title, string initial, Action<string> onDone)
    {
        var keyboard = new OnScreenKeyboard(title, initial) { Width = Math.Min(720, Math.Max(320, ActualWidth - 80)) };
        keyboard.Confirmed += text =>
        {
            CloseOverlay();
            onDone(text);
        };
        keyboard.Cancelled += CloseOverlay;
        ShowOverlay(keyboard, null, dismissable: true);
        _keyboard = keyboard;
        Dispatcher.InvokeAsync(() => { if (ReferenceEquals(_keyboard, keyboard)) keyboard.FocusFirstKey(); }, DispatcherPriority.Loaded);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void UpdateStatus()
    {
        var parts = new List<string> { Plural(_visible.Count) };
        int checkedCount = _all.Count(i => i.IsChecked);
        if (checkedCount > 0) parts.Add($"{checkedCount} selected");
        if (_filter.Length > 0) parts.Add($"filter “{_filter}”");
        if (_clipboard.Count > 0) parts.Add($"{Plural(_clipboard.Count)} ready to {(_clipboardIsCut ? "move" : "copy")}");
        StatusText.Text = string.Join("   ·   ", parts);
    }

    private static string Plural(int count) => count == 1 ? "1 item" : $"{count} items";
}
