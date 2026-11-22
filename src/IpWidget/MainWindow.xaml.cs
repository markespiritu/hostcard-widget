using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace IpWidget;

public partial class MainWindow : Window
{
    private static readonly Brush IdleBorder = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromArgb(0x99, 0x6C, 0xB8, 0xFF));
    private static readonly Brush LightCardBackground = new SolidColorBrush(Color.FromArgb(0xB8, 0xF2, 0xF2, 0xF2));
    private static readonly Brush CardBackground = new SolidColorBrush(Color.FromArgb(0xA6, 0x14, 0x14, 0x14));
    // Text-only mode while Shift+Alt is held: a faint panel so you can see what you're grabbing.
    private static readonly Brush GhostBackground = new SolidColorBrush(Color.FromArgb(0x40, 0x14, 0x14, 0x14));
    private static readonly System.Windows.Media.Effects.DropShadowEffect TextShadow = new()
    {
        Color = Colors.Black, BlurRadius = 3, ShadowDepth = 0, Opacity = 1,
        RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
    };
    private static readonly System.Windows.Media.Effects.DropShadowEffect HaloShadow = new()
    {
        Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
    };

    private readonly int _slot;
    private readonly WidgetSettings _settings;
    private readonly DispatcherTimer _keyTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _debounceTimer;

    private IntPtr _hwnd;
    private bool _interactive;
    private bool _menuOpen;
    private bool _dragging;

    // Resizing: Shift+Alt + mouse wheel, or Shift+Alt + drag the bottom-right corner.
    private const double MinScale = 0.6, MaxScale = 3.0, GripSize = 18;
    private bool _resizing;
    private double _resizeStartScale, _resizeStartWidth, _resizeStartX;

    internal MainWindow(int slot)
    {
        _slot = slot;
        _settings = WidgetSettings.Load(slot);
        InitializeComponent();

        Topmost = _settings.KeepOnTop;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _settings.Left ?? -10000;   // final placement happens in OnLoaded, once the size is known
        Top = _settings.Top ?? -10000;
        Title = slot == 1 ? "IP Widget" : $"IP Widget (slot {slot})";

        // Poll Shift+Alt: holding them makes the widget clickable/draggable, otherwise clicks pass through.
        _keyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => UpdateInteractive(), Dispatcher);
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _debounceTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { _debounceTimer!.Stop(); Refresh(); }, Dispatcher);
        _debounceTimer.Stop();

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

        MouseLeftButtonDown += OnLeftDown;
        MouseRightButtonUp += OnRightUp;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseWheel += OnWheel;
        Loaded += OnLoaded;

        ApplyScale(_settings.Scale);
        ApplyLook();

        StartupShortcut.RefreshIfPresent(slot);
        Refresh();
    }

    // ---------- window plumbing ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetExStyle(_hwnd,
            add: NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED,
            remove: 0);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        SendToBottom();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Pin to the bottom of the z-order so it sits on the desktop under every other window.
        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && !_settings.KeepOnTop && !_forceOnTop)
        {
            var wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
            if ((wp.flags & NativeMethods.SWP_NOZORDER) == 0)
            {
                wp.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    private void SendToBottom()
    {
        if (_hwnd == IntPtr.Zero || _settings.KeepOnTop || _forceOnTop) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceWindow();
        _keyTimer.Start();
        _refreshTimer.Start();
    }

    private void PlaceWindow()
    {
        if (_settings.Left is double l && _settings.Top is double t && IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
            return;
        }
        PlaceDefault();
    }

    private void PlaceDefault()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 24;
        Top = area.Top + 24 + (_slot - 1) * (ActualHeight + 12);
        if (Top + ActualHeight > area.Bottom) Top = area.Top + 24;
    }

    private bool IsOnScreen(double left, double top)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        double w = Math.Max(ActualWidth, 100), h = Math.Max(ActualHeight, 60);
        // At least 40px of the widget must be visible.
        return left + w > vl + 40 && left < vr - 40 && top + h > vt + 40 && top < vb - 40;
    }

    // ---------- click-through / Shift+Alt interaction ----------

    private void UpdateInteractive()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool want = NativeMethods.IsModifierDown() || _menuOpen || _dragging || _resizing;
        if (want == _interactive) return;
        _interactive = want;

        if (want)
        {
            NativeMethods.SetExStyle(_hwnd, 0, NativeMethods.WS_EX_TRANSPARENT);
            InstallWheelHook();
        }
        else
        {
            NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT, 0);
            RemoveWheelHook();
        }

        ApplyLook();
    }

    // ---------- look ----------

    internal static Color ParseColor(string? hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? "#FFFFFF"); }
        catch { return Colors.White; }
    }

    // Text colour: IP (and the icon) at full strength, MAC and speed slightly faded when there's a background.
    private Brush _primary = Brushes.White, _secondary = Brushes.White;
    private const string SecondaryTag = "secondary";

    private void ApplyLook()
    {
        var text = ParseColor(_settings.TextColor);
        // Dark text gets a light panel / light halo; light text gets dark ones.
        bool darkText = (0.2126 * text.R + 0.7152 * text.G + 0.0722 * text.B) / 255.0 < 0.5;
        _primary = new SolidColorBrush(text);
        // No panel behind the text: full strength everywhere so the halo can carry it on any wallpaper.
        _secondary = _settings.ShowBackground ? new SolidColorBrush(Color.FromArgb(0xB3, text.R, text.G, text.B)) : _primary;
        HostText.Foreground = _primary;
        foreach (UIElement el in Rows.Children)
        {
            if (el is TextBlock tb) tb.Foreground = Equals(tb.Tag, SecondaryTag) ? _secondary : _primary;
            else if (el is System.Windows.Shapes.Shape icon) icon.Fill = _primary;
        }
        TextShadow.Color = HaloShadow.Color = darkText ? Colors.White : Colors.Black;

        if (_settings.ShowBackground)
        {
            Card.Background = darkText ? LightCardBackground : CardBackground;
            Card.BorderBrush = _interactive ? ActiveBorder : IdleBorder;
            Body.Effect = null;
            Halo.Effect = null;
        }
        else
        {
            Card.Background = _interactive ? GhostBackground : Brushes.Transparent;
            Card.BorderBrush = _interactive ? ActiveBorder : Brushes.Transparent;
            Body.Effect = _settings.TextShadow ? TextShadow : null;
            Halo.Effect = _settings.TextShadow ? HaloShadow : null;
        }
    }

    // ---------- resizing ----------

    private void ApplyScale(double scale)
    {
        scale = Math.Round(Math.Clamp(scale, MinScale, MaxScale), 2);
        _settings.Scale = scale;
        Card.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        // Pixel-snapped text looks best at 100%; scaled text needs ideal layout to stay smooth.
        TextOptions.SetTextFormattingMode(this, Math.Abs(scale - 1.0) < 0.001 ? TextFormattingMode.Display : TextFormattingMode.Ideal);
    }

    private bool InGrip(Point p) => p.X >= ActualWidth - GripSize && p.Y >= ActualHeight - GripSize;

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_interactive) return;
        e.Handled = true;
        StepScale(e.Delta);
    }

    private void StepScale(int delta)
    {
        NativeMethods.MaskModifierRelease();
        ApplyScale(_settings.Scale + (delta > 0 ? 0.1 : -0.1));
        _settings.Save(_slot);
    }

    // Only hooked while Shift+Alt is held, and only wheel events over the widget are taken.
    private IntPtr _wheelHook;
    private NativeMethods.LowLevelMouseProc? _wheelProc;

    private void InstallWheelHook()
    {
        if (_wheelHook != IntPtr.Zero) return;
        _wheelProc ??= WheelHookProc;
        _wheelHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _wheelProc, NativeMethods.GetModuleHandle(null), 0);
    }

    private void RemoveWheelHook()
    {
        if (_wheelHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_wheelHook);
        _wheelHook = IntPtr.Zero;
    }

    private IntPtr WheelHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == NativeMethods.WM_MOUSEWHEEL && _interactive
            && NativeMethods.GetWindowRect(_hwnd, out var r))
        {
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            // Must be over the widget AND the widget must be the visible window there (not covered by another app).
            if (info.pt.X >= r.Left && info.pt.X < r.Right && info.pt.Y >= r.Top && info.pt.Y < r.Bottom
                && NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(info.pt), NativeMethods.GA_ROOT) == _hwnd)
            {
                int delta = (short)(info.mouseData >> 16);
                Dispatcher.BeginInvoke(() => StepScale(delta));
                return new IntPtr(1); // swallow it so the window underneath doesn't scroll/zoom
            }
        }
        return NativeMethods.CallNextHookEx(_wheelHook, nCode, wParam, lParam);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_resizing)
        {
            // The top-left corner stays put, so the new width is simply where the mouse is plus the grab offset.
            double newWidth = Math.Max(40, p.X + (_resizeStartWidth - _resizeStartX));
            ApplyScale(_resizeStartScale * newWidth / _resizeStartWidth);
            return;
        }
        Cursor = _interactive && InGrip(p) ? Cursors.SizeNWSE : null;
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        ReleaseMouseCapture();
        _settings.Save(_slot);
        SendToBottom();
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive) return;

        var p = e.GetPosition(this);
        if (InGrip(p))
        {
            NativeMethods.MaskModifierRelease();
            _resizing = true;
            _resizeStartScale = _settings.Scale;
            _resizeStartWidth = ActualWidth;
            _resizeStartX = p.X;
            CaptureMouse();
            return;
        }

        NativeMethods.MaskModifierRelease();
        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { _dragging = false; }

        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save(_slot);
        SendToBottom();
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive) return;
        e.Handled = true;
        NativeMethods.MaskModifierRelease();
        var menu = BuildMenu();
        menu.PlacementTarget = Card;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) => { _menuOpen = false; RemoveEscHook(); _openMenu = null; SendToBottom(); };
        _menuOpen = true;
        _openMenu = menu;
        menu.IsOpen = true;
        InstallEscHook();
    }

    // The widget never takes keyboard focus, so Esc would go to whatever app is in front.
    // While the menu is open, a low-level keyboard hook catches Esc and closes it.
    private ContextMenu? _openMenu;
    private IntPtr _escHook;
    private NativeMethods.LowLevelMouseProc? _escProc;

    private void InstallEscHook()
    {
        if (_escHook != IntPtr.Zero) return;
        _escProc ??= EscHookProc;
        _escHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _escProc, NativeMethods.GetModuleHandle(null), 0);
    }

    private void RemoveEscHook()
    {
        if (_escHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_escHook);
        _escHook = IntPtr.Zero;
    }

    private IntPtr EscHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == NativeMethods.WM_KEYDOWN && _openMenu != null
            && Marshal.ReadInt32(lParam) == NativeMethods.VK_ESCAPE)
        {
            var menu = _openMenu;
            Dispatcher.BeginInvoke(() => menu.IsOpen = false);
            return new IntPtr(1); // swallow it
        }
        return NativeMethods.CallNextHookEx(_escHook, nCode, wParam, lParam);
    }

    // ---------- menu ----------

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem { Header = $"IP Widget  (slot {_slot})", IsEnabled = false });
        menu.Items.Add(new Separator());

        menu.Items.Add(InterfacesMenu());

        var onTop = new MenuItem { Header = "Keep on top of other windows", IsCheckable = true, IsChecked = _settings.KeepOnTop };
        onTop.Click += (_, _) =>
        {
            _settings.KeepOnTop = !_settings.KeepOnTop;
            _settings.Save(_slot);
            Topmost = _settings.KeepOnTop;
            SendToBottom();
        };
        menu.Items.Add(onTop);

        menu.Items.Add(FieldsMenu());
        menu.Items.Add(IpFormatMenu());
        var text = new MenuItem { Header = "Font, style, color and spacing…", FontWeight = FontWeights.SemiBold };
        text.Click += (_, _) => Dispatcher.BeginInvoke(OpenTextSettings);
        menu.Items.Add(text);

        var background = new MenuItem { Header = "Show background", IsCheckable = true, IsChecked = _settings.ShowBackground };
        background.Click += (_, _) =>
        {
            _settings.ShowBackground = !_settings.ShowBackground;
            _settings.Save(_slot);
            ApplyLook();
        };
        menu.Items.Add(background);

        var shadow = new MenuItem
        {
            Header = "Text shadow (when background is off)",
            IsCheckable = true,
            IsChecked = _settings.TextShadow,
            IsEnabled = !_settings.ShowBackground,
        };
        shadow.Click += (_, _) =>
        {
            _settings.TextShadow = !_settings.TextShadow;
            _settings.Save(_slot);
            ApplyLook();
        };
        menu.Items.Add(shadow);

        var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = StartupShortcut.Exists(_slot) };
        startup.Click += (_, _) =>
        {
            try
            {
                if (StartupShortcut.Exists(_slot)) StartupShortcut.Remove(_slot);
                else StartupShortcut.Create(_slot);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't update the startup shortcut:\n" + ex.Message, "IP Widget");
            }
        };
        menu.Items.Add(startup);

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += (_, _) => Refresh();
        menu.Items.Add(refresh);

        var resetSize = new MenuItem { Header = "Reset size" };
        resetSize.Click += (_, _) =>
        {
            ApplyScale(1.0);
            _settings.Save(_slot);
        };
        menu.Items.Add(resetSize);

        var reset = new MenuItem { Header = "Reset position" };
        reset.Click += (_, _) =>
        {
            _settings.Left = _settings.Top = null;
            _settings.Save(_slot);
            PlaceDefault();
        };
        menu.Items.Add(reset);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);

        return menu;
    }

    private MenuItem InterfacesMenu()
    {
        var parent = new MenuItem { Header = "Interfaces" };
        var chosen = _settings.Interfaces;
        var auto = new MenuItem { Header = "Automatic (all active)", IsCheckable = true, IsChecked = chosen.Count == 0 };
        auto.Click += (_, _) => SetInterfaces(new List<string>());
        parent.Items.Add(auto);
        parent.Items.Add(new Separator());

        // Real Wi-Fi/Ethernet adapters, plus anything ticked that isn't one of them (e.g. chosen in an older version).
        var adapters = NetworkInfo.Candidates();
        adapters.AddRange(chosen.Where(id => adapters.All(a => a.Id != id))
            .Select(id => NetworkInfo.Find(id) ?? (_lastSeen.TryGetValue(id, out var seen) ? seen : null))
            .OfType<AdapterInfo>());

        foreach (var a in adapters)
        {
            string state = a.IsUp ? a.Ip ?? "connected, no IPv4 address" : "disconnected";
            var item = new MenuItem
            {
                Header = $"{a.Name}  —  {state}",
                ToolTip = a.Description,
                IsCheckable = true,
                IsChecked = chosen.Count == 0 ? NetworkInfo.IsActive(a) : chosen.Contains(a.Id),
                Icon = TypeIcon(a.Kind, 12, "Normal", SystemColors.MenuTextBrush),
            };
            string id = a.Id;
            item.Click += (_, _) => ToggleInterface(id);
            parent.Items.Add(item);
        }
        return parent;
    }

    private void ToggleInterface(string id)
    {
        // Coming from Automatic: start from what's showing now.
        var list = _settings.Interfaces.Count == 0 ? _rows.Select(r => r.Id).ToList() : new List<string>(_settings.Interfaces);
        if (!list.Remove(id)) list.Add(id);
        SetInterfaces(list); // empty again = back to Automatic
    }

    private void SetInterfaces(List<string> ids)
    {
        _settings.Interfaces = ids;
        _settings.Save(_slot);
        Refresh();
    }

    // ---------- data ----------

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _debounceTimer.Stop(); _debounceTimer.Start(); });

    private void Refresh()
    {
        var chosen = _settings.Interfaces;
        var candidates = NetworkInfo.Candidates();
        List<AdapterInfo> rows;
        if (chosen.Count == 0)
        {
            rows = candidates.Where(NetworkInfo.IsActive).ToList();
        }
        else
        {
            // Ticked adapters stay visible when they're down; one that's been unplugged keeps its last-seen row.
            rows = NetworkInfo.Sorted(chosen
                .Select(id => candidates.FirstOrDefault(a => a.Id == id) ?? NetworkInfo.Find(id)
                    ?? (_lastSeen.TryGetValue(id, out var seen) ? seen with { IsUp = false, Ip = null, Speed = null } : null))
                .OfType<AdapterInfo>());
        }
        foreach (var r in rows) _lastSeen[r.Id] = r;

        _rows = rows;
        ApplyFields();
    }

    private List<AdapterInfo> _rows = new();
    private readonly Dictionary<string, AdapterInfo> _lastSeen = new();

    private static readonly FontFamily DefaultFont = new("Segoe UI");
    private static readonly FontFamily DefaultMacFont = new("Consolas");

    /// <summary>
    /// The hostname, then one row per adapter: type icon, IP, MAC, speed. The rows share one grid so the
    /// columns line up, and a column every row has hidden takes no space.
    /// </summary>
    private void ApplyFields()
    {
        var s = _settings;
        var font = string.IsNullOrEmpty(s.FontFamily) ? null : new FontFamily(s.FontFamily);
        HostText.Visibility = s.HideHostname ? Visibility.Collapsed : Visibility.Visible;
        HostText.FontFamily = font ?? DefaultFont;
        HostText.Text = FormatHostname(s.HostnameCase);
        HostText.FontSize = s.HostnameSize;
        StyleText(HostText, s.HostnameStyle ?? new FieldStyle { Weight = "SemiBold" });

        // Columns: 0 icon, 1 IP, 2 MAC, 3 speed.
        var lines = new List<FrameworkElement?[]>();
        if (_rows.Count == 0)
        {
            // Always show why there's nothing, even if the IP field is hidden.
            string why = s.Interfaces.Count == 0 ? "No network connection" : "Adapter not found";
            lines.Add(new FrameworkElement?[] { null, Text(why, s.IpSize, s.IpStyle, font ?? DefaultFont, false), null, null });
        }
        foreach (var r in _rows)
        {
            var line = new FrameworkElement?[4];
            if (!s.HideType)
            {
                // Wi-Fi and Ethernet get an icon (like the Mac version); anything else keeps its name as text.
                line[0] = (FrameworkElement?)TypeIcon(r.Kind, s.TypeSize, s.TypeStyle?.Weight, _primary)
                          ?? Text(r.Kind, s.TypeSize, s.TypeStyle, font ?? DefaultFont, true);
            }
            if (!s.HideIp)
                line[1] = Text(r.IsUp ? FormatIp(r.Ip ?? "No IPv4 address", s.IpFormat) : "disconnected",
                               s.IpSize, s.IpStyle, font ?? DefaultFont, false);
            if (!s.HideMac)
                line[2] = Text(r.Mac, s.MacSize, s.MacStyle, font ?? DefaultMacFont, true);
            if (!s.HideSpeed && r.IsUp)
                line[3] = Text(r.Speed ?? "–", s.SpeedSize, s.SpeedStyle, font ?? DefaultFont, true);
            if (!r.IsUp)
                foreach (var el in line) if (el != null) el.Opacity = 0.45;
            lines.Add(line);
        }

        Rows.Children.Clear();
        Rows.RowDefinitions.Clear();
        Rows.ColumnDefinitions.Clear();
        var used = Enumerable.Range(0, 4).Where(c => lines.Any(l => l[c] != null)).ToList();
        foreach (var _ in used) Rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Gaps are user-adjustable: a row only gets the row gap when there's something above it.
        bool above = !s.HideHostname;
        foreach (var line in lines.Where(l => l.Any(e => e != null)))
        {
            int row = Rows.RowDefinitions.Count;
            Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < used.Count; i++)
            {
                if (line[used[i]] is not FrameworkElement el) continue;
                // The icon sits close to its IP; the column gap applies between the text columns.
                double left = i == 0 ? 0 : used[i - 1] == 0 ? Math.Max(4, s.TypeSize * 0.45) : s.ColumnGap;
                el.Margin = new Thickness(left, above ? s.RowGap : 0, 0, 0);
                el.VerticalAlignment = VerticalAlignment.Center;
                if (used[i] == 0) el.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetRow(el, row);
                Grid.SetColumn(el, i);
                Rows.Children.Add(el);
            }
            above = true;
        }
    }

    /// <summary>One cell of an adapter row. MAC, speed and non-icon types use the faded colour when there's a background.</summary>
    private TextBlock Text(string text, double size, FieldStyle? style, FontFamily font, bool secondary)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = font,
            Foreground = secondary ? _secondary : _primary,
            Tag = secondary ? SecondaryTag : null,
        };
        StyleText(tb, style ?? new FieldStyle());
        return tb;
    }

    // ---------- type icons ----------

    /// <summary>
    /// Vector Wi-Fi / Ethernet icon sized to the type font, or null for other adapter kinds.
    /// In the widget it sits inside Body, so it gets the same halo as the text.
    /// </summary>
    private static System.Windows.Shapes.Path? TypeIcon(string kind, double fontSize, string? weight, Brush fill)
    {
        // Roughly the height of the "wifi" SF Symbol at this point size.
        double h = Math.Round(fontSize * 0.8, 1);
        // Stroke weight roughly like SF Symbols at Regular; heavier weights get thicker lines.
        double wScale = weight switch
        {
            "Light" => 0.8, "Medium" => 1.1, "SemiBold" => 1.2, "Bold" => 1.35, "Black" => 1.5, _ => 1.0,
        };
        double line = Math.Max(1, h * 0.12 * wScale);

        Geometry? geometry = kind switch
        {
            "WLAN" => WifiGeometry(h, line),
            "LAN" => PortGeometry(h, line),
            _ => null,
        };
        if (geometry == null) return null;
        geometry.Freeze();

        return new System.Windows.Shapes.Path
        {
            Data = geometry,
            Width = geometry.Bounds.Right,
            Height = h,
            Fill = fill,
            SnapsToDevicePixels = false,
        };
    }

    /// <summary>Three-arc fan with a dot at its centre, like the "wifi" SF Symbol.</summary>
    private static Geometry WifiGeometry(double h, double line)
    {
        double dot = line * 0.95;
        double angle = 47 * Math.PI / 180;      // each arc spans ±47° from vertical
        double cy = h - dot;                     // fan centre = dot centre, at the bottom
        double outer = cy - line / 2;            // outer arc's top edge touches the top
        double cx = outer * Math.Sin(angle) + line / 2;

        var arcs = new PathGeometry();
        foreach (double r in new[] { outer, outer * 0.655, outer * 0.31 })
        {
            var fig = new PathFigure { StartPoint = new Point(cx - r * Math.Sin(angle), cy - r * Math.Cos(angle)), IsFilled = false };
            fig.Segments.Add(new ArcSegment(new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle)),
                new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            arcs.Figures.Add(fig);
        }
        var pen = new Pen(Brushes.Black, line) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(arcs.GetWidenedPathGeometry(pen));
        group.Children.Add(new EllipseGeometry(new Point(cx, cy), dot, dot));
        return group;
    }

    /// <summary>
    /// RJ45 jack seen from the front: a solid block with the socket opening (and its latch notch) cut out,
    /// and contact pins along the top of the socket. Same shape as mac/Sources/WiredGlyphs.swift.
    /// </summary>
    private static Geometry PortGeometry(double h, double line)
    {
        double w = h * 1.12;
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(0.25, 0.25, w - 0.5, h - 0.5), h * 0.16, h * 0.16));

        // Socket: wide upper part, narrower latch notch at the bottom.
        double t = line * 1.15;
        double left = t, right = w - t, top = t, bottom = h - t;
        double notchW = w * 0.36, notchTop = top + (bottom - top) * 0.62;
        double mid = w / 2;
        var hole = new PathFigure { StartPoint = new Point(left, top), IsClosed = true };
        foreach (var p in new[]
        {
            new Point(right, top), new Point(right, notchTop),
            new Point(mid + notchW / 2, notchTop), new Point(mid + notchW / 2, bottom),
            new Point(mid - notchW / 2, bottom), new Point(mid - notchW / 2, notchTop),
            new Point(left, notchTop),
        })
            hole.Segments.Add(new LineSegment(p, false));
        group.Children.Add(new PathGeometry(new[] { hole }));

        // Contact pins along the top of the socket.
        const int pins = 4;
        double pinW = Math.Max(0.8, line * 0.5), pinH = (notchTop - top) * 0.38;
        double span = right - left - line * 1.2;
        for (int i = 0; i < pins; i++)
        {
            double x = left + line * 0.6 + span * (i + 0.5) / pins - pinW / 2;
            group.Children.Add(new RectangleGeometry(new Rect(x, top, pinW, pinH)));
        }
        return group;
    }

    // ---------- hostname case ----------

    internal static string FormatHostname(string mode)
    {
        // MachineName is the NetBIOS name (always upper case); the DNS host name keeps the case it was typed in.
        string asTyped;
        try { asTyped = System.Net.Dns.GetHostName(); } catch { asTyped = Environment.MachineName; }
        if (!asTyped.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !asTyped.StartsWith(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            asTyped = Environment.MachineName;

        return mode switch
        {
            "title" => asTyped.Length == 0 ? asTyped : char.ToUpperInvariant(asTyped[0]) + asTyped[1..].ToLowerInvariant(),
            "lower" => asTyped.ToLowerInvariant(),
            "asis" => asTyped,
            _ => asTyped.ToUpperInvariant(),
        };
    }

    internal static readonly double[] SizePresets = { 9, 10, 11, 12, 13, 14, 16, 17, 18, 20, 22, 24, 28, 32, 36, 42, 48, 56, 64, 72 };

    private bool _forceOnTop;

    /// <summary>
    /// Opens the text dialog. It edits the live settings so the widget previews every change;
    /// Cancel restores the snapshot taken here.
    /// </summary>
    private void OpenTextSettings()
    {
        var snapshot = _settings.Clone();
        var dialog = new TextSettingsWindow(_settings, InstalledFonts.Value, () => { ApplyLook(); ApplyFields(); });

        // Lift the widget above other windows while the dialog is open, so the preview is visible.
        _forceOnTop = true;
        Topmost = true;
        bool ok;
        try { ok = dialog.ShowDialog() == true; }
        finally
        {
            _forceOnTop = false;
            Topmost = _settings.KeepOnTop;
            SendToBottom();
        }

        if (ok)
            _settings.Save(_slot);
        else
            _settings.CopyTextSettingsFrom(snapshot);
        ApplyLook();
        ApplyFields();
    }

    // ---------- bold / italic / underline ----------

    internal static readonly string[] Weights = { "Light", "Normal", "Medium", "SemiBold", "Bold", "Black" };

    internal static FontWeight ToWeight(string? name) => name switch
    {
        "Light" => FontWeights.Light,
        "Medium" => FontWeights.Medium,
        "SemiBold" => FontWeights.SemiBold,
        "Bold" => FontWeights.Bold,
        "Black" => FontWeights.Black,
        _ => FontWeights.Normal,
    };

    private static void StyleText(System.Windows.Documents.TextElement el, FieldStyle st)
    {
        el.FontWeight = ToWeight(st.Weight);
        el.FontStyle = st.Italic ? FontStyles.Italic : FontStyles.Normal;
        if (el is System.Windows.Documents.Inline inline)
            inline.TextDecorations = st.Underline ? TextDecorations.Underline : null;
    }

    private static void StyleText(TextBlock tb, FieldStyle st)
    {
        tb.FontWeight = ToWeight(st.Weight);
        tb.FontStyle = st.Italic ? FontStyles.Italic : FontStyles.Normal;
        tb.TextDecorations = st.Underline ? TextDecorations.Underline : null;
    }

    private static readonly Lazy<List<string>> InstalledFonts = new(() =>
        Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(n => !n.StartsWith("@") && !n.Contains('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList());

    private static string FormatIp(string ip, string format)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return ip; // "No IPv4 address", "-", etc.
        return format switch
        {
            "last2" => $"{parts[2]}.{parts[3]}",
            "last1" => $".{parts[3]}",
            _ => ip,
        };
    }

    internal static readonly (string Name, string Hex)[] ColorPresets =
    {
        ("White", "#FFFFFF"), ("Light gray", "#C8C8C8"), ("Black", "#000000"), ("Yellow", "#FFE066"),
        ("Orange", "#FFA94D"), ("Green", "#69DB7C"), ("Cyan", "#66D9E8"), ("Blue", "#74C0FC"),
        ("Pink", "#F783AC"), ("Red", "#FF6B6B"),
    };

    private MenuItem IpFormatMenu()
    {
        string sample = _rows.Select(r => r.Ip).FirstOrDefault(ip => ip?.Split('.').Length == 4) ?? "192.168.26.218";
        var parent = new MenuItem { Header = "IP address format" };
        foreach (var (key, label) in new[] { ("full", "Full"), ("last2", "Last two parts"), ("last1", "Last part only") })
        {
            var item = new MenuItem
            {
                Header = $"{label}   {FormatIp(sample, key)}",
                IsCheckable = true,
                IsChecked = _settings.IpFormat == key || (key == "full" && _settings.IpFormat is not ("last2" or "last1")),
            };
            item.Click += (_, _) =>
            {
                _settings.IpFormat = key;
                _settings.Save(_slot);
                ApplyFields();
            };
            parent.Items.Add(item);
        }
        return parent;
    }

    private MenuItem FieldsMenu()
    {
        var fields = new (string Label, Func<bool> Hidden, Action<bool> SetHidden)[]
        {
            ("Hostname",    () => _settings.HideHostname, v => _settings.HideHostname = v),
            ("IP address",  () => _settings.HideIp,       v => _settings.HideIp = v),
            ("MAC address", () => _settings.HideMac,      v => _settings.HideMac = v),
            ("Type icon (Wi-Fi / Ethernet)", () => _settings.HideType, v => _settings.HideType = v),
            ("Speed",       () => _settings.HideSpeed,    v => _settings.HideSpeed = v),
        };

        var parent = new MenuItem { Header = "Show fields" };
        var items = new List<MenuItem>();
        void UpdateEnabled()
        {
            // Don't allow hiding the last visible field, or the widget would vanish.
            int shown = fields.Count(f => !f.Hidden());
            for (int i = 0; i < fields.Length; i++)
                items[i].IsEnabled = !(shown == 1 && !fields[i].Hidden());
        }

        foreach (var f in fields)
        {
            var item = new MenuItem { Header = f.Label, IsCheckable = true, IsChecked = !f.Hidden(), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                f.SetHidden(!item.IsChecked);
                _settings.Save(_slot);
                ApplyFields();
                UpdateEnabled();
            };
            items.Add(item);
            parent.Items.Add(item);
        }
        UpdateEnabled();
        return parent;
    }

    protected override void OnClosed(EventArgs e)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _keyTimer.Stop();
        _refreshTimer.Stop();
        RemoveWheelHook();
        base.OnClosed(e);
    }
}
