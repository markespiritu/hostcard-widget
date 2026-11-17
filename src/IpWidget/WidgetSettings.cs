using System.IO;
using System.Text.Json;

namespace IpWidget;

/// <summary>Per-slot settings, stored in %APPDATA%\IpWidget\slot{N}.json.</summary>
internal sealed class WidgetSettings
{
    /// <summary>NetworkInterface.Id of the chosen adapter; null/empty means Automatic (primary adapter).</summary>
    public string? AdapterId { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool KeepOnTop { get; set; }

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IpWidget");

    private static string PathFor(int slot) => Path.Combine(Folder, $"slot{slot}.json");

    public static WidgetSettings Load(int slot)
    {
        try
        {
            var path = PathFor(slot);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(path)) ?? new();
        }
        catch { /* corrupt or unreadable: start fresh */ }
        return new();
    }

    public void Save(int slot)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(slot), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
