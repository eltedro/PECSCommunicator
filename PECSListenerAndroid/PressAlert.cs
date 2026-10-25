using System.Diagnostics;
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Speech.Tts;
using Android.Util;

namespace PECSListenerAndroid;

/// <summary>
/// TODO #3: makes a press hard to miss, from the service so it works with the screen off:
/// the tile's chime, speech ("Ian wants Walk"), a short vibration, and a heads-up notification
/// in the tile's color. Mirrors the Windows Listener's PressAlert:
///   - the chime starts at once (non-blocking); speech starts SpeechDelayMs later;
///   - a newer press stops the previous chime and cancels pending or ongoing speech.
/// Sound and speech use USAGE_NOTIFICATION_EVENT, so they follow the phone's notification volume
/// and Do Not Disturb. The press notification channel is silent (no double sound).
/// Logcat tag "PECS"; offsets are from the moment the press arrived. Problems never throw.
/// </summary>
public sealed class PressAlert : Java.Lang.Object, TextToSpeech.IOnInitListener
{
    public const string Tag = "PECS";
    private const string ChannelId = "presses";
    private const int NotificationId = 2;   // 1 is the "running" notification
    private const int SampleRate = 44100;

    private readonly Context _context;
    private readonly ListenerSettings _settings;
    private readonly AudioAttributes _audio = new AudioAttributes.Builder()
        .SetUsage(AudioUsageKind.NotificationEvent)!
        .SetContentType(AudioContentType.Sonification)!
        .Build()!;
    private readonly object _gate = new();

    private TextToSpeech? _tts;
    private bool _ttsReady;
    private AudioTrack? _track;
    private CancellationTokenSource? _pendingSpeech;   // cancels the previous press's delayed speech

    public PressAlert(Context context, ListenerSettings settings)
    {
        _context = context;
        _settings = settings;
        CreateChannel();
        if (settings.Speak) _tts = new TextToSpeech(context, this);   // initialized once; OnInit reports readiness
    }

    public void OnInit(OperationResult status)
    {
        _ttsReady = status == OperationResult.Success;
        if (_ttsReady) _tts!.SetAudioAttributes(_audio);
        Log.Info(Tag, $"TTS init: {status}, delay {_settings.SpeechDelayMs} ms");
    }

    /// <summary>Call for each received press (any thread). The newest press supersedes the previous one.</summary>
    public void OnPress(string tile)
    {
        var clock = Stopwatch.StartNew();
        lock (_gate)
        {
            // Supersede the previous press.
            if (_pendingSpeech != null) { _pendingSpeech.Cancel(); _pendingSpeech.Dispose(); _pendingSpeech = null; }
            if (_tts?.IsSpeaking == true) { _tts.Stop(); Log.Info(Tag, "Speech stopped by a newer press"); }

            StartSound(tile);
            Log.Info(Tag, $"Press {tile}: sound started at +{clock.ElapsedMilliseconds} ms");

            if (_settings.Vibrate) Vibrate();
            PostNotification(tile);
            Log.Info(Tag, $"Press {tile}: notification posted at +{clock.ElapsedMilliseconds} ms");

            if (_tts == null) return;
            var text = _settings.SpeechTemplate.Replace("{tile}", tile, StringComparison.OrdinalIgnoreCase);
            _pendingSpeech = new CancellationTokenSource();
            _ = SpeakAfterDelayAsync(tile, text, _pendingSpeech.Token, clock);
        }
    }

