using System.Text.Json;

namespace PECSCommunicator;

/// <summary>
/// One PECS tile as defined in appsettings.json.
/// </summary>
/// <param name="Label">Text shown on the tile and sent to the listener.</param>
/// <param name="Color">Named color (e.g. "LightBlue") or "#RRGGBB".</param>
/// <param name="Image">Optional image filename relative to the Resources folder (used by TODO #2).</param>
public sealed record TileDefinition(string Label, string Color, string Image = "")
{
    /// <summary>Resolves <see cref="Color"/> to a System.Drawing color; anything invalid becomes LightGray.</summary>
    public Color ResolveColor()
    {
        if (string.IsNullOrWhiteSpace(Color)) return System.Drawing.Color.LightGray;
        try
        {
            // ColorTranslator understands both "#RRGGBB" and known color names.
            var c = ColorTranslator.FromHtml(Color.Trim());
            return c.IsEmpty ? System.Drawing.Color.LightGray : c;
        }
        catch
        {
            return System.Drawing.Color.LightGray;
        }
    }
}

/// <summary>
/// Communicator settings loaded from appsettings.json next to the exe.
/// Every value has a fallback so a missing or broken file never stops the app.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultListenerUrl = "http://localhost:5050/pecs";
    public const int DefaultColumns = 2;
    public const double DefaultDebounceSeconds = 2;

    /// <summary>The original four tiles, used when the file has none.</summary>
    public static readonly IReadOnlyList<TileDefinition> DefaultTiles = new[]
    {
        new TileDefinition("Toilet", "LightBlue"),
        new TileDefinition("Eat", "LightGreen"),
        new TileDefinition("Walk", "LightCyan"),
        new TileDefinition("Help", "LightYellow"),
    };

    // Mutable so the Settings screen can apply a new URL without a restart.
    public string ListenerUrl { get; set; } = DefaultListenerUrl;
    public int Columns { get; init; } = DefaultColumns;

    /// <summary>Repeat taps of the same tile within this many seconds are logged but not sent; 0 disables.</summary>
    public double DebounceSeconds { get; init; } = DefaultDebounceSeconds;
    public IReadOnlyList<TileDefinition> Tiles { get; init; } = DefaultTiles;

    /// <summary>Loads settings from appsettings.json in the app folder.</summary>
    public static AppSettings Load() => Load(DefaultPath);

    /// <summary>Loads settings from <paramref name="path"/>, falling back to defaults per value.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();

            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            using var doc = JsonDocument.Parse(File.ReadAllText(path), options);
            var root = doc.RootElement;

            // ListenerUrl: any non-blank string.
            var url = DefaultListenerUrl;
            if (root.TryGetProperty("ListenerUrl", out var u) && u.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(u.GetString()))
                url = u.GetString()!.Trim();

            // Columns: a positive integer.
            var columns = DefaultColumns;
            if (root.TryGetProperty("Columns", out var c) && c.ValueKind == JsonValueKind.Number
                && c.TryGetInt32(out var n) && n > 0)
                columns = n;

            // DebounceSeconds: a number >= 0 (0 turns debouncing off).
            var debounce = DefaultDebounceSeconds;
            if (root.TryGetProperty("DebounceSeconds", out var db) && db.ValueKind == JsonValueKind.Number
                && db.TryGetDouble(out var secs) && secs >= 0)
                debounce = secs;

            return new AppSettings { ListenerUrl = url, Columns = columns, DebounceSeconds = debounce, Tiles = ReadTiles(root) };
        }
        catch
        {
            // Unreadable or invalid JSON: run with the built-in defaults.
            return new AppSettings();
        }
    }

    /// <summary>Reads the "Tiles" array, skipping entries without a label; defaults if none remain.</summary>
    private static IReadOnlyList<TileDefinition> ReadTiles(JsonElement root)
    {
        if (!root.TryGetProperty("Tiles", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return DefaultTiles;

        var tiles = new List<TileDefinition>();
        foreach (var t in arr.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            var label = GetString(t, "Label");
            if (string.IsNullOrWhiteSpace(label)) continue;
            tiles.Add(new TileDefinition(label.Trim(), GetString(t, "Color"), GetString(t, "Image")));
        }
        return tiles.Count > 0 ? tiles : DefaultTiles;
    }

    private static string GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Full path of the appsettings.json next to the exe.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>
    /// Writes a new ListenerUrl into appsettings.json, keeping every other setting.
    /// If the file already has a "ListenerUrl" string, only that value is replaced in the text,
    /// so comments and formatting survive. Otherwise the file is parsed (comments skipped)
    /// and rewritten indented with the new value; comments are lost in that case only.
    /// </summary>
    public static void SaveListenerUrl(string path, string url)
    {
        var encoded = System.Text.Json.JsonSerializer.Serialize(url); // quoted + escaped JSON string

        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            var pattern = new System.Text.RegularExpressions.Regex("(\"ListenerUrl\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"");
            if (pattern.IsMatch(text))
            {
                File.WriteAllText(path, pattern.Replace(text, m => m.Groups[1].Value + encoded, 1));
                return;
            }

            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var node = System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: options) as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            node["ListenerUrl"] = url;
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var fresh = new System.Text.Json.Nodes.JsonObject { ["ListenerUrl"] = url };
        File.WriteAllText(path, fresh.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
