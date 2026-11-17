using System.IO;

namespace IpWidget;

/// <summary>"Start with Windows" = a shortcut per slot in the user's Startup folder.</summary>
internal static class StartupShortcut
{
    private static string PathFor(int slot) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        slot == 1 ? "IP Widget.lnk" : $"IP Widget (slot {slot}).lnk");

    public static bool Exists(int slot) => File.Exists(PathFor(slot));

    public static void Create(int slot)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(PathFor(slot));
        link.TargetPath = exe;
        link.Arguments = $"--slot {slot}";
        link.WorkingDirectory = Path.GetDirectoryName(exe);
        link.Description = "IP Widget";
        link.Save();
    }

    public static void Remove(int slot)
    {
        var path = PathFor(slot);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>If the exe was moved, point an existing shortcut at the new location.</summary>
    public static void RefreshIfPresent(int slot)
    {
        try { if (Exists(slot)) Create(slot); } catch { }
    }
}
