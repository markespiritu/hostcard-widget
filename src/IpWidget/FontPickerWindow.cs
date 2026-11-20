using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IpWidget;

/// <summary>
/// Searchable font list. Every selection change is previewed on the widget straight away;
/// OK keeps it, Cancel / Esc / closing the window puts the old font back.
/// </summary>
internal sealed class FontPickerWindow : Window
{
    private const string DefaultLabel = "Default (Segoe UI)";

    private readonly Action<string?> _preview;
    private readonly ListBox _list;
    private readonly TextBox _search;
    private readonly List<string> _all;

    /// <summary>The chosen family (null = default). Only meaningful when DialogResult is true.</summary>
    public string? Chosen { get; private set; }

    public FontPickerWindow(string? current, IEnumerable<string> fonts, Action<string?> preview)
    {
        _preview = preview;
        _all = fonts.ToList();
        Chosen = current;

        Title = "IP Widget – Font";
        Width = 340;
        Height = 480;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;

        _search = new TextBox { Margin = new Thickness(10, 10, 10, 6), Padding = new Thickness(4, 3, 4, 3) };
        _search.TextChanged += (_, _) => Fill(_search.Text);
        _search.PreviewKeyDown += (_, e) =>
        {
            // Arrow keys in the search box move through the list, so you can type then browse.
            if (e.Key is Key.Down or Key.Up && _list.Items.Count > 0)
            {
                int i = Math.Clamp(_list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _list.Items.Count - 1);
                _list.SelectedIndex = i;
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
            }
        };

        _list = new ListBox { Margin = new Thickness(10, 0, 10, 0) };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_list, true);
        _list.ItemTemplate = BuildTemplate();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is string name)
                _preview(name == DefaultLabel ? null : name);
        };
        _list.MouseDoubleClick += (_, _) => Accept();

        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var hint = new TextBlock
        {
            Text = "Type to search · ↑/↓ to browse · the widget updates as you go",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(10, 0, 10, 4),
        };

        var root = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;

        Fill("");
        _list.SelectedItem = current ?? DefaultLabel;
        if (_list.SelectedItem != null) _list.ScrollIntoView(_list.SelectedItem);
        Loaded += (_, _) => _search.Focus();
    }

    private void Fill(string filter)
    {
        var keep = _list.SelectedItem as string;
        _list.Items.Clear();
        if (string.IsNullOrWhiteSpace(filter) || DefaultLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            _list.Items.Add(DefaultLabel);
        foreach (var f in _all)
            if (string.IsNullOrWhiteSpace(filter) || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _list.Items.Add(f);
        if (keep != null && _list.Items.Contains(keep)) _list.SelectedItem = keep;
    }

    private void Accept()
    {
        if (_list.SelectedItem is string name)
            Chosen = name == DefaultLabel ? null : name;
        DialogResult = true;
    }

    /// <summary>Each row shows the font's name in that font.</summary>
    private static DataTemplate BuildTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding { Converter = new NameToFont() });
        text.SetValue(TextBlock.FontSizeProperty, 15.0);
        text.SetValue(TextBlock.PaddingProperty, new Thickness(2, 2, 2, 2));
        return new DataTemplate { VisualTree = text };
    }

    private sealed class NameToFont : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            value is string s && s != DefaultLabel ? new FontFamily(s) : new FontFamily("Segoe UI");
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            throw new NotSupportedException();
    }
}
