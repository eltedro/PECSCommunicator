using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;

namespace PECSListener;

/// <summary>
/// TODO #9: makes a press hard to miss: the tile's sound, (optionally) speech, a flashing taskbar
/// button, and optionally bringing the window to the front. Only POSTed presses call this;
/// health-check GETs never do.
/// Timing: the sound starts at once without waiting for it to finish, and the speech starts
/// SpeechDelayMs later (0 = together with the sound), so the voice comes in over the tail of the sound.
/// Problems never throw; they go to Debug output and %TEMP%\pecs_listener_diag.txt.
/// </summary>
public sealed class PressAlert : IDisposable
{
    private readonly Form _window;
    private readonly ListenerSettings _settings;
    private readonly SpeechSynthesizer? _synth;
    private SoundPlayer? _player;

    // Cancels the previous press's pending (delayed) speech when a newer press arrives.
    private CancellationTokenSource? _pendingSpeech;

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
            Diag($"Speech ready, voice: {_synth.Voice.Name}, delay {settings.SpeechDelayMs} ms");
        }
        catch (Exception ex)
        {
            _synth = null; // keep running without speech
            Diag("Speech unavailable: " + ex.Message);
        }
    }

    /// <summary>
    /// Call on the UI thread for each received press. Rapid presses: the newest press stops the previous
    /// sound, cancels its pending speech and any speech in progress, so only the newest press speaks.
    /// </summary>
    public void OnPress(string tile)
    {
        var clock = Stopwatch.StartNew(); // offsets in the diag log are from the moment the press arrived

        if (_settings.FlashTaskbar) Flash();
        if (_settings.BringToFront) BringToFront();

        // Supersede the previous press.
        _pendingSpeech?.Cancel();
        _pendingSpeech?.Dispose();
        _pendingSpeech = null;
        try { _synth?.SpeakAsyncCancelAll(); } catch (Exception ex) { Diag("Speech cancel failed: " + ex.Message); }

        var wav = ResolveSound(tile);
        StartSound(wav, tile);
        Diag($"Press {tile}: sound {(wav ?? "Asterisk")} started at +{clock.ElapsedMilliseconds} ms");

        if (_synth == null) return; // Speak is off (or no voice): sound only

        var text = _settings.SpeechTemplate.Replace("{tile}", tile, StringComparison.OrdinalIgnoreCase);
        _pendingSpeech = new CancellationTokenSource();
        _ = SpeakAfterDelayAsync(tile, text, _settings.SpeechDelayMs, _pendingSpeech.Token, clock);
    }

    /// <summary>Waits the delay (unless a newer press cancels it), then speaks. Runs on the UI thread.</summary>
    private async Task SpeakAfterDelayAsync(string tile, string text, int delayMs, CancellationToken token, Stopwatch clock)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, token);
            token.ThrowIfCancellationRequested();
            _synth!.SpeakAsync(text); // returns immediately; speech plays in the background
            Diag($"Press {tile}: speaking \"{text}\" started at +{clock.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException)
        {
            Diag($"Press {tile}: speech cancelled by a newer press at +{clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Diag("Speech failed: " + ex.Message);
        }
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

    /// <summary>Stops any previous sound and starts this one without waiting for it to finish.</summary>
    private void StartSound(string? wav, string tile)
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;

        if (wav == null) { SystemSounds.Asterisk.Play(); return; }
        try
        {
            _player = new SoundPlayer(wav);
            _player.Play(); // asynchronous playback
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
        _pendingSpeech?.Cancel();
        _pendingSpeech?.Dispose();
        _player?.Dispose();
        _synth?.Dispose();
    }
}
