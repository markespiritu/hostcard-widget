using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IpWidget;

/// <summary>
/// One dialog for everything about the text: font, per-field size and style, colour, hostname case.
/// It edits the live settings object and calls <c>changed</c> after every tweak, so the widget previews
/// as you go. The caller restores its snapshot if the dialog is cancelled.
/// </summary>
internal sealed class TextSettingsWindow : Window
{
    private const string DefaultFontLabel = "Default (Segoe UI)";

    private readonly WidgetSettings _s;
    private readonly Action _changed;
    private readonly List<string> _fonts;
    private readonly ListBox _fontList;
    private readonly TextBox _search;
    private bool _loading = true;

    public TextSettingsWindow(WidgetSettings settings, IEnumerable<string> fonts, Action changed)
    {
        _s = settings;
        _changed = changed;
        _fonts = fonts.ToList();

        Title = "IP Widget – Text";
        Width = 720;
        Height = 560;
        MinWidth = 600;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        // ---- left: font list ----
        _search = new TextBox { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 4, 0, 6) };
        _search.TextChanged += (_, _) => FillFonts(_search.Text);
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Up && _fontList!.Items.Count > 0)
            {
                _fontList.SelectedIndex = Math.Clamp(_fontList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _fontList.Items.Count - 1);
                _fontList.ScrollIntoView(_fontList.SelectedItem);
                e.Handled = true;
            }
        };

        _fontList = new ListBox();
        VirtualizingPanel.SetIsVirtualizing(_fontList, true);
        VirtualizingPanel.SetVirtualizationMode(_fontList, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_fontList, true);
        _fontList.ItemTemplate = FontRowTemplate();
        _fontList.SelectionChanged += (_, _) =>
        {
            if (_loading || _fontList.SelectedItem is not string name) return;
            _s.FontFamily = name == DefaultFontLabel ? null : name;
            _changed();
        };

        var left = new DockPanel { Margin = new Thickness(12, 10, 8, 0) };
        var fontHeader = Header("Font  (type to search, ↑/↓ to browse)");
        DockPanel.SetDock(fontHeader, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        left.Children.Add(fontHeader);
        left.Children.Add(_search);
        left.Children.Add(_fontList);

        // ---- right: per-field size & style, colour, capitalisation ----
        var right = new StackPanel { Margin = new Thickness(8, 10, 12, 0) };
        right.Children.Add(Header("Size and style per field"));
        right.Children.Add(FieldGrid());

        right.Children.Add(Header("Color", top: 14));
        right.Children.Add(ColorRow());

        right.Children.Add(Header("Hostname capitalization", top: 14));
        right.Children.Add(CaseBox());

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);

        // ---- buttons ----
        var ok = new Button { Content = "OK", Width = 90, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 28, IsCancel = true };
        var hint = new TextBlock
        {
            Text = "Changes show on the widget straight away. Cancel or Esc undoes them.",
            Foreground = Brushes.Gray,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var buttons = new DockPanel { Margin = new Thickness(12, 10, 12, 12), LastChildFill = false };
        DockPanel.SetDock(hint, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(hint);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;

        FillFonts("");
        _fontList.SelectedItem = _s.FontFamily ?? DefaultFontLabel;
        if (_fontList.SelectedItem != null) _fontList.ScrollIntoView(_fontList.SelectedItem);

        // Esc anywhere in the dialog cancels (IsCancel only fires when the dialog has focus).
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
        Loaded += (_, _) =>
        {
            Activate();
            _search.Focus();
            _loading = false;
        };
    }

    // ---------- pieces ----------

    private static TextBlock Header(string text, double top = 0) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, top, 0, 4),
    };

    private Grid FieldGrid()
    {
        var rows = new (string Label, Func<double> GetSize, Action<double> SetSize, Func<FieldStyle> Style)[]
        {
            ("Hostname",    () => _s.HostnameSize, v => _s.HostnameSize = v, () => _s.HostnameStyle ??= new FieldStyle { Weight = "SemiBold" }),
            ("IP address",  () => _s.IpSize,       v => _s.IpSize = v,       () => _s.IpStyle ??= new FieldStyle()),
            ("MAC address", () => _s.MacSize,      v => _s.MacSize = v,      () => _s.MacStyle ??= new FieldStyle()),
            ("Type",        () => _s.TypeSize,     v => _s.TypeSize = v,     () => _s.TypeStyle ??= new FieldStyle()),
            ("Speed",       () => _s.SpeedSize,    v => _s.SpeedSize = v,    () => _s.SpeedStyle ??= new FieldStyle()),
        };

        var g = new Grid();
        foreach (var w in new[] { 92.0, 72, 104, 0, 0 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        AddCell(g, 0, 1, new TextBlock { Text = "Size", Foreground = Brushes.Gray, FontSize = 11 });
        AddCell(g, 0, 2, new TextBlock { Text = "Weight", Foreground = Brushes.Gray, FontSize = 11 });

        int r = 1;
        foreach (var f in rows)
        {
            g.RowDefinitions.Add(new RowDefinition());
            AddCell(g, r, 0, new TextBlock { Text = f.Label, VerticalAlignment = VerticalAlignment.Center });

            var size = new ComboBox { IsEditable = true, Margin = new Thickness(0, 2, 6, 2), Text = $"{f.GetSize():0.#}" };
            foreach (var p in MainWindow.SizePresets) size.Items.Add($"{p:0.#}");
            void CommitSize()
            {
                if (_loading) return;
                if (double.TryParse(size.Text, out var v) && v >= 4 && v <= 200 && Math.Abs(v - f.GetSize()) > 0.001)
                {
                    f.SetSize(v);
                    _changed();
                }
            }
            size.SelectionChanged += (_, _) => { if (size.SelectedItem is string t) { size.Text = t; CommitSize(); } };
            size.LostKeyboardFocus += (_, _) => CommitSize();
            size.KeyUp += (_, e) => { if (e.Key == Key.Enter) CommitSize(); };
            AddCell(g, r, 1, size);

            var weight = new ComboBox { Margin = new Thickness(0, 2, 8, 2) };
            foreach (var w in MainWindow.Weights)
                weight.Items.Add(new ComboBoxItem { Content = w == "SemiBold" ? "Semibold" : w, Tag = w, FontWeight = MainWindow.ToWeight(w) });
            weight.SelectedItem = weight.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string)i.Tag, f.Style().Weight, StringComparison.OrdinalIgnoreCase));
            weight.SelectionChanged += (_, _) =>
            {
                if (_loading || weight.SelectedItem is not ComboBoxItem it) return;
                f.Style().Weight = (string)it.Tag;
                _changed();
            };
            AddCell(g, r, 2, weight);

            var italic = new CheckBox { Content = "Italic", FontStyle = FontStyles.Italic, IsChecked = f.Style().Italic, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            italic.Click += (_, _) => { f.Style().Italic = italic.IsChecked == true; _changed(); };
            AddCell(g, r, 3, italic);

            var underline = new CheckBox { Content = new TextBlock { Text = "Underline", TextDecorations = TextDecorations.Underline }, IsChecked = f.Style().Underline, VerticalAlignment = VerticalAlignment.Center };
            underline.Click += (_, _) => { f.Style().Underline = underline.IsChecked == true; _changed(); };
            AddCell(g, r, 4, underline);
            r++;
        }
        return g;
    }

    private static void AddCell(Grid g, int row, int col, UIElement e)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        g.Children.Add(e);
    }

    private UIElement ColorRow()
    {
        var panel = new WrapPanel { MaxWidth = 400 };
        var swatches = new List<(Border Box, string Hex)>();
        void Mark()
        {
            foreach (var (box, hex) in swatches)
                box.BorderBrush = string.Equals(hex, _s.TextColor, StringComparison.OrdinalIgnoreCase) ? Brushes.DodgerBlue : Brushes.Gray;
        }

        foreach (var (name, hex) in MainWindow.ColorPresets)
        {
            var box = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(MainWindow.ParseColor(hex)),
                BorderThickness = new Thickness(2), ToolTip = name, Cursor = Cursors.Hand,
            };
            box.MouseLeftButtonUp += (_, _) => { _s.TextColor = hex; Mark(); _changed(); };
            swatches.Add((box, hex));
            panel.Children.Add(box);
        }

        var custom = new Button { Content = "Custom…", Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 6) };
        custom.Click += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (NativeMethods.PickColor(hwnd, MainWindow.ParseColor(_s.TextColor)) is Color c)
            {
                _s.TextColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                Mark();
                _changed();
            }
        };
        panel.Children.Add(custom);
        Mark();
        return panel;
    }

    private UIElement CaseBox()
    {
        var box = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (key, label) in new[] { ("upper", "UPPERCASE"), ("title", "Capitalized"), ("lower", "lowercase"), ("asis", "As named in Windows") })
        {
            var item = new ComboBoxItem { Content = $"{label}   ({MainWindow.FormatHostname(key)})", Tag = key };
            box.Items.Add(item);
            if (_s.HostnameCase == key) box.SelectedItem = item;
        }
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedItem is not ComboBoxItem it) return;
            _s.HostnameCase = (string)it.Tag;
            _changed();
        };
        return box;
    }

    private void FillFonts(string filter)
    {
        var keep = _fontList.SelectedItem as string;
        bool wasLoading = _loading;
        _loading = true; // don't treat list rebuilding as a font choice
        _fontList.Items.Clear();
        if (string.IsNullOrWhiteSpace(filter) || DefaultFontLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            _fontList.Items.Add(DefaultFontLabel);
        foreach (var f in _fonts)
            if (string.IsNullOrWhiteSpace(filter) || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _fontList.Items.Add(f);
        if (keep != null && _fontList.Items.Contains(keep)) _fontList.SelectedItem = keep;
        _loading = wasLoading;
    }

    private static DataTemplate FontRowTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding { Converter = new NameToFont() });
        text.SetValue(TextBlock.FontSizeProperty, 15.0);
        text.SetValue(TextBlock.PaddingProperty, new Thickness(2));
        return new DataTemplate { VisualTree = text };
    }

    private sealed class NameToFont : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            value is string s && s != DefaultFontLabel ? new FontFamily(s) : new FontFamily("Segoe UI");
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            throw new NotSupportedException();
    }
}
