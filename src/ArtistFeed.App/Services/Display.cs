using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using ArtistFeed.App.ViewModels;
using ArtistFeed.Core;

namespace ArtistFeed.App.Services;

/// <summary>
/// Display settings: text size (the whole window is scaled, like a browser's zoom) and colour scheme
/// (Dark, Light, High contrast, or Match this computer). Kept with this computer's preferences in the database.
/// The colours match the web version's.
/// </summary>
public static class Display
{
    public const string KvKey = "desktop_display";

    public static readonly Option[] Sizes =
    {
        new("90", "Smaller (90%)"), new("100", "Normal (100%)"), new("115", "Larger (115%)"),
        new("130", "Large (130%)"), new("150", "Largest (150%)"),
    };

    public static readonly Option[] Themes =
    {
        new("dark", "Dark"), new("light", "Light"), new("contrast", "High contrast"), new("system", "Match this computer"),
    };

    // Brush key → colour, per scheme. Dark is the default, as in App.axaml.
    static readonly Dictionary<string, Dictionary<string, string>> Palettes = new()
    {
        ["dark"] = new()
        {
            ["Bg"] = "#0f1012", ["Panel"] = "#17191d", ["Panel2"] = "#1f2227", ["Line"] = "#2a2e35",
            ["Text"] = "#eceef1", ["Muted"] = "#8d95a1", ["Accent"] = "#d9f25c", ["AccentInk"] = "#d9f25c",
            ["OnAccent"] = "#14160a", ["Danger"] = "#ff6b6b", ["Hover"] = "#444a55", ["Letter"] = "#3a3f48",
            ["Overlay"] = "#cc0f1012", ["PlayingRow"] = "#262b1a", ["Warn"] = "#e6b450",
        },
        ["light"] = new()
        {
            ["Bg"] = "#f5f6f8", ["Panel"] = "#ffffff", ["Panel2"] = "#eceff3", ["Line"] = "#d3d8df",
            ["Text"] = "#15181d", ["Muted"] = "#586170", ["Accent"] = "#c8e33d", ["AccentInk"] = "#4b5a00",
            ["OnAccent"] = "#14160a", ["Danger"] = "#c62828", ["Hover"] = "#a7afba", ["Letter"] = "#c3c8cf",
            ["Overlay"] = "#e6ffffff", ["PlayingRow"] = "#eef5cc", ["Warn"] = "#8f5f00",
        },
        ["contrast"] = new()
        {
            ["Bg"] = "#000000", ["Panel"] = "#000000", ["Panel2"] = "#1c1c1c", ["Line"] = "#ffffff",
            ["Text"] = "#ffffff", ["Muted"] = "#e2e2e2", ["Accent"] = "#ffff00", ["AccentInk"] = "#ffff00",
            ["OnAccent"] = "#000000", ["Danger"] = "#ff6a6a", ["Hover"] = "#ffff00", ["Letter"] = "#5c5c5c",
            ["Overlay"] = "#eb000000", ["PlayingRow"] = "#333300", ["Warn"] = "#ffd34d",
        },
    };

    public static string Size { get; private set; } = "100";
    public static string Theme { get; private set; } = "dark";
    public static double Scale => double.Parse(Size, System.Globalization.CultureInfo.InvariantCulture) / 100;

    /// <summary>The scheme in use: "system" resolved to light or dark.</summary>
    public static string ActualTheme =>
        Theme != "system" ? Theme
        : Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light ? "light" : "dark";

    /// <summary>The panel colour as CSS, for the player's page.</summary>
    public static string PanelCss => Palettes[ActualTheme]["Panel"];

    public static event Action? Changed;
    static bool _watching;

    public static (string Size, string Theme) Read(Db db)
    {
        var size = "100";
        var theme = "dark";
        if (Html.ParseJson(db.KvGet(KvKey)) is JsonObject p)
        {
            if (p["size"]?.GetValue<string>() is { } s && Sizes.Any(o => o.Value == s)) size = s;
            if (p["theme"]?.GetValue<string>() is { } t && Themes.Any(o => o.Value == t)) theme = t;
        }
        return (size, theme);
    }

    public static void Save(Db db, string size, string theme)
    {
        db.KvSet(KvKey, new JsonObject { ["size"] = size, ["theme"] = theme }.ToJsonString());
        Apply(size, theme);
    }

    public static void Load(Db db)
    {
        var (size, theme) = Read(db);
        Apply(size, theme);
    }

    public static void Apply(string size, string theme)
    {
        Size = Sizes.Any(o => o.Value == size) ? size : "100";
        Theme = Themes.Any(o => o.Value == theme) ? theme : "dark";
        var app = Application.Current;
        if (app == null) return;
        if (!_watching && app.PlatformSettings != null)
        {
            _watching = true;
            app.PlatformSettings.ColorValuesChanged += (_, _) => { if (Theme == "system") Apply(Size, Theme); };
        }
        var actual = ActualTheme;
        var res = app.Resources;
        foreach (var (key, hex) in Palettes[actual])
            res[key] = new SolidColorBrush(Color.Parse(hex));
        // Fluent's own controls (ticks, focus, selection) follow the scheme too.
        var accent = Color.Parse(Palettes[actual]["AccentInk"]);
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorDark1"] = Shade(accent, -0.1);
        res["SystemAccentColorDark2"] = Shade(accent, -0.2);
        res["SystemAccentColorDark3"] = Shade(accent, -0.3);
        res["SystemAccentColorLight1"] = Shade(accent, 0.15);
        res["SystemAccentColorLight2"] = Shade(accent, 0.3);
        res["SystemAccentColorLight3"] = Shade(accent, 0.45);
        app.RequestedThemeVariant = actual == "light" ? ThemeVariant.Light : ThemeVariant.Dark;
        Changed?.Invoke();
    }

    static Color Shade(Color c, double by)
    {
        byte f(byte v) => (byte)Math.Clamp(by < 0 ? v * (1 + by) : v + (255 - v) * by, 0, 255);
        return Color.FromRgb(f(c.R), f(c.G), f(c.B));
    }

    /// <summary>Scale a window's content by the text size, now and whenever it changes; High contrast gets thicker outlines.</summary>
    public static void Attach(Window w)
    {
        if (w.Content is not Control content) return;
        var zoom = new LayoutTransformControl { Name = "DisplayZoom" };
        // A window of fixed width (Settings, the "are you sure?" box) grows with the text.
        var baseWidth = w.CanResize ? double.NaN : w.Width;
        w.Content = null;
        zoom.Child = content;
        w.Content = zoom;
        void update()
        {
            zoom.LayoutTransform = Size == "100" ? null : new ScaleTransform(Scale, Scale);
            if (!double.IsNaN(baseWidth)) w.Width = baseWidth * Scale;
            w.Classes.Set("contrast", ActualTheme == "contrast");
        }
        update();
        Action handler = update;
        Changed += handler;
        w.Closed += (_, _) => Changed -= handler;
    }
}
