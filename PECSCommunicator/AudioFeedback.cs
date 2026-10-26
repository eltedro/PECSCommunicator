using System.Media;
using System.Speech.Synthesis;

namespace PECSCommunicator;

/// <summary>
/// TODO #11: spoken feedback on the Communicator itself, like Ian's PECS device at school:
/// each tap says the tile's word ("Eat", "Help") through the default audio device.
///
///   - A tile with its own recording (e.g. Resources\03_Walk.wav next to 03_Walk.png) plays that
///     instead of the voice, so a familiar voice can be recorded per tile.
///   - Optional short chime before the word (off by default: the school device just says the word).
///   - A new tap stops whatever is still playing, so rapid taps never queue up.
///   - Never blocks the UI or the send to the listeners, and never throws: if Windows speech is
///     unavailable the app keeps running silently (logged to the tiles log).
/// Must be created and used on the UI thread (SpeechSynthesizer's async calls post back to it).
/// </summary>
public sealed class AudioFeedback : IDisposable
{
    private readonly AudioFeedbackSettings _settings;
    private readonly SpeechSynthesizer? _synth;
    private readonly SoundPlayer _player = new();
    private MemoryStream? _chime;

    // Cancels the previous tap's pending speech (the part that waits for the chime to finish).
    private CancellationTokenSource? _pending;

    /// <summary>Turned on/off from Settings; when off, taps are silent.</summary>
    public bool Enabled { get; set; }

    /// <summary>Name of the voice in use, or null if speech is unavailable (for the self-test).</summary>
    public string? VoiceName => _synth?.Voice.Name;

    public AudioFeedback(AudioFeedbackSettings settings)
    {
        _settings = settings;
        Enabled = settings.Enabled;
        try
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();
            _synth.Volume = settings.Volume;   // 0-100
            _synth.Rate = settings.Rate;       // -10 (slow) .. 10 (fast)
        }
        catch (Exception ex)
        {
            _synth?.Dispose();
            _synth = null;   // keep running without speech
            TileCatalog.Log("Audio", "speech unavailable: " + ex.Message);
        }
    }

    /// <summary>
    /// Plays the feedback for a tap: the tile's .wav if <paramref name="wavPath"/> exists, else
    /// (optional chime, then) the spoken word. Returns at once; stops any earlier feedback first.
    /// </summary>
    public void Play(string label, string? wavPath)
    {
        if (!Enabled) return;
        Stop();

        try
        {
            // Own recording wins over the voice (and the chime, which would clash with it).
            if (wavPath != null && File.Exists(wavPath))
            {
                _player.Stream = null;
                _player.SoundLocation = wavPath;
                _player.Play();   // async; plays on its own thread
                return;
            }

            int speakAfterMs = 0;
            if (_settings.Chime)
            {
                _chime ??= MakeChime(_settings.Volume);
                _chime.Position = 0;
                _player.SoundLocation = "";
                _player.Stream = _chime;
                _player.Play();
                speakAfterMs = ChimeMs;   // let the chime finish before the word
            }

            if (_settings.Speak && _synth != null)
                _ = SpeakAfterAsync(_settings.SpeechTemplate.Replace("{tile}", label, StringComparison.OrdinalIgnoreCase), speakAfterMs);
        }
        catch (Exception ex)
        {
            TileCatalog.Log("Audio", $"feedback for {label} failed: {ex.Message}");
        }
    }

    /// <summary>Waits for the chime (if any), then speaks, unless a newer tap came first.</summary>
    private async Task SpeakAfterAsync(string text, int delayMs)
    {
        var cts = _pending = new CancellationTokenSource();
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);   // resumes on the UI thread
            _synth!.SpeakAsync(text);
        }
        catch (OperationCanceledException) { /* a newer tap took over */ }
        catch (Exception ex) { TileCatalog.Log("Audio", "speech failed: " + ex.Message); }
    }

    /// <summary>Stops the sound, pending speech and any speech in progress.</summary>
    public void Stop()
    {
        _pending?.Cancel();
        _pending = null;
        try
        {
            _player.Stop();
            _synth?.SpeakAsyncCancelAll();
        }
        catch (Exception ex) { TileCatalog.Log("Audio", "stop failed: " + ex.Message); }
    }

    /// <summary>For the self-test: speaks synchronously (or plays the .wav to the end).</summary>
    public void PlaySync(string label, string? wavPath)
    {
        if (wavPath != null && File.Exists(wavPath))
        {
            _player.SoundLocation = wavPath;
            _player.PlaySync();
            return;
        }
        if (_settings.Chime)
        {
            _chime ??= MakeChime(_settings.Volume);
            _chime.Position = 0;
            _player.Stream = _chime;
            _player.PlaySync();
        }
        if (_settings.Speak) _synth?.Speak(_settings.SpeechTemplate.Replace("{tile}", label, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The tile's own recording: same name as its picture, with .wav (e.g. 03_Walk.wav).</summary>
    public static string? WavFor(string folder, string pictureFile)
    {
        if (string.IsNullOrWhiteSpace(pictureFile)) return null;
        var wav = Path.Combine(folder, Path.GetFileNameWithoutExtension(pictureFile) + ".wav");
        return File.Exists(wav) ? wav : null;
    }

    private const int ChimeMs = 180;

    /// <summary>
    /// A soft two-note "ding" (E6 then A6, 90 ms each) as an in-memory 16-bit mono WAV:
    /// sine with a quick attack and exponential fade, so it never clicks.
    /// </summary>
    private static MemoryStream MakeChime(int volume)
    {
        const int rate = 22050;
        int[] notes = [1319, 1760];
        int perNote = rate * (ChimeMs / notes.Length) / 1000;
        var samples = new short[perNote * notes.Length];
        double amp = 0.35 * Math.Clamp(volume, 0, 100) / 100.0 * short.MaxValue;
        for (int n = 0; n < notes.Length; n++)
            for (int i = 0; i < perNote; i++)
            {
                double t = (double)i / rate;
                double env = Math.Min(1, i / (rate * 0.004)) * Math.Exp(-t * 18);
                samples[n * perNote + i] = (short)(amp * env * Math.Sin(2 * Math.PI * notes[n] * t));
            }

        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(dataBytes);
            foreach (var s in samples) w.Write(s);
        }
        ms.Position = 0;
        return ms;
    }

    public void Dispose()
    {
        Stop();
        _synth?.Dispose();
        _player.Dispose();
        _chime?.Dispose();
    }
}

/// <summary>TODO #11: the "AudioFeedback" section of appsettings.json; every value has a default.</summary>
public sealed record AudioFeedbackSettings
{
    /// <summary>Master switch (also the "Audio feedback" checkbox in Settings).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Speak the tile's word (unless the tile has its own .wav).</summary>
    public bool Speak { get; init; } = true;

    /// <summary>What is spoken; "{tile}" becomes the tile label.</summary>
    public string SpeechTemplate { get; init; } = "{tile}";

    /// <summary>Voice (and chime) volume, 0-100.</summary>
    public int Volume { get; init; } = 100;

    /// <summary>Speaking rate, -10 (slow) to 10 (fast); 0 = normal.</summary>
    public int Rate { get; init; } = -1;

    /// <summary>Short chime before the word.</summary>
    public bool Chime { get; init; }

    /// <summary>Also give feedback for repeat taps the debounce doesn't send (they look the same to Ian).</summary>
    public bool SpeakRepeats { get; init; } = true;
}
