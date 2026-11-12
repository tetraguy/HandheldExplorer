using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HandheldExplorer.Controls;

/// <summary>
/// Built-in text entry that works with the D-pad / stick, touch and a physical keyboard,
/// so renaming and searching never depend on the Windows touch keyboard popping up.
/// </summary>
public sealed class OnScreenKeyboard : UserControl
{
    private static readonly string[] LetterRows = { "1234567890", "qwertyuiop", "asdfghjkl-", "zxcvbnm_.," };
    private static readonly string[] SymbolRows = { "1234567890", "!@#$%^&()+", "=[]{};'~`-", "_.,:?*<>|/" };

    private readonly TextBlock _display;
    private readonly List<Button> _charKeys = new();
    private readonly Button _shiftKey;
    private readonly Button _symbolKey;
    private string _text;
    private bool _shift;
    private bool _symbols;

    public event Action<string>? Confirmed;
    public event Action? Cancelled;

    public OnScreenKeyboard(string title, string initialText)
    {
        _text = initialText;
        Focusable = false;
        Width = 720;

        var root = new StackPanel();

        root.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 10) });

        _display = new TextBlock { FontSize = 20, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var displayBox = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(3, 0, 3, 10),
            MinHeight = 52,
            Child = _display
        };
        displayBox.SetResourceReference(Border.BackgroundProperty, "CardBg");
        displayBox.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        root.Children.Add(displayBox);

        for (int r = 0; r < LetterRows.Length; r++)
        {
            var row = new UniformGrid { Rows = 1, Columns = 10 };
            for (int c = 0; c < 10; c++)
            {
                var key = MakeKey("");
                key.Click += (s, _) => Append((string)((Button)s).Content);
                _charKeys.Add(key);
                row.Children.Add(key);
            }
            root.Children.Add(row);
        }

        var bottom = new Grid();
        double[] widths = { 1.5, 1.5, 3.5, 1.5, 1.5, 2, 2 };
        foreach (var w in widths) bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });

        _shiftKey = MakeKey("", icon: true);      // Shift
        _shiftKey.Click += (_, _) => ToggleShift();
        _symbolKey = MakeKey("?123");
        _symbolKey.FontSize = 15;
        _symbolKey.Click += (_, _) => ToggleSymbols();
        var space = MakeKey("space");
        space.FontSize = 15;
        space.Click += (_, _) => Append(" ");
        var back = MakeKey("", icon: true);       // Backspace
        back.Click += (_, _) => Backspace();
        var clear = MakeKey("Clear");
        clear.FontSize = 15;
        clear.Click += (_, _) => { _text = ""; Refresh(); };
        var cancel = MakeKey("Cancel");
        cancel.FontSize = 15;
        cancel.Click += (_, _) => Cancel();
        var done = MakeKey("Done");
        done.FontSize = 15;
        done.FontWeight = FontWeights.SemiBold;
        done.SetResourceReference(ForegroundProperty, "AccentBrush");
        done.Click += (_, _) => Confirm();

        Button[] bottomKeys = { _shiftKey, _symbolKey, space, back, clear, cancel, done };
        for (int i = 0; i < bottomKeys.Length; i++)
        {
            Grid.SetColumn(bottomKeys[i], i);
            bottom.Children.Add(bottomKeys[i]);
        }
        root.Children.Add(bottom);

        var hint = new TextBlock
        {
            Text = "X  Backspace      Y  Space      L-stick click  Shift      Menu  Done      B  Cancel",
            FontSize = 12,
            Margin = new Thickness(4, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        root.Children.Add(hint);

        Content = root;
        Refresh();
    }

    /// <summary>Puts gamepad focus on the first letter key.</summary>
    public void FocusFirstKey() => _charKeys[10].Focus();

    public void Append(string value)
    {
        _text += value;
        if (_shift && !_symbols) { _shift = false; }   // shift applies to one letter
        Refresh();
    }

    public void Backspace()
    {
        if (_text.Length == 0) return;
        // Remove a whole surrogate pair if the last character is one.
        int cut = _text.Length >= 2 && char.IsLowSurrogate(_text[^1]) ? 2 : 1;
        _text = _text[..^cut];
        Refresh();
    }

    public void ToggleShift() { _shift = !_shift; Refresh(); }
    public void ToggleSymbols() { _symbols = !_symbols; Refresh(); }
    public void Confirm() => Confirmed?.Invoke(_text.Trim());
    public void Cancel() => Cancelled?.Invoke();

    private Button MakeKey(string label, bool icon = false)
    {
        var key = new Button { Content = label };
        key.SetResourceReference(StyleProperty, "KeyButton");
        if (icon) key.FontFamily = (FontFamily)Application.Current.FindResource("IconFont");
        return key;
    }

    private void Refresh()
    {
        _display.Text = _text + "▏"; // thin caret
        var rows = _symbols ? SymbolRows : LetterRows;
        for (int i = 0; i < _charKeys.Count; i++)
        {
            string ch = rows[i / 10][i % 10].ToString();
            _charKeys[i].Content = _shift && !_symbols ? ch.ToUpperInvariant() : ch;
        }
        _symbolKey.Content = _symbols ? "abc" : "?123";
        if (_shift && !_symbols) _shiftKey.SetResourceReference(ForegroundProperty, "AccentBrush");
        else _shiftKey.SetResourceReference(ForegroundProperty, "TextPrimary");
    }

    // ---- physical keyboard ---------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Back: Backspace(); e.Handled = true; break;
            case Key.Enter: Confirm(); e.Handled = true; break;
            case Key.Space: Append(" "); e.Handled = true; break;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]))
        {
            _text += e.Text;
            Refresh();
            e.Handled = true;
        }
        base.OnPreviewTextInput(e);
    }
}
