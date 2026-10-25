using System.Text.RegularExpressions;

namespace PECSCommunicator;

/// <summary>One tile ready to show: its label (also what is sent), background color, and picture.</summary>
/// <param name="Label">Text shown on the tile and sent to the listeners.</param>
/// <param name="Color">Tile background.</param>
/// <param name="Picture">Picture, or null for a text-only tile. Whoever shows it owns (and disposes) it.</param>
/// <param name="Source">File name the tile came from, or "" for text-only tiles; for logs and the self-test.</param>
public sealed record TileEntry(string Label, Color Color, Image? Picture, string Source);

/// <summary>Result of one scan: the tiles plus where they came from (for logs and Settings).</summary>
public sealed record TileSet(IReadOnlyList<TileEntry> Tiles, string Origin)
{
    /// <summary>Disposes every picture; used when a set is thrown away without being shown.</summary>
    public void DisposePictures()
    {
        foreach (var t in Tiles) t.Picture?.Dispose();
    }
}

/// <summary>
/// TODO #10: builds the tiles from the pictures in the Resources folder next to the exe, so adding,
/// removing or renaming a picture changes the tiles without touching appsettings.json.
///
/// Rules:
///   - Only .png/.jpg/.jpeg/.bmp files whose name starts with a number and a separator become tiles,
///     e.g. "01_Toilet.png", "2-Go_Outside.jpg", "10 Help.png". Removing the number ("Toilet.png")
///     disables a tile without deleting its picture.
///   - Label = the name without the number, separator and extension; "_" and "-" become spaces;
///     capitalization is kept ("02_Eat.png" -> "Eat").
///   - Order: by the number's value (so 2 comes before 10), then by label.
///   - Two files with the same label (ignoring case): the first in that order wins.
///   - Color: a Tiles entry in appsettings.json with the same label overrides; otherwise the picture's
///     average color, lightened to a pastel so the black label stays readable; LightGray if the
///     picture can't be read (that tile is then text-only).
///   - No numbered pictures, or no Resources folder: the Tiles from appsettings.json are used, and
///     if there are none, the four built-in tiles. The screen is never empty.
/// </summary>
public static class TileCatalog
{
    /// <summary>Picture types that can become tiles (anything GDI+ reads well).</summary>
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    /// <summary>"01_Toilet" -> number "01", rest "Toilet". The separator is one "_", "-" or space.</summary>
    private static readonly Regex NumberedName = new(@"^(\d+)[_\- ](.+)$", RegexOptions.CultureInvariant);

    /// <summary>More tiles than this get too small to hit reliably on the 7" panel (a warning is logged).</summary>
    public const int ComfortableMaxTiles = 12;

    /// <summary>The Resources folder next to the exe.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "Resources");

    // Tile events (scan results, bad pictures, fallbacks) go to %LOCALAPPDATA%\PECS\Logs\tiles-yyyy-MM-dd.csv.
    private static readonly CsvLog _log = new("tiles", "Time", "Event", "Detail");

