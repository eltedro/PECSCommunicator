/**
 * Project: PECSCommunicator
 * File: MainForm.cs
 * This file now encoded in UTF-8.
 * Initial history: https://x.com/i/grok/share/c2hhcmQtMw_69196335-2278-4bf2-b2c5-bf6f369e5fd3
 * 
 */

/**
 * Application CONOP: SEE: PECS_Grok_History.md.
 * 
 * TODO: 
 * [x] what do I need to do to make PECSCommunicator and PECSListener work on different computers on the same network? 
 * [x] 1. move tile definitions (label, color, image file) into appsettings.json so tiles change without a rebuild
 * [x] 2. add pictures to the Resources folder matching the tile names; show picture with a small label beneath (data-driven via #1)
 * [x] 3. add a hidden Settings screen (e.g. 3-second corner press + keyboard shortcut) to change the listener URL without editing appsettings.json
 * [x] 4. add a "Test Connection" button inside Settings to verify the listener is reachable before saving
 * [x] 5. show a subtle adult-visible status indicator when the listener is unreachable; retry a failed send once
 * [x] 6. log every press (incl. failed sends) to a dated CSV on both Communicator and Listener
 * [x] 7. debounce rapid repeat taps of the same tile (~2 s) so the listener isn't flooded; still log them
 * [x] 8. if exactly three displays are detected, open full-screen on the 7" display; otherwise open in a borderless 1024x600 window. Auto-start with Windows.
 * [x] 9. make the Listener harder to miss: per-tile sound, flash taskbar button, and/or bring window to front
 * [x] 10. Make the number of tiles/buttons dynamic by reading the .png files in the Resources folder and using their names as the labels. (The color can be a default or derived from the image.)
 * [x] 11. The Communicator needs to provide audio feedback for Ian in addition to the current visual. This emulates the PECS device he uses at school. It should just be a single spoken word, e.g. "Eat", "Help", etc.
 * [x] 12. During testing evening of 10/25/2026, after the apps had been running for 20 minutes or so, tapping a tile in the Communicator did not result in a Listener response, and the status bulb shows red.
 * [ ] 13. Implement push notifications through Google (Firebase Cloud Messaging), replacing the current wifi/LAN solution on the phone app.
 * [ ] 14. Create a README.md with instructions for running the Communicator and Listener on different platforms, including network configuration and firewall settings as necessary. Include the touch screen setup commands, e.g. "MultiDigiMon.exe -touch" to set the touch screen and "tabcal.exe DisplayID=\\.\DISPLAY3" to calibrate it.
 */

namespace PECSCommunicator;

public partial class MainForm : Form
{
    // Settings (listener URL, columns, tiles) come from appsettings.json next to the exe,
    // so tiles and the listener address can change without a rebuild.
    private static readonly AppSettings Settings = AppSettings.Load();

    private Label? _overlayLabel;

    // TODO #5: adult-visible connection dot, drawn in the grid's bottom-right padding.
    // Android TODO #5: Partial = some listeners answered, some didn't (amber dot).
    private enum LinkState { Unknown, Ok, Partial, Failed }
    private LinkState _linkState = LinkState.Unknown;
    private string _linkStatusText = "Not checked yet.";
    private const int DotSize = 16;   // px; fits inside the 20 px grid padding
    private const int DotInset = 2;   // px from the bottom-right edge

    // Periodic health check (UI-thread timer; _checking prevents overlapping checks).
    private readonly System.Windows.Forms.Timer _healthTimer = new() { Interval = 30_000 };
    private bool _checking;

    // Hidden adult access: hold the top-left 80x80 px corner for 3 seconds.
    private readonly CornerHoldGesture _cornerHold;

    // TODO #8: where the window was put (shown in Settings). See ScreenPlacement.
    private string _placementText = "";

