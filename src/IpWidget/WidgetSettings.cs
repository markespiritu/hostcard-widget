using System.IO;
using System.Text.Json;

namespace IpWidget;

internal sealed class FieldStyle
{
    /// <summary>Light, Normal, Medium, SemiBold, Bold or Black.</summary>
    public string Weight { get; set; } = "Normal";
    public bool Italic { get; set; }
    public bool Underline { get; set; }
}

/// <summary>Per-slot settings, stored in %APPDATA%\IpWidget\slot{N}.json.</summary>
internal sealed class WidgetSettings
{
    /// <summary>NetworkInterface.Id of the chosen adapter; null/empty means Automatic (primary adapter).</summary>
    public string? AdapterId { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool KeepOnTop { get; set; }
    /// <summary>False = text only (with a shadow so it stays readable on any wallpaper).</summary>
    public bool ShowBackground { get; set; } = true;
    /// <summary>Dark halo behind the text; only used when the background is off.</summary>
    public bool TextShadow { get; set; } = true;
    /// <summary>Text colour as #RRGGBB.</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>One font for every field; null = default (Segoe UI, with Consolas for the MAC).</summary>
    public string? FontFamily { get; set; }

    /// <summary>"upper" = FROGGER, "title" = Frogger, "lower" = frogger, "asis" = as typed when the PC was named.</summary>
    public string HostnameCase { get; set; } = "upper";

    /// <summary>Font size of each field, in points at 100% scale.</summary>
    public double HostnameSize { get; set; } = 17;
    public double IpSize { get; set; } = 14;
    public double MacSize { get; set; } = 12;
    public double TypeSize { get; set; } = 12;
    public double SpeedSize { get; set; } = 12;

    /// <summary>Weight / italic / underline per field.</summary>
    public FieldStyle HostnameStyle { get; set; } = new() { Weight = "SemiBold" };
    public FieldStyle IpStyle { get; set; } = new();
    public FieldStyle MacStyle { get; set; } = new();
    public FieldStyle TypeStyle { get; set; } = new();
    public FieldStyle SpeedStyle { get; set; } = new();

    /// <summary>Vertical gap between the two rows, and horizontal gap between the left and right columns.</summary>
    public double RowGap { get; set; } = 1;
    public double ColumnGap { get; set; } = 18;

    /// <summary>"full" =192.168.26.218, "last2" = 26.218, "last1" = .218</summary>
    public string IpFormat { get; set; } = "full";

    /// <summary>Fields hidden from the widget (all shown by default).</summary>
    public bool HideHostname { get; set; }
    public bool HideIp { get; set; }
    public bool HideMac { get; set; }
    public bool HideType { get; set; }
    public bool HideSpeed { get; set; }

    /// <summary>Size multiplier for the whole widget (1.0 = default).</summary>
    public double Scale { get; set; } = 1.0;

    public WidgetSettings Clone() =>
        JsonSerializer.Deserialize<WidgetSettings>(JsonSerializer.Serialize(this)) ?? new();

    /// <summary>Puts back everything the text dialog can change (used when it's cancelled).</summary>
    public void CopyTextSettingsFrom(WidgetSettings o)
    {
        FontFamily = o.FontFamily;
        HostnameCase = o.HostnameCase;
        TextColor = o.TextColor;
        (HostnameSize, IpSize, MacSize, TypeSize, SpeedSize) = (o.HostnameSize, o.IpSize, o.MacSize, o.TypeSize, o.SpeedSize);
        (HostnameStyle, IpStyle, MacStyle, TypeStyle, SpeedStyle) = (o.HostnameStyle, o.IpStyle, o.MacStyle, o.TypeStyle, o.SpeedStyle);
    }

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
