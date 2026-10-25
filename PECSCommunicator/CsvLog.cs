using System.Diagnostics;
using System.Text;

namespace PECSCommunicator;

/// <summary>
/// Tiny append-only daily CSV log in %LOCALAPPDATA%\PECS\Logs\&lt;prefix&gt;-yyyy-MM-dd.csv.
/// Thread-safe, writes a header row when it starts a new file, escapes fields per RFC 4180,
/// and never throws: logging must never disturb the UI.
/// (The same small class lives in both PECS projects; it is too small to justify a shared library.)
/// </summary>
public sealed class CsvLog
{
    /// <summary>Folder that holds all PECS logs.</summary>
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PECS", "Logs");

    private readonly string _prefix;
    private readonly string _headerLine;
    private readonly object _lock = new();

    public CsvLog(string prefix, params string[] columns)
    {
        _prefix = prefix;
        _headerLine = string.Join(",", columns.Select(Escape));
    }

    /// <summary>
    /// Path of today's file (a new file starts after midnight).
    /// If today's file was started with different columns (e.g. by an older version of the app),
    /// rows go to "&lt;prefix&gt;-yyyy-MM-dd-2.csv" (then -3, ...) so one file never mixes layouts.
    /// </summary>
    public string CurrentPath
    {
        get
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            if (_cachedDate == date && _cachedPath != null) return _cachedPath;

            for (int n = 1; ; n++)
            {
                var path = Path.Combine(Folder, n == 1 ? $"{_prefix}-{date}.csv" : $"{_prefix}-{date}-{n}.csv");
                if (!File.Exists(path) || ReadFirstLine(path) is null or "" || ReadFirstLine(path) == _headerLine)
                {
                    (_cachedDate, _cachedPath) = (date, path);
                    return path;
                }
            }
        }
    }

    private string? _cachedDate, _cachedPath;

    /// <summary>First line of a file (shared read, so it works while Excel has it open); null if unreadable.</summary>
    private static string? ReadFirstLine(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadLine();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CsvLog: could not read header of {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Appends one row. Values are converted with ToString(); null becomes an empty field.</summary>
    public void Append(params object?[] fields)
    {
        var line = string.Join(",", fields.Select(f => Escape(f?.ToString() ?? "")));
        lock (_lock)
        {
            // The file may be open in Excel/Notepad; retry briefly, then give up on this row.
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var path = CurrentPath;
                    // FileShare.ReadWrite: other programs may keep the file open while we append.
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    if (stream.Length == 0) writer.WriteLine(_headerLine);
                    writer.WriteLine(line);
                    return;
                }
                catch (IOException ex) when (attempt < 3)
                {
                    Debug.WriteLine($"CsvLog: retrying after {ex.Message}");
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"CsvLog: row skipped: {ex.Message}");
                    return;
                }
            }
        }
    }

    /// <summary>Opens the log folder in Explorer (creating it if needed). Never throws.</summary>
    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CsvLog: could not open folder: {ex.Message}");
        }
    }

    /// <summary>CSV escaping: quote a field containing a comma, quote, or line break, doubling inner quotes.</summary>
    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