    public MainForm()
    {
        InitializeComponent();

        // TODO #8: full screen on the 7" panel when 3 displays are attached, else a 1024x600 window.
        // Re-run when displays change (e.g. the panel is plugged in late). SystemEvents is static,
        // so the handler must be removed on close to avoid a leak.
        _placementText = ScreenPlacement.Apply(this, Settings.TouchScreenSize);
        EventHandler onDisplayChange = (_, _) => BeginInvoke(() => _placementText = ScreenPlacement.Apply(this, Settings.TouchScreenSize));
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += onDisplayChange;
        FormClosed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= onDisplayChange;
        // TODO #11: spoken word per tap; created before the tiles, which look up their .wav overrides.
        _audio = new AudioFeedback(Settings.AudioFeedback);
        FormClosed += (_, _) => _audio.Dispose();
        SetupPECSTiles();
        WatchTileFolder();   // TODO #10: live reload when Resources changes
        SetupOverlay();

        _cornerHold = new CornerHoldGesture(this, new Size(80, 80), TimeSpan.FromSeconds(3));
        _cornerHold.HoldCompleted += (_, _) => BeginInvoke(OpenSettings);
        FormClosed += (_, _) => _cornerHold.Dispose(); // remove the app-wide message filter

        // Health check at startup and every 30 s.
        tableLayoutPanel1.Paint += DrawStatusDot;
        _healthTimer.Tick += async (_, _) => await CheckListenerAsync();
        Shown += async (_, _) => { _healthTimer.Start(); await CheckListenerAsync(); };
        FormClosed += (_, _) => _healthTimer.Dispose();
    }

    /// <summary>Runs one health check against the current URL, unless one is already in progress.</summary>
    private async Task CheckListenerAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var outcomes = await ListenerClient.CheckAllAsync(Settings.ListenerUrls);
            SetLinkState(outcomes, "Check");
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// Records the latest outcome (check or send) for all listeners and repaints the dot:
    /// green = all OK, amber = some OK, red = none. The text (shown in Settings) has one line per listener.
    /// </summary>
    private void SetLinkState(IReadOnlyList<ListenerOutcome> outcomes, string source)
    {
        if (IsDisposed) return;
        int ok = outcomes.Count(o => o.Result.Ok);
        _linkState = ok == outcomes.Count ? LinkState.Ok : ok > 0 ? LinkState.Partial : LinkState.Failed;
        _linkStatusText = DescribeOutcomes(outcomes, $"{source} at {DateTime.Now:HH:mm:ss}");
        tableLayoutPanel1.Invalidate(DotBounds());
    }

    /// <summary>"Send at 13:05:01: 1 of 2 OK" then one "OK/FAIL  url  message" line per listener.</summary>
    internal static string DescribeOutcomes(IReadOnlyList<ListenerOutcome> outcomes, string heading) =>
        $"{heading}: {outcomes.Count(o => o.Result.Ok)} of {outcomes.Count} OK" +
        string.Concat(outcomes.Select(o => $"\n{(o.Result.Ok ? "OK  " : "FAIL")}  {o.Url}  {o.Result.Message}"));

    /// <summary>Dot position: bottom-right corner of the grid, inside its padding (never over a tile).</summary>
    private Rectangle DotBounds()
    {
        var c = tableLayoutPanel1.ClientSize;
        return new Rectangle(c.Width - DotSize - DotInset, c.Height - DotSize - DotInset, DotSize, DotSize);
    }

    /// <summary>Paints the status dot: gray = unknown, green = all listeners OK, amber = some, red = none.</summary>
    private void DrawStatusDot(object? sender, PaintEventArgs e)
    {
        var color = _linkState switch
        {
            LinkState.Ok => Color.FromArgb(60, 170, 60),
            LinkState.Partial => Color.FromArgb(230, 160, 0),
            LinkState.Failed => Color.FromArgb(200, 40, 40),
            _ => Color.Gray
        };
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        e.Graphics.FillEllipse(brush, DotBounds());
    }

