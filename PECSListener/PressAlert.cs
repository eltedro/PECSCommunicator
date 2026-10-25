using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;

namespace PECSListener;

/// <summary>
/// TODO #9: makes a press hard to miss: the tile's sound, then (optionally) speech, a flashing
/// taskbar button, and optionally bringing the window to the front. Only POSTed presses call this;
/// health-check GETs never do.
/// Problems never throw; they go to Debug output and %TEMP%\pecs_listener_diag.txt.
/// </summary>
public sealed class PressAlert : IDisposable
{
    private readonly Form _window;
    private readonly ListenerSettings _settings;
    private readonly SpeechSynthesizer? _synth;
    private readonly object _soundLock = new();
    private SoundPlayer? _player;
    private int _generation; // bumped per press so a stale press doesn't speak after a newer one

    public static string DiagPath => Path.Combine(Path.GetTempPath(), "pecs_listener_diag.txt");

    public PressAlert(Form window, ListenerSettings settings)
    {
        _window = window;
        _settings = settings;
        if (!settings.Speak) return;
        try
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();
            _synth.SpeakCompleted += (_, e) => { if (e.Error != null) Diag("Speech error: " + e.Error.Message); };
            Diag($"Speech ready, voice: {_synth.Voice.Name}");
        }
        catch (Exception ex)
        {
            _synth = null; // keep running without speech
            Diag("Speech unavailable: " + ex.Message);
        }
    }

    /// <summary>Call on the UI thread for each received press.</summary>
    public void OnPress(string tile)
    {
        if (_settings.FlashTaskbar) Flash();
        if (_settings.BringToFront) BringToFront();

        // Sound then speech on a worker thread so the UI and the HTTP loop never wait.
        int generation = Interlocked.Increment(ref _generation);
        var wav = ResolveSound(tile);
        var text = _settings.SpeechTemplate.Replace("{tile}", tile, StringComparison.OrdinalIgnoreCase);
        _ = Task.Run(() =>
        {
            PlaySound(wav, tile);

            // Rapid presses: only the newest one speaks, and it cuts off anything still being said.
            if (_synth == null || generation != Volatile.Read(ref _generation)) return;
            try
            {
                _synth.SpeakAsyncCancelAll();
                _synth.SpeakAsync(text);
                Diag($"Press {tile}: sound {(wav ?? "Asterisk")}, speaking \"{text}\"");
            }
            catch (Exception ex) { Diag("Speech failed: " + ex.Message); }
        });
    }

    /// <summary>Tile's .wav, else "Default", else null (system Asterisk). Missing files count as not set.</summary>
    private string? ResolveSound(string tile)
    {
        foreach (var key in new[] { tile, "Default" })
        {
            if (!_settings.Sounds.TryGetValue(key, out var path)) continue;
            if (File.Exists(path)) return path;
            Diag($"Sound for {key} not found: {path}");
        }
        return null;
    }

    /// <summary>Plays the wav to the end (blocking this worker), stopping any previous one first.</summary>
    private void PlaySound(string? wav, string tile)
    {
        if (wav == null) { SystemSounds.Asterisk.Play(); return; }
        try
        {
            SoundPlayer player;
            lock (_soundLock)
            {
                _player?.Stop();
                _player?.Dispose();
                _player = player = new SoundPlayer(wav);
            }
            player.PlaySync();
        }
        catch (Exception ex)
        {
            Diag($"Sound for {tile} failed ({ex.Message}); using Asterisk");
            SystemSounds.Asterisk.Play();
        }
    }

    // --- Taskbar flash -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    private const uint FLASHW_ALL = 3;        // caption + taskbar button
    private const uint FLASHW_TIMERNOFG = 12; // keep flashing until the window comes to the foreground

    private void Flash()
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = _window.Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    /// <summary>
    /// Restores and activates the window. Windows' focus rules usually stop a background app from
    /// taking the foreground, in which case it only flashes; that's expected, not an error.
    /// </summary>
    private void BringToFront()
    {
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
        SetForegroundWindow(_window.Handle);
    }

    private static void Diag(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        Debug.WriteLine("PECSListener: " + line);
        try { File.AppendAllText(DiagPath, line + Environment.NewLine); } catch { /* diagnostics only */ }
    }

    public void Dispose()
    {
        lock (_soundLock) { _player?.Dispose(); }
        _synth?.Dispose();
    }
}
