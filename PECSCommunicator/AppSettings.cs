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
    /// <summary>0 = automatic columns for the tile count (TODO #10, see TileCatalog.ColumnsFor).</summary>
    public const int DefaultColumns = 0;
    public const double DefaultDebounceSeconds = 2;
    public static readonly Size DefaultTouchScreenSize = new(1024, 600);

    /// <summary>The original four tiles, used when the file has none.</summary>
    public static readonly IReadOnlyList<TileDefinition> DefaultTiles = new[]
    {
        new TileDefinition("Toilet", "LightBlue"),
        new TileDefinition("Eat", "LightGreen"),
        new TileDefinition("Walk", "LightCyan"),
        new TileDefinition("Help", "LightYellow"),
    };

    // Android TODO #5: every listener that receives presses (laptop, phone, ...).
    // Plain URL strings keep the file simple; the URL itself identifies each listener in the status and CSV.
    // Mutable so the Settings screen can apply a new list without a restart.
    public IReadOnlyList<string> ListenerUrls { get; set; } = [DefaultListenerUrl];
    /// <summary>Tile columns; 0 = automatic, a positive value is used as is.</summary>
    public int Columns { get; init; } = DefaultColumns;

    /// <summary>Repeat taps of the same tile within this many seconds are logged but not sent; 0 disables.</summary>
    public double DebounceSeconds { get; init; } = DefaultDebounceSeconds;

    /// <summary>Exact resolution of the 7" touch panel, used to find it when 3 displays are attached (TODO #8).</summary>
    public Size TouchScreenSize { get; init; } = DefaultTouchScreenSize;
    public IReadOnlyList<TileDefinition> Tiles { get; init; } = DefaultTiles;

    /// <summary>
    /// TODO #10: true when <see cref="Tiles"/> came from the file (not the built-in defaults). Only then
    /// do its Colors override the colors of the picture tiles.
    /// </summary>
    public bool HasConfiguredTiles { get; init; }

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

            // ListenerUrls: array of non-blank strings. Backward compatible: a file with only the
            // legacy "ListenerUrl" string is read as a one-item list.
            IReadOnlyList<string> urls = [DefaultListenerUrl];
            if (root.TryGetProperty("ListenerUrls", out var ua) && ua.ValueKind == JsonValueKind.Array)
            {
                var list = ua.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                    .Select(e => e.GetString()!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (list.Count > 0) urls = list;
            }
            else if (root.TryGetProperty("ListenerUrl", out var u) && u.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(u.GetString()))
                urls = [u.GetString()!.Trim()];

            // Columns: 0 = automatic (TODO #10), or a positive integer.
            var columns = DefaultColumns;
            if (root.TryGetProperty("Columns", out var c) && c.ValueKind == JsonValueKind.Number
                && c.TryGetInt32(out var n) && n >= 0)
                columns = n;

            // DebounceSeconds: a number >= 0 (0 turns debouncing off).
            var debounce = DefaultDebounceSeconds;
            if (root.TryGetProperty("DebounceSeconds", out var db) && db.ValueKind == JsonValueKind.Number
                && db.TryGetDouble(out var secs) && secs >= 0)
                debounce = secs;

            // TouchScreenSize: "WIDTHxHEIGHT", e.g. "1024x600".
            var touchSize = DefaultTouchScreenSize;
            if (root.TryGetProperty("TouchScreenSize", out var ts) && ts.ValueKind == JsonValueKind.String)
            {
                var parts = (ts.GetString() ?? "").ToLowerInvariant().Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var tw) && int.TryParse(parts[1].Trim(), out var th)
                    && tw > 0 && th > 0)
                    touchSize = new Size(tw, th);
            }

            var tiles = ReadTiles(root);
            return new AppSettings
            {
                ListenerUrls = urls, Columns = columns, DebounceSeconds = debounce, TouchScreenSize = touchSize,
                Tiles = tiles, HasConfiguredTiles = !ReferenceEquals(tiles, DefaultTiles)
            };
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
    /// Android TODO #5: writes the listener list into appsettings.json as "ListenerUrls", keeping every
    /// other setting. Only the URL value is replaced in the text, so comments and formatting survive:
    /// an existing "ListenerUrls" array is replaced, or a legacy "ListenerUrl" string is converted.
    /// If neither is found the file is parsed (comments skipped) and rewritten indented;
    /// comments are lost in that case only.
    /// </summary>
    public static void SaveListenerUrls(string path, IReadOnlyList<string> urls)
    {
        // "ListenerUrls": [ ... ] laid out one URL per line, like the shipped file.
        var items = string.Join(",\r\n", urls.Select(u => "    " + JsonSerializer.Serialize(u)));
        var arrayText = "[\r\n" + items + "\r\n  ]";

        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            var current = new System.Text.RegularExpressions.Regex("(\"ListenerUrls\"\\s*:\\s*)\\[[^\\]]*\\]");
            var legacy = new System.Text.RegularExpressions.Regex("\"ListenerUrl\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"");
            if (current.IsMatch(text))
            {
                File.WriteAllText(path, current.Replace(text, m => m.Groups[1].Value + arrayText, 1));
                return;
            }
            if (legacy.IsMatch(text))
            {
                File.WriteAllText(path, legacy.Replace(text, _ => "\"ListenerUrls\": " + arrayText, 1));
                return;
            }

            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var node = System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: options) as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            node.Remove("ListenerUrl");
            node["ListenerUrls"] = ToJsonArray(urls);
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var fresh = new System.Text.Json.Nodes.JsonObject { ["ListenerUrls"] = ToJsonArray(urls) };
        File.WriteAllText(path, fresh.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static System.Text.Json.Nodes.JsonArray ToJsonArray(IReadOnlyList<string> urls) =>
        new(urls.Select(u => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(u)).ToArray());
}