    /// <summary>Appends one row to the tiles log (never throws).</summary>
    public static void Log(string evt, string detail) =>
        _log.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), evt, detail);

    /// <summary>A numbered picture file found in the folder, before its picture is loaded.</summary>
    private sealed record Candidate(string Path, string FileName, long Number, string Label);

    /// <summary>
    /// Scans <paramref name="folder"/> and returns the tiles to show (never empty).
    /// <paramref name="settings"/> supplies the color overrides and the fallback tiles.
    /// </summary>
    public static TileSet Load(string folder, AppSettings settings)
    {
        var candidates = FindNumberedPictures(folder);
        if (candidates.Count == 0)
        {
            var reason = Directory.Exists(folder) ? "no numbered pictures in " + folder : "folder not found: " + folder;
            var fallback = FromSettings(folder, settings);
            Log("Fallback", $"{reason}; using {fallback.Origin}");
            return fallback;
        }

        // Color overrides: only tiles really written in appsettings.json, not the built-in defaults.
        var overrides = settings.HasConfiguredTiles
            ? settings.Tiles.GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TileDefinition>(StringComparer.OrdinalIgnoreCase);

        var tiles = new List<TileEntry>();
        foreach (var c in candidates)
        {
            var picture = TileButton.TryLoadPicture(c.Path);
            if (picture == null) Log("BadImage", $"{c.FileName}: could not be read; showing \"{c.Label}\" as text only");

            Color color;
            if (overrides.TryGetValue(c.Label, out var def) && TryParseColor(def.Color, out var configured))
                color = configured;
            else if (picture != null)
                color = PastelAverage(picture);
            else
                color = Color.LightGray;

            tiles.Add(new TileEntry(c.Label, color, picture, c.FileName));
        }

        if (tiles.Count > ComfortableMaxTiles)
            Log("Warning", $"{tiles.Count} tiles; more than {ComfortableMaxTiles} may be too small to tap on the 7\" panel");
        Log("Loaded", $"{tiles.Count} tile(s) from {folder}: " + string.Join(", ", tiles.Select(t => t.Source)));
        return new TileSet(tiles, $"{tiles.Count} picture(s) in Resources");
    }

    /// <summary>
    /// Numbered pictures in the folder, sorted by number value then label, duplicates (same label,
    /// ignoring case) removed after the first. Missing or unreadable folder: empty list.
    /// </summary>
    private static List<Candidate> FindNumberedPictures(string folder)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(folder)) return [];
            files = Directory.GetFiles(folder);
        }
        catch (Exception ex)
        {
            Log("Error", $"cannot list {folder}: {ex.Message}");
            return [];
        }

        var found = new List<Candidate>();
        foreach (var path in files)
        {
            if (!ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;

            var m = NumberedName.Match(Path.GetFileNameWithoutExtension(path));
            if (!m.Success) continue;   // no number prefix: disabled tile

            // "_" and "-" become spaces; runs of spaces collapse to one.
            var label = Regex.Replace(m.Groups[2].Value.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
            if (label.Length == 0) continue;

            // Very long digit runs can't be a long; they sort last.
            var number = long.TryParse(m.Groups[1].Value, out var n) ? n : long.MaxValue;
            found.Add(new Candidate(path, Path.GetFileName(path), number, label));
        }

        var ordered = found.OrderBy(c => c.Number)
                           .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
                           .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Candidate>();
        foreach (var c in ordered)
        {
            if (seen.Add(c.Label)) result.Add(c);
            else Log("Duplicate", $"{c.FileName}: another picture already makes a \"{c.Label}\" tile; ignored");
        }
        return result;
    }

    /// <summary>Fallback: the Tiles from appsettings.json (or the built-in four), pictures by their Image field.</summary>
    private static TileSet FromSettings(string folder, AppSettings settings)
    {
        var tiles = settings.Tiles.Select(t =>
        {
            Image? picture = null;
            if (!string.IsNullOrWhiteSpace(t.Image))
            {
                picture = TileButton.TryLoadPicture(Path.Combine(folder, t.Image.Trim()));
                if (picture == null) Log("BadImage", $"{t.Image}: missing or unreadable; showing \"{t.Label}\" as text only");
            }
            return new TileEntry(t.Label, t.ResolveColor(), picture, t.Image);
        }).ToList();

        return new TileSet(tiles, settings.HasConfiguredTiles ? "Tiles in appsettings.json" : "built-in tiles");
    }

    /// <summary>
    /// Columns for the grid. A positive <paramref name="configured"/> value is used as before
    /// (capped at the tile count); 0 = automatic, shaped for the wide 1024x600 panel.
    /// </summary>
    public static int ColumnsFor(int tileCount, int configured)
    {
        if (tileCount <= 0) return 1;
        if (configured > 0) return Math.Min(configured, tileCount);

        return tileCount switch
        {
            <= 3 => tileCount,         // one row: 1x1, 2x1, 3x1
            4 => 2,                    // 2x2
            <= 6 => 3,                 // 3x2
            <= 8 => 4,                 // 4x2
            9 => 3,                    // 3x3
            <= 12 => 4,                // 4x3
            // Beyond 12: about 5 columns for every 3 rows (the panel is ~1.7 times wider than tall).
            _ => (int)Math.Ceiling(Math.Sqrt(tileCount * 5.0 / 3.0))
        };
    }

    /// <summary>
    /// Average color of the picture's visible pixels, mixed 60% toward white so it is a pastel
    /// (each channel at least 153), which keeps the black label readable.
    /// Mostly transparent or white pictures come out near white; then a light gray is used so
    /// the tile still stands out from the white "pressed" flash.
    /// </summary>
    public static Color PastelAverage(Image picture)
    {
        // Sample a small copy: fast, and averaging is what we want anyway.
        using var small = new Bitmap(24, 24);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(picture, 0, 0, small.Width, small.Height);
        }

        double r = 0, gr = 0, b = 0, weight = 0;
        for (int y = 0; y < small.Height; y++)
            for (int x = 0; x < small.Width; x++)
            {
                var p = small.GetPixel(x, y);
                if (p.A < 32) continue;   // transparent background: ignore
                // Near-white pixels (paper background) say little about the picture: count them less.
                double w = p.A / 255.0 * (p.R + p.G + p.B > 720 ? 0.2 : 1.0);
                r += p.R * w; gr += p.G * w; b += p.B * w; weight += w;
            }

        if (weight == 0) return Color.LightGray;
        static int Pastel(double v) => (int)Math.Round(255 - (255 - v) * 0.4);
        var c = Color.FromArgb(Pastel(r / weight), Pastel(gr / weight), Pastel(b / weight));
        return c.R + c.G + c.B > 740 ? Color.LightGray : c;
    }

    /// <summary>"#RRGGBB" or a known color name; blank or unknown is false.</summary>
    private static bool TryParseColor(string? text, out Color color)
    {
        color = Color.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { color = ColorTranslator.FromHtml(text.Trim()); }
        catch (Exception) { return false; }
        return !color.IsEmpty && color.A == 255;
    }
}

