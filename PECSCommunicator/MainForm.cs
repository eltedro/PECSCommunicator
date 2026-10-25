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
 * 
 */

namespace PECSCommunicator;

public partial class MainForm : Form
{
    // Settings (listener URL, columns, tiles) come from appsettings.json next to the exe,
    // so tiles and the listener address can change without a rebuild.
    private static readonly AppSettings Settings = AppSettings.Load();

    private Label? _overlayLabel;

    // TODO #5: adult-visible connection dot, drawn in the grid's bottom-right padding.
    private enum LinkState { Unknown, Ok, Failed }
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
        SetupPECSTiles();
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
            var result = await ListenerClient.CheckAsync(Settings.ListenerUrl);
            SetLinkState(result, "Check");
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Records the latest outcome (check or send) and repaints the dot.</summary>
    private void SetLinkState(ListenerResult result, string source)
    {
        if (IsDisposed) return;
        _linkState = result.Ok ? LinkState.Ok : LinkState.Failed;
        _linkStatusText = $"{source} at {DateTime.Now:HH:mm:ss}: {result.Message}";
        tableLayoutPanel1.Invalidate(DotBounds());
    }

    /// <summary>Dot position: bottom-right corner of the grid, inside its padding (never over a tile).</summary>
    private Rectangle DotBounds()
    {
        var c = tableLayoutPanel1.ClientSize;
        return new Rectangle(c.Width - DotSize - DotInset, c.Height - DotSize - DotInset, DotSize, DotSize);
    }

    /// <summary>Paints the status dot: gray = unknown, green = last contact OK, red = last contact failed.</summary>
    private void DrawStatusDot(object? sender, PaintEventArgs e)
    {
        var color = _linkState switch
        {
            LinkState.Ok => Color.FromArgb(60, 170, 60),
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

    /// <summary>Shows the Settings dialog; on Save, writes appsettings.json and applies the URL now.</summary>
    private void OpenSettings()
    {
        using var dlg = new SettingsForm(Settings.ListenerUrl, _linkStatusText, _placementText);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            AppSettings.SaveListenerUrl(AppSettings.DefaultPath, dlg.ListenerUrl);
            Settings.ListenerUrl = dlg.ListenerUrl; // takes effect on the next tap

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

    /// <summary>Builds the tile grid from the configured tiles and column count.</summary>
    private void SetupPECSTiles()
    {
        var tiles = Settings.Tiles;
        int columns = Math.Min(Settings.Columns, tiles.Count);
        int rows = (int)Math.Ceiling((double)tiles.Count / columns);

        tableLayoutPanel1.SuspendLayout();
        tableLayoutPanel1.Controls.Clear();
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

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            // Owner-drawn tile: picture with label beneath, or big text if no usable image.
            var btn = new TileButton
            {
                Text = tile.Label,
                Font = new Font("Segoe UI", 72, FontStyle.Bold),
                BackColor = tile.ResolveColor(),
                Dock = DockStyle.Fill,
                Margin = new Padding(15),
                FlatStyle = FlatStyle.Flat,
                Tag = tile.Label,
                Picture = LoadTileImage(tile.Image)
            };
            btn.Click += Tile_Click;
            tableLayoutPanel1.Controls.Add(btn, i % columns, i / columns);
        }

        tableLayoutPanel1.ResumeLayout();
    }

    /// <summary>
    /// Loads a tile picture from the Resources folder next to the exe.
    /// Returns null (text-only tile) if the name is empty or the file is missing or unreadable.
    /// </summary>
    private static Image? LoadTileImage(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        return TileButton.TryLoadPicture(Path.Combine(AppContext.BaseDirectory, "Resources", fileName.Trim()));
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
        var url = Settings.ListenerUrl; // capture: Settings may change while the send is in flight

        bool suppressed = Settings.DebounceSeconds > 0
            && _lastSentAt.TryGetValue(label, out var last)
            && (pressedAt - last).TotalSeconds < Settings.DebounceSeconds;

        // Log first, synchronously (a one-line append), so the tap is on disk even if the app
        // is closed or crashes before the send finishes.
        LogPress(pressId, pressedAt, label, suppressed ? "Suppressed" : "Pressed", suppressed,
            suppressed ? $"Repeat within {Settings.DebounceSeconds:0.#} s; not sent" : "", url);

        if (!suppressed)
        {
            _lastSentAt[label] = pressedAt;
            _ = NotifyListenerAsync(pressId, pressedAt, label, url); // fire-and-forget; logs its own result
        }

        // Child feedback is the same for sent and suppressed taps, so Ian always gets a consistent
        // response and isn't taught that tapping again "does nothing" (adults see it in the log).
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

        // System.Media.SystemSounds.Asterisk.Play();
    }

    // TODO #6/#7: every tap goes to %LOCALAPPDATA%\PECS\Logs\communicator-yyyy-MM-dd.csv as up to two rows
    // sharing PressId, PressedAt, and Tile:
    //   1. at the tap:       Outcome = Pressed (will be sent) or Suppressed (debounced; Suppressed = True)
    //   2. when send is done: Outcome = Sent, SentAfterRetry, or Failed (Pressed taps only)
    // Result rows of overlapping sends may interleave; PressId pairs them up.
    private static readonly CsvLog _pressLog =
        new("communicator", "PressId", "PressedAt", "Tile", "Outcome", "Suppressed", "Detail", "ListenerUrl");

    private static void LogPress(string pressId, DateTime pressedAt, string tile, string outcome,
                                 bool suppressed, string detail, string url) =>
        _pressLog.Append(pressId, pressedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"), tile, outcome, suppressed, detail, url);

    /// <summary>
    /// Sends the press (with one retry inside ListenerClient), updates the dot from the final outcome,
    /// and logs the result row.
    /// </summary>
    private async Task NotifyListenerAsync(string pressId, DateTime pressedAt, string tile, string url)
    {
        // Never throws: failures only turn the dot red; Ian's feedback is unaffected.
        var result = await ListenerClient.SendPressAsync(url, tile, pressedAt);
        SetLinkState(result, "Send");

        var outcome = !result.Ok ? "Failed" : result.Retried ? "SentAfterRetry" : "Sent";
        LogPress(pressId, pressedAt, tile, outcome, false, result.Message, url);
    }
}
