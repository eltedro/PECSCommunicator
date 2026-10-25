using System.Text;
using Android.Content;
using Android.Util;

namespace PECSListenerAndroid;

/// <summary>
/// TODO #4: append-only daily CSV log, same format as the Windows Listener's
/// %LOCALAPPDATA%\PECS\Logs\listener-yyyy-MM-dd.csv: header row on a new file, RFC 4180 escaping,
/// UTF-8 without BOM, CRLF line endings (so files from the phone and the laptop match byte-for-byte).
///
/// Location: Context.GetExternalFilesDir(null)/Logs, i.e. on the phone
///   /sdcard/Android/data/com.pecs.listener/files/Logs/listener-yyyy-MM-dd.csv
/// No storage permission is needed; reachable with "adb pull" or (on most phones) USB file transfer
/// under Internal storage\Android\data\com.pecs.listener\files\Logs. Deleted if the app is uninstalled.
///
/// Thread-safe; each row is opened, appended and flushed; never throws (logging must not disturb alerts).
/// </summary>
public sealed class CsvLog
{
    private const string Newline = "\r\n";

    private readonly string _folder;
    private readonly string _prefix;
    private readonly string _headerLine;
    private readonly object _lock = new();

    public CsvLog(Context context, string prefix, params string[] columns)
    {
        var baseDir = context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir!.AbsolutePath;
        _folder = Path.Combine(baseDir, "Logs");
        _prefix = prefix;
        _headerLine = string.Join(",", columns.Select(Escape));
    }

    /// <summary>Path of today's file (a new file starts after midnight).</summary>
    public string CurrentPath => Path.Combine(_folder, $"{_prefix}-{DateTime.Now:yyyy-MM-dd}.csv");

    /// <summary>Appends one row. Values are converted with ToString(); null becomes an empty field.</summary>
    public void Append(params object?[] fields)
    {
        var line = string.Join(",", fields.Select(f => Escape(f?.ToString() ?? "")));
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                using var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (stream.Length == 0) writer.Write(_headerLine + Newline);
                writer.Write(line + Newline);
                writer.Flush();
            }
            catch (Exception ex) { Log.Warn(PressAlert.Tag, $"CsvLog: row skipped: {ex.Message}"); }
        }
    }

    /// <summary>Data rows of today's file (header skipped), oldest first; empty if none. Never throws.</summary>
    public List<string[]> ReadToday()
    {
        var rows = new List<string[]>();
        lock (_lock)
        {
            try
            {
                if (!File.Exists(CurrentPath)) return rows;
                var all = ParseCsv(File.ReadAllText(CurrentPath));
                rows.AddRange(all.Skip(1));   // first row is the header
            }
            catch (Exception ex) { Log.Warn(PressAlert.Tag, $"CsvLog: read failed: {ex.Message}"); }
        }
        return rows;
    }

    /// <summary>CSV escaping: quote a field containing a comma, quote, or line break, doubling inner quotes.</summary>
    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>Minimal RFC 4180 reader (the inverse of Escape): quoted fields, "" inside quotes, CRLF or LF rows.</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                fields.Add(field.ToString()); field.Clear();
                rows.Add(fields.ToArray()); fields.Clear();
            }
            else if (c != '\r') field.Append(c);
        }
        if (field.Length > 0 || fields.Count > 0) { fields.Add(field.ToString()); rows.Add(fields.ToArray()); }
        return rows;
    }
}