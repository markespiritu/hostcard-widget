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

    private readonly int _slot;
    private readonly WidgetSettings _settings;
    private readonly DispatcherTimer _keyTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _debounceTimer;

    private IntPtr _hwnd;
    private bool _interactive;
    private bool _menuOpen;
    private bool _dragging;

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

        // Poll Ctrl: holding it makes the widget clickable/draggable, otherwise clicks pass through.
        _keyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => UpdateInteractive(), Dispatcher);
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _debounceTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { _debounceTimer!.Stop(); Refresh(); }, Dispatcher);
        _debounceTimer.Stop();

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

        MouseLeftButtonDown += OnLeftDown;
        MouseRightButtonUp += OnRightUp;
        Loaded += OnLoaded;

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
        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && !_settings.KeepOnTop)
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
        if (_hwnd == IntPtr.Zero || _settings.KeepOnTop) return;
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

    // ---------- click-through / Ctrl interaction ----------

    private void UpdateInteractive()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool want = NativeMethods.IsCtrlDown() || _menuOpen || _dragging;
        if (want == _interactive) return;
        _interactive = want;

        if (want)
            NativeMethods.SetExStyle(_hwnd, 0, NativeMethods.WS_EX_TRANSPARENT);
        else
            NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT, 0);

        Card.BorderBrush = want ? ActiveBorder : IdleBorder;
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive) return;
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
        var menu = BuildMenu();
        menu.PlacementTarget = Card;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) => { _menuOpen = false; SendToBottom(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }

    // ---------- menu ----------

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem { Header = $"IP Widget  (slot {_slot})", IsEnabled = false });
        menu.Items.Add(new Separator());

        var adapterMenu = new MenuItem { Header = "Adapter" };
        var auto = new MenuItem { Header = "Automatic (primary adapter)", IsCheckable = true, IsChecked = string.IsNullOrEmpty(_settings.AdapterId) };
        auto.Click += (_, _) => SelectAdapter(null);
        adapterMenu.Items.Add(auto);
        adapterMenu.Items.Add(new Separator());
        foreach (var n in NetworkInfo.Candidates())
        {
            var info = NetworkInfo.Describe(n);
            var item = new MenuItem
            {
                Header = $"{info.Name}  ({info.Kind}, {(info.IsUp ? "connected" : "disconnected")})",
                ToolTip = info.Description,
                IsCheckable = true,
                IsChecked = _settings.AdapterId == info.Id,
            };
            string id = info.Id;
            item.Click += (_, _) => SelectAdapter(id);
            adapterMenu.Items.Add(item);
        }
        menu.Items.Add(adapterMenu);

        var onTop = new MenuItem { Header = "Keep on top of other windows", IsCheckable = true, IsChecked = _settings.KeepOnTop };
        onTop.Click += (_, _) =>
        {
            _settings.KeepOnTop = !_settings.KeepOnTop;
            _settings.Save(_slot);
            Topmost = _settings.KeepOnTop;
            SendToBottom();
        };
        menu.Items.Add(onTop);

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

    private void SelectAdapter(string? id)
    {
        _settings.AdapterId = id;
        _settings.Save(_slot);
        Refresh();
    }

    // ---------- data ----------

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _debounceTimer.Stop(); _debounceTimer.Start(); });

    private void Refresh()
    {
        HostText.Text = Environment.MachineName;

        var candidates = NetworkInfo.Candidates();
        NetworkInterface? nic = string.IsNullOrEmpty(_settings.AdapterId)
            ? NetworkInfo.Primary(candidates)
            : candidates.FirstOrDefault(n => n.Id == _settings.AdapterId);

        if (nic == null)
        {
            IpText.Text = string.IsNullOrEmpty(_settings.AdapterId) ? "No network adapter" : "Adapter not found";
            MacText.Text = "";
            LinkText.Text = "";
            return;
        }

        var info = NetworkInfo.Describe(nic);
        IpText.Text = info.Ip;
        MacText.Text = info.Mac;
        LinkText.Text = $"{info.Kind} · {info.Speed}";
    }

    protected override void OnClosed(EventArgs e)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _keyTimer.Stop();
        _refreshTimer.Stop();
        base.OnClosed(e);
    }
}