/// <summary>
/// TODO #10: watches the Resources folder and raises <see cref="Changed"/> once things settle
/// (about 1 s after the last create/delete/rename/change), so copying in several pictures
/// causes one reload, not many. The event comes on a thread-pool thread.
/// If the folder doesn't exist, nothing is watched (use "Reload tiles" in Settings after creating it).
/// </summary>
public sealed class TileFolderWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _settle;
    private readonly TimeSpan _delay;

    /// <summary>Raised once after a burst of changes in the folder.</summary>
    public event EventHandler? Changed;

    public TileFolderWatcher(string folder, TimeSpan delay)
    {
        _delay = delay;
        _settle = new System.Threading.Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
        if (!Directory.Exists(folder))
        {
            TileCatalog.Log("Watcher", "not watching (folder not found): " + folder);
            return;
        }

        _watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Changed += OnEvent;
        _watcher.Renamed += OnEvent;
        _watcher.Error += (_, e) =>
        {
            // Buffer overflow etc.: events were lost, so just reload everything.
            TileCatalog.Log("Watcher", "error: " + e.GetException().Message);
            Restart();
        };
        _watcher.EnableRaisingEvents = true;
    }

    private void OnEvent(object? sender, FileSystemEventArgs e) => Restart();

    /// <summary>(Re)starts the settle timer: Changed fires once, _delay after the last event.</summary>
    private void Restart() => _settle.Change(_delay, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _watcher?.Dispose();
        _settle.Dispose();
    }
}

/// <summary>Developer check of TODO #10 (no UI): prints the tiles and grid a folder produces.</summary>
public static class TileSelfTest
{
    /// <summary>
    /// "--selftest-tiles [folder] [watchSeconds]": prints label, color, source and grid for the folder
    /// (default: Resources next to the exe). With watchSeconds, keeps watching and prints the tiles
    /// again after each settled change, so live reload can be tested by editing files meanwhile.
    /// </summary>
    public static int Run(IEnumerable<string> args)
    {
        var list = args.ToList();
        var folder = list.Count > 0 ? Path.GetFullPath(list[0]) : TileCatalog.DefaultFolder;
        int watchSeconds = list.Count > 1 && int.TryParse(list[1], out var s) ? s : 0;
        var settings = AppSettings.Load();

        Print(folder, settings, "Initial");
        if (watchSeconds <= 0) return 0;

        using var watcher = new TileFolderWatcher(folder, TimeSpan.FromSeconds(1));
        watcher.Changed += (_, _) => Print(folder, settings, "Reloaded");
        Console.WriteLine($"Watching for {watchSeconds} s...");
        Thread.Sleep(TimeSpan.FromSeconds(watchSeconds));
        return 0;
    }

    private static readonly object _printLock = new();

    private static void Print(string folder, AppSettings settings, string heading)
    {
        var set = TileCatalog.Load(folder, settings);
        int columns = TileCatalog.ColumnsFor(set.Tiles.Count, settings.Columns);
        int rows = (int)Math.Ceiling((double)set.Tiles.Count / columns);
        lock (_printLock)
        {
            Console.WriteLine($"{heading} at {DateTime.Now:HH:mm:ss.fff}: {set.Tiles.Count} tile(s) from {set.Origin}; grid {columns}x{rows}");
            foreach (var t in set.Tiles)
                Console.WriteLine($"  {t.Label,-14} #{t.Color.R:X2}{t.Color.G:X2}{t.Color.B:X2}  {(t.Picture == null ? "text only" : "picture")}  {t.Source}");
        }
        set.DisposePictures();
    }
}