    private async Task SpeakAfterDelayAsync(string tile, string text, CancellationToken token, Stopwatch clock)
    {
        try
        {
            if (_settings.SpeechDelayMs > 0) await Task.Delay(_settings.SpeechDelayMs, token);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!_ttsReady) { Log.Warn(Tag, $"Press {tile}: TTS not ready, not speaking"); return; }
                _tts!.Speak(text, QueueMode.Flush, null, "press");
            }
            Log.Info(Tag, $"Press {tile}: speaking \"{text}\" started at +{clock.ElapsedMilliseconds} ms");
        }
        catch (System.OperationCanceledException)
        {
            Log.Info(Tag, $"Press {tile}: speech canceled by a newer press at +{clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { Log.Warn(Tag, "Speech failed: " + ex.Message); }
    }

    // ---------- Sound: generated chime ----------

    /// <summary>Stops any previous chime and starts this tile's, without waiting for it to finish.</summary>
    private void StartSound(string tile)
    {
        try
        {
            if (_track != null) { _track.Stop(); _track.Release(); _track = null; }

            var notes = _settings.SoundFor(tile);
            if (notes.Length == 0) return;
            var pcm = Chime(notes, _settings.SoundNoteMs);

            _track = new AudioTrack.Builder()
                .SetAudioAttributes(_audio)
                .SetAudioFormat(new AudioFormat.Builder()
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetSampleRate(SampleRate)!
                    .SetChannelMask(ChannelOut.Mono)!
                    .Build()!)
                .SetTransferMode(AudioTrackMode.Static)
                .SetBufferSizeInBytes(pcm.Length * 2)
                .Build();
            _track.Write(pcm, 0, pcm.Length);
            _track.Play();
        }
        catch (Exception ex) { Log.Warn(Tag, $"Sound for {tile} failed: {ex.Message}"); }
    }

    /// <summary>
    /// Builds a soft bell-like chime: each note is a sine with its 2nd harmonic, a 5 ms attack and an
    /// exponential decay, so notes are clearly separated and there are no clicks.
    /// </summary>
    private static short[] Chime(int[] notesHz, int noteMs)
    {
        int perNote = SampleRate * noteMs / 1000;
        int tail = SampleRate * 250 / 1000;   // let the last note ring out
        var pcm = new short[perNote * notesHz.Length + tail];
        int attack = SampleRate * 5 / 1000;

        for (int n = 0; n < notesHz.Length; n++)
        {
            int length = n == notesHz.Length - 1 ? perNote + tail : perNote;
            for (int i = 0; i < length; i++)
            {
                double t = (double)i / SampleRate;
                double env = Math.Min(1.0, (double)i / attack) * Math.Exp(-t * 6);
                double w = 2 * Math.PI * notesHz[n] * t;
                double v = (Math.Sin(w) + 0.3 * Math.Sin(2 * w)) / 1.3 * env * 0.8;
                pcm[n * perNote + i] = (short)(v * short.MaxValue);
            }
        }
        return pcm;
    }

    // ---------- Vibration ----------

    private void Vibrate()
    {
        try
        {
            Vibrator? vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? ((VibratorManager?)_context.GetSystemService(Context.VibratorManagerService))?.DefaultVibrator
#pragma warning disable CA1422 // pre-API-31 path
                : (Vibrator?)_context.GetSystemService(Context.VibratorService);
#pragma warning restore CA1422
            // Two short buzzes: off 0, on 200, off 120, on 200.
            vibrator?.Vibrate(VibrationEffect.CreateWaveform([0, 200, 120, 200], -1));
        }
        catch (Exception ex) { Log.Warn(Tag, "Vibrate failed: " + ex.Message); }
    }

    // ---------- Notification ----------

    private void CreateChannel()
    {
        var channel = new NotificationChannel(ChannelId, "Tile presses", NotificationImportance.High)
        {
            Description = "Heads-up alert for each press. Silent: the app plays the tile sound itself."
        };
        channel.SetSound(null, null);      // the chime is played by StartSound
        channel.EnableVibration(false);    // and the vibration by Vibrate
        ((NotificationManager)_context.GetSystemService(Context.NotificationService)!).CreateNotificationChannel(channel);
    }

    /// <summary>Heads-up "Ian pressed: tile" in the tile color; replaces the previous press notification.</summary>
    private void PostNotification(string tile)
    {
        try
        {
            var openApp = PendingIntent.GetActivity(_context, 0,
                new Intent(_context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            var notification = new Notification.Builder(_context, ChannelId)
                .SetContentTitle($"Ian pressed: {tile}")
                .SetContentText(DateTime.Now.ToString("HH:mm:ss"))
                .SetSmallIcon(Android.Resource.Drawable.StatNotifyChat)
                .SetColor(_settings.BannerColorFor(tile).ToArgb())
                .SetCategory(Notification.CategoryMessage)
                .SetContentIntent(openApp)
                .SetAutoCancel(true)
                .Build();
            ((NotificationManager)_context.GetSystemService(Context.NotificationService)!).Notify(NotificationId, notification);
        }
        catch (Exception ex) { Log.Warn(Tag, "Notification failed: " + ex.Message); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pendingSpeech?.Cancel();
            _track?.Release();
            _tts?.Shutdown();
        }
        base.Dispose(disposing);
    }
}