    /// <summary>Keyboard shortcut for adults: Ctrl+Shift+S opens Settings.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.S))
        {
            OpenSettings();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Shows the Settings dialog; on Save, writes appsettings.json and applies the URLs now.</summary>
    private void OpenSettings()
    {
        using var dlg = new SettingsForm(Settings.ListenerUrls, _linkStatusText, _placementText, reloadTiles: SetupPECSTiles,
            audioFeedback: _audio.Enabled);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            AppSettings.SaveListenerUrls(AppSettings.DefaultPath, dlg.ListenerUrls);
            Settings.ListenerUrls = dlg.ListenerUrls; // takes effect on the next tap
            _ = CheckListenerAsync();                 // refresh the dot for the new list

            // TODO #11: the Audio feedback checkbox applies at once and is saved as AudioFeedback.Enabled.
            if (dlg.AudioFeedback != _audio.Enabled)
            {
                _audio.Enabled = dlg.AudioFeedback;
                if (!_audio.Enabled) _audio.Stop();
                AppSettings.SaveAudioEnabled(AppSettings.DefaultPath, dlg.AudioFeedback);
            }

            // TODO #8: only touch the registry when the checkbox actually changed.
            if (dlg.StartWithWindows != AutoStart.IsEnabled)
                AutoStart.SetEnabled(dlg.StartWithWindows);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings:\n" + ex.Message, "PECS Settings",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // TODO #10: the tiles come from the numbered pictures in Resources (see TileCatalog) and are
    // rebuilt when that folder changes, or from Settings > "Reload tiles".
    private TileFolderWatcher? _tileWatcher;

    // TODO #11: audio feedback, and each tile's own recording (Resources\03_Walk.wav for 03_Walk.png), if any.
    private readonly AudioFeedback _audio;
    private readonly Dictionary<string, string?> _tileWav = new();

    /// <summary>Watches Resources; a settled change rebuilds the grid on the UI thread.</summary>
    private void WatchTileFolder()
    {
        _tileWatcher = new TileFolderWatcher(TileCatalog.DefaultFolder, TimeSpan.FromSeconds(1));
        _tileWatcher.Changed += (_, _) =>
        {
            // Called on a thread-pool thread; the form may be closing.
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => SetupPECSTiles()); }
            catch (InvalidOperationException) { /* handle destroyed while closing */ }
        };
        FormClosed += (_, _) => _tileWatcher.Dispose();
    }

