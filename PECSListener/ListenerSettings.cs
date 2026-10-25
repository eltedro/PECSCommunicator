using System.Diagnostics;
using System.Text.Json;

namespace PECSListener;

/// <summary>
/// TODO #9: alert options from appsettings.json next to the exe (comments and trailing commas allowed).
/// A missing or broken file, or a missing/invalid value, falls back to the defaults below, so the
/// Listener always starts.
/// </summary>
public sealed class ListenerSettings
{
    public bool Speak { get; private init; } = true;

    /// <summary>Spoken text; "{tile}" is replaced with the tile label.</summary>
    public string SpeechTemplate { get; private init; } = "Ian wants {tile}";

    /// <summary>Milliseconds after the sound starts before speech starts; 0 = together. Negative/invalid -> 300.</summary>
    public int SpeechDelayMs { get; private init; } = DefaultSpeechDelayMs;
    public const int DefaultSpeechDelayMs = 300;

    /// <summary>Flash the taskbar button until the window is brought to the foreground.</summary>
    public bool FlashTaskbar { get; private init; } = true;

    /// <summary>Restore and activate the window on each press (Windows may only flash it instead).</summary>
    public bool BringToFront { get; private init; }

    /// <summary>Tile label (case-insensitive) -> .wav path. "Default" is used for tiles not listed.</summary>
    public IReadOnlyDictionary<string, string> Sounds { get; private init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Banner background per tile (case-insensitive); "Default" for others. Text is white.</summary>
    public IReadOnlyDictionary<string, Color> BannerColors { get; private init; } = DefaultBannerColors();

    private static readonly Color FallbackBanner = Color.FromArgb(0, 100, 0); // dark green

    private static Dictionary<string, Color> DefaultBannerColors() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Toilet"] = Color.FromArgb(0, 80, 160),   // blue
        ["Eat"] = Color.FromArgb(0, 120, 60),      // green
        ["Walk"] = Color.FromArgb(160, 120, 0),    // amber
        ["Help"] = Color.FromArgb(180, 40, 40),    // red
        ["Default"] = FallbackBanner
    };

    /// <summary>Banner color for a tile: its own entry, else "Default", else dark green.</summary>
    public Color BannerColorFor(string tile) =>
        BannerColors.TryGetValue(tile, out var c) ? c
        : BannerColors.TryGetValue("Default", out var d) ? d : FallbackBanner;

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static ListenerSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new ListenerSettings();

            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var d = new ListenerSettings();

            var sounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("Sounds", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                        sounds[p.Name] = p.Value.GetString()!;

            // BannerColors: "#RRGGBB" or a named color (e.g. "DarkRed"). Invalid entries are skipped, so that
            // tile shows the Default color. If the section is missing, the built-in colors are used.
            var banners = DefaultBannerColors();
            if (root.TryGetProperty("BannerColors", out var bc) && bc.ValueKind == JsonValueKind.Object)
            {
                banners = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in bc.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && TryParseColor(p.Value.GetString(), out var color))
                        banners[p.Name] = color;
                    else
                        Debug.WriteLine($"ListenerSettings: invalid banner color for {p.Name}; using Default");
            }

            var template = GetString(root, "SpeechTemplate");
            return new ListenerSettings
            {
                Speak = GetBool(root, "Speak", d.Speak),
                SpeechTemplate = string.IsNullOrWhiteSpace(template) ? d.SpeechTemplate : template,
                SpeechDelayMs = root.TryGetProperty("SpeechDelayMs", out var sd) && sd.ValueKind == JsonValueKind.Number
                                && sd.TryGetInt32(out var ms) && ms >= 0 ? ms : DefaultSpeechDelayMs,
                FlashTaskbar = GetBool(root, "FlashTaskbar", d.FlashTaskbar),
                BringToFront = GetBool(root, "BringToFront", d.BringToFront),
                Sounds = sounds,
                BannerColors = banners
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ListenerSettings: using defaults ({ex.Message})");
            return new ListenerSettings();
        }
    }

    /// <summary>Accepts #RRGGBB or a known color name; anything else is invalid.</summary>
    private static bool TryParseColor(string? text, out Color color)
    {
        color = Color.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { color = ColorTranslator.FromHtml(text.Trim()); }
        catch (Exception) { return false; } // e.g. "#12" or "#GGGGGG"
        return !color.IsEmpty && color.A == 255; // unknown names come back empty
    }

    private static bool GetBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
