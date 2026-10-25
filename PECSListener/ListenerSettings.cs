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

    /// <summary>Flash the taskbar button until the window is brought to the foreground.</summary>
    public bool FlashTaskbar { get; private init; } = true;

    /// <summary>Restore and activate the window on each press (Windows may only flash it instead).</summary>
    public bool BringToFront { get; private init; }

    /// <summary>Tile label (case-insensitive) -> .wav path. "Default" is used for tiles not listed.</summary>
    public IReadOnlyDictionary<string, string> Sounds { get; private init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            var template = GetString(root, "SpeechTemplate");
            return new ListenerSettings
            {
                Speak = GetBool(root, "Speak", d.Speak),
                SpeechTemplate = string.IsNullOrWhiteSpace(template) ? d.SpeechTemplate : template,
                FlashTaskbar = GetBool(root, "FlashTaskbar", d.FlashTaskbar),
                BringToFront = GetBool(root, "BringToFront", d.BringToFront),
                Sounds = sounds
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ListenerSettings: using defaults ({ex.Message})");
            return new ListenerSettings();
        }
    }

    private static bool GetBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