    /// <summary>
    /// Builds (or rebuilds) the tile grid from the Resources folder and the column setting.
    /// Returns a one-line summary (shown in Settings after "Reload tiles").
    /// </summary>
    private string SetupPECSTiles()
    {
        var set = TileCatalog.Load(TileCatalog.DefaultFolder, Settings);
        var tiles = set.Tiles;
        int columns = TileCatalog.ColumnsFor(tiles.Count, Settings.Columns);
        int rows = (int)Math.Ceiling((double)tiles.Count / columns);

        tableLayoutPanel1.SuspendLayout();

        // Remove and dispose the old tiles (TileButton disposes its picture). Controls.Clear alone
        // would leak them on every reload.
        var oldTiles = tableLayoutPanel1.Controls.Cast<Control>().ToList();
        tableLayoutPanel1.Controls.Clear();
        foreach (var old in oldTiles) old.Dispose();

        tableLayoutPanel1.Dock = DockStyle.Fill;
        tableLayoutPanel1.Padding = new Padding(20);

        // Replace the designer's fixed 2x2 styles with equal percentages for any count.
        tableLayoutPanel1.ColumnStyles.Clear();
        tableLayoutPanel1.RowStyles.Clear();
        tableLayoutPanel1.ColumnCount = columns;
        tableLayoutPanel1.RowCount = rows;
        for (int c = 0; c < columns; c++)
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        for (int r = 0; r < rows; r++)
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));

        _tileWav.Clear();
        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            _tileWav[tile.Label] = AudioFeedback.WavFor(TileCatalog.DefaultFolder, Path.GetFileName(tile.Source));
            // Owner-drawn tile: picture with label beneath, or big text if no usable picture.
            var btn = new TileButton
            {
                Text = tile.Label,
                Font = new Font("Segoe UI", 72, FontStyle.Bold),
                BackColor = tile.Color,
                Dock = DockStyle.Fill,
                Margin = new Padding(15),
                FlatStyle = FlatStyle.Flat,
                Tag = tile.Label,
                Picture = tile.Picture   // the button now owns the picture
            };
            btn.Click += Tile_Click;
            tableLayoutPanel1.Controls.Add(btn, i % columns, i / columns);
        }

        tableLayoutPanel1.ResumeLayout();
        tableLayoutPanel1.Invalidate();   // repaint the status dot too

        // Forget debounce times of tiles that are gone, so a re-added tile starts fresh.
        var labels = tiles.Select(t => t.Label).ToHashSet();
        foreach (var gone in _lastSentAt.Keys.Where(k => !labels.Contains(k)).ToList())
            _lastSentAt.Remove(gone);

        return $"{tiles.Count} tile(s) from {set.Origin}, grid {columns}x{rows}";
    }

    private void SetupOverlay()
    {
        _overlayLabel = new Label
        {
            Text = "Sent!",
            Font = new Font("Segoe UI", 72, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(180, 0, 128, 0),  // semi-transparent green
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Visible = false
        };
        Controls.Add(_overlayLabel);
        _overlayLabel.BringToFront();
    }

    // TODO #7: debounce. A tap of the SAME tile within DebounceSeconds of that tile's last sent
    // press is not sent (Ian sometimes taps repeatedly). Different tiles are never debounced.
    // Key = tile label, value = when that tile was last sent (attempted; a failed send still counts).
    private readonly Dictionary<string, DateTime> _lastSentAt = new();

    // Links the "Pressed" row to its result row in the CSV. Short random id, unique across restarts.
    private static string NewPressId() => Guid.NewGuid().ToString("N")[..8];

    private async void Tile_Click(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string label) return;

        // Releasing a completed corner hold must not count as a tap.
        if (_cornerHold.ShouldSuppressClick) return;

        var pressedAt = DateTime.Now;
        var pressId = NewPressId();
        var urls = Settings.ListenerUrls; // capture: Settings may change while the send is in flight

        bool suppressed = Settings.DebounceSeconds > 0
            && _lastSentAt.TryGetValue(label, out var last)
            && (pressedAt - last).TotalSeconds < Settings.DebounceSeconds;

        // Log first, synchronously (a one-line append), so the tap is on disk even if the app
        // is closed or crashes before the send finishes.
        LogPress(pressId, pressedAt, label, suppressed ? "Suppressed" : "Pressed", suppressed,
            suppressed ? $"Repeat within {Settings.DebounceSeconds:0.#} s; not sent" : "", string.Join(" ", urls));

        if (!suppressed)
        {
            _lastSentAt[label] = pressedAt;
            _ = NotifyListenerAsync(pressId, pressedAt, label, urls); // fire-and-forget; logs its own results
        }

        // Child feedback is the same for sent and suppressed taps, so Ian always gets a consistent
        // response and isn't taught that tapping again "does nothing" (adults see it in the log).
        // TODO #11: say the word (or play the tile's .wav) now; it runs in the background, after the
        // send has started, and a newer tap cuts it off. SpeakRepeats=false makes debounced taps silent.
        if (!suppressed || Settings.AudioFeedback.SpeakRepeats)
            _audio.Play(label, _tileWav.GetValueOrDefault(label));
        var originalColor = btn.BackColor;
        btn.BackColor = Color.White;
        await Task.Delay(150);
        btn.BackColor = originalColor;

        if (_overlayLabel != null)
        {
            _overlayLabel.Text = $"{label}\nSent!";
            _overlayLabel.Visible = true;
            await Task.Delay(1000);
            _overlayLabel.Visible = false;
        }
    }

    /// <summary>
    /// TODO #11 developer check (--selftest-audio [label ...]): speaks each tile (or the given labels)
    /// in order through the default audio device, waiting for each to finish, and prints what played.
    /// Uses the AudioFeedback settings except Enabled. Returns 0, or 1 if speech is unavailable.
    /// </summary>
    internal static int RunAudioSelfTest(IEnumerable<string> labels)
    {
        var set = TileCatalog.Load(TileCatalog.DefaultFolder, Settings);
        var wavs = set.Tiles.ToDictionary(t => t.Label, t => AudioFeedback.WavFor(TileCatalog.DefaultFolder, Path.GetFileName(t.Source)));
        set.DisposePictures();
        var list = labels.ToList();
        if (list.Count == 0) list = wavs.Keys.ToList();

        using var audio = new AudioFeedback(Settings.AudioFeedback);
        var a = Settings.AudioFeedback;
        Console.WriteLine($"Voice: {audio.VoiceName ?? "(unavailable)"}; Speak={a.Speak} Chime={a.Chime} Volume={a.Volume} Rate={a.Rate} Template=\"{a.SpeechTemplate}\"");
        int failures = 0;
        foreach (var label in list)
        {
            var wav = wavs.GetValueOrDefault(label);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                audio.PlaySync(label, wav);
                Console.WriteLine($"OK    {sw.ElapsedMilliseconds,5} ms  {label}  {(wav != null ? "wav " + Path.GetFileName(wav) : "spoken")}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL  {sw.ElapsedMilliseconds,5} ms  {label}  {ex.GetType().Name}: {ex.Message}");
            }
        }
        return audio.VoiceName == null || failures > 0 ? 1 : 0;
    }

    // TODO #6/#7: every tap goes to %LOCALAPPDATA%\PECS\Logs\communicator-yyyy-MM-dd.csv as up to two rows
    // sharing PressId, PressedAt, and Tile:
    //   1. at the tap:       Outcome = Pressed (will be sent) or Suppressed (debounced; Suppressed = True)
    //   2. when each send is done: Outcome = Sent, SentAfterRetry, or Failed (Pressed taps only)
    // Android TODO #5: one result row PER LISTENER, its URL in ListenerUrl (the press row lists all
    // URLs, space-separated). Same columns as before, so existing files need no -N rollover.
    // Result rows of overlapping sends may interleave; PressId pairs them up.
    private static readonly CsvLog _pressLog =
        new("communicator", "PressId", "PressedAt", "Tile", "Outcome", "Suppressed", "Detail", "ListenerUrl");

    private static void LogPress(string pressId, DateTime pressedAt, string tile, string outcome,
                                 bool suppressed, string detail, string url) =>
        _pressLog.Append(pressId, pressedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"), tile, outcome, suppressed, detail, url);

    /// <summary>
    /// Sends the press to every listener, updates the dot from the outcomes, and logs the result rows.
    /// </summary>
    private async Task NotifyListenerAsync(string pressId, DateTime pressedAt, string tile, IReadOnlyList<string> urls)
    {
        // Never throws: failures only change the dot; Ian's feedback is unaffected.
        var outcomes = await SendAndLogAsync(pressId, pressedAt, tile, urls);
        SetLinkState(outcomes, "Send");
    }

    /// <summary>
    /// Android TODO #5: sends to all listeners in parallel (each with its own timeout and retry inside
    /// ListenerClient) and logs each listener's result row as soon as THAT listener finishes,
    /// so a slow listener doesn't hold back the others' rows.
    /// </summary>
    internal static async Task<ListenerOutcome[]> SendAndLogAsync(string pressId, DateTime pressedAt, string tile, IReadOnlyList<string> urls)
    {
        var tasks = urls.Select(async url =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await ListenerClient.SendPressAsync(url, tile, pressedAt);
            var outcome = !result.Ok ? "Failed" : result.Retried ? "SentAfterRetry" : "Sent";
            LogPress(pressId, pressedAt, tile, outcome, false, result.Message, url);
            return new ListenerOutcome(url, result, sw.ElapsedMilliseconds);
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Developer check (--selftest-send [extra URL ...]): sends one "Walk" press to every configured
    /// listener, plus any extra URLs given for this run only (never saved), logs it like a real tap,
    /// prints one line per listener, and returns 0 = all OK, 1 = some, 2 = none.
    /// </summary>
    internal static int RunSendSelfTest(IEnumerable<string> extraUrls)
    {
        var urls = Settings.ListenerUrls.Concat(extraUrls).ToList();
        var pressedAt = DateTime.Now;
        var pressId = NewPressId();
        const string tile = "Walk";
        LogPress(pressId, pressedAt, tile, "Pressed", false, "selftest-send", string.Join(" ", urls));

        var outcomes = SendAndLogAsync(pressId, pressedAt, tile, urls).GetAwaiter().GetResult();
        Console.WriteLine($"PressId {pressId}, pressed {pressedAt:HH:mm:ss.fff}");
        foreach (var o in outcomes)
            Console.WriteLine($"{(o.Result.Ok ? "OK  " : "FAIL")}  {o.ElapsedMs,5} ms  {o.Url}  {o.Result.Message}");
        int ok = outcomes.Count(o => o.Result.Ok);
        Console.WriteLine($"{ok} of {outcomes.Length} OK");
        return ok == outcomes.Length ? 0 : ok > 0 ? 1 : 2;
    }
}
