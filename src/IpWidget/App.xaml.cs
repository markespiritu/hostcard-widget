using System.Windows;

namespace IpWidget;

public partial class App : Application
{
    private const int MaxSlots = 9;
    private Mutex? _slotMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int? requested = ParseSlot(e.Args);
        int slot;

        if (requested is int r)
        {
            if (!TryClaim(r))
            {
                // That slot is already running (e.g. started twice from the Startup folder): just quit.
                Shutdown();
                return;
            }
            slot = r;
        }
        else
        {
            // No slot given: take the first free one, so a second copy becomes slot 2, and so on.
            slot = Enumerable.Range(1, MaxSlots).FirstOrDefault(TryClaim);
            if (slot == 0)
            {
                MessageBox.Show($"All {MaxSlots} IP Widget slots are already running.", "IP Widget");
                Shutdown();
                return;
            }
        }

        var window = new MainWindow(slot);
        MainWindow = window;
        window.Show();
    }

    private bool TryClaim(int slot)
    {
        var m = new Mutex(true, $@"Local\IpWidget.Slot{slot}", out bool created);
        if (!created)
        {
            m.Dispose();
            return false;
        }
        _slotMutex = m;
        return true;
    }

    private static int? ParseSlot(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? value = null;
            if ((a is "--slot" or "-slot" or "/slot") && i + 1 < args.Length) value = args[i + 1];
            else if (a.StartsWith("--slot=")) value = a["--slot=".Length..];
            if (value != null && int.TryParse(value, out int n) && n >= 1 && n <= MaxSlots) return n;
        }
        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _slotMutex?.ReleaseMutex(); } catch { }
        _slotMutex?.Dispose();
        base.OnExit(e);
    }
}
