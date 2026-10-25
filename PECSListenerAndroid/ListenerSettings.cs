using System.Text.Json;
using Android.Content;
using Android.Graphics;
using Android.Util;

namespace PECSListenerAndroid;

/// <summary>
/// TODO #3: alert options from the bundled Assets/appsettings.json (comments and trailing commas allowed),
/// mirroring the Windows Listener's settings. A missing or broken file, or a missing/invalid value,
/// falls back to the defaults below, so the listener always starts.
/// </summary>
public sealed class ListenerSettings
{
    public bool Speak { get; private init; } = true;

    /// <summary>Spoken text; "{tile}" is replaced with the tile label.</summary>
    public string SpeechTemplate { get; private init; } = "Ian wants {tile}";

    /// <summary>Milliseconds after the sound starts before speech starts; 0 = together.</summary>
    public int SpeechDelayMs { get; private init; } = DefaultSpeechDelayMs;
    public const int DefaultSpeechDelayMs = 300;

    public bool Vibrate { get; private init; } = true;

    /// <summary>Length of each note of a tile's chime.</summary>
    public int SoundNoteMs { get; private init; } = 140;

    /// <summary>Tile (case-insensitive) -> chime note frequencies in Hz. "Default" for tiles not listed.</summary>
    public IReadOnlyDictionary<string, int[]> Sounds { get; private init; } = DefaultSounds();

    /// <summary>Banner background per tile (case-insensitive); "Default" for others. Text is white.</summary>
    public IReadOnlyDictionary<string, Color> BannerColors { get; private init; } = DefaultBannerColors();

    private static readonly Color FallbackBanner = Color.Rgb(0, 100, 0); // dark green

    private static Dictionary<string, Color> DefaultBannerColors() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Toilet"] = Color.Rgb(0, 80, 160),    // blue
        ["Eat"] = Color.Rgb(0, 120, 60),       // green
        ["Walk"] = Color.Rgb(160, 120, 0),     // amber
        ["Help"] = Color.Rgb(180, 40, 40),     // red
        ["Default"] = FallbackBanner
    };

    private static Dictionary<string, int[]> DefaultSounds() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Toilet"] = [523, 784],              // two rising notes
        ["Eat"] = [784, 659, 523],            // three falling
        ["Walk"] = [523, 659, 784, 1047],     // four rising (fanfare)
        ["Help"] = [988, 740, 988, 740],      // high-low alternating (urgent)
        ["Default"] = [660, 880]
    };

    /// <summary>Banner color for a tile: its own entry, else "Default", else dark green.</summary>
    public Color BannerColorFor(string tile) =>
        BannerColors.TryGetValue(tile, out var c) ? c
        : BannerColors.TryGetValue("Default", out var d) ? d : FallbackBanner;

    /// <summary>Chime notes for a tile: its own entry, else "Default", else none.</summary>
    public int[] SoundFor(string tile) =>
        Sounds.TryGetValue(tile, out var s) ? s
        : Sounds.TryGetValue("Default", out var d) ? d : [];

    public const string AssetName = "appsettings.json";

    public static ListenerSettings Load(Context context)
    {
        try
        {
            string json;
            using (var stream = context.Assets!.Open(AssetName))
            using (var reader = new StreamReader(stream))
                json = reader.ReadToEnd();

            using var doc = JsonDocument.Parse(json,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var d = new ListenerSettings();

            // Sounds: arrays of positive Hz values. Invalid entries are skipped (that tile uses Default).
            var sounds = DefaultSounds();
            if (root.TryGetProperty("Sounds", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                sounds = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        sounds[p.Name] = p.Value.EnumerateArray()
                            .Where(n => n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var hz) && hz is > 20 and < 20000)
                            .Select(n => n.GetInt32()).ToArray();
            }

            // BannerColors: "#RRGGBB" (or an Android color name). Invalid entries are skipped (that tile uses Default).
            var banners = DefaultBannerColors();
            if (root.TryGetProperty("BannerColors", out var bc) && bc.ValueKind == JsonValueKind.Object)
            {
                banners = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in bc.EnumerateObject())
                {
                    try { banners[p.Name] = Color.ParseColor(p.Value.GetString()!.Trim()); }
                    catch { Log.Warn(PressAlert.Tag, $"Invalid banner color for {p.Name}; using Default"); }
                }
            }

            var template = root.TryGetProperty("SpeechTemplate", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return new ListenerSettings
            {
                Speak = GetBool(root, "Speak", d.Speak),
                SpeechTemplate = string.IsNullOrWhiteSpace(template) ? d.SpeechTemplate : template,
                SpeechDelayMs = GetInt(root, "SpeechDelayMs", min: 0) ?? DefaultSpeechDelayMs,
                Vibrate = GetBool(root, "Vibrate", d.Vibrate),
                SoundNoteMs = GetInt(root, "SoundNoteMs", min: 20) ?? d.SoundNoteMs,
                Sounds = sounds,
                BannerColors = banners
            };
        }
        catch (Exception ex)
        {
            Log.Warn(PressAlert.Tag, $"Settings: using defaults ({ex.Message})");
            return new ListenerSettings();
        }
    }

    private static bool GetBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    private static int? GetInt(JsonElement root, string name, int min) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= min
            ? i : null;
}
