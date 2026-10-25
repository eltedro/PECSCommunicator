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
 * [ ] 4. add a "Test Connection" button inside Settings to verify the listener is reachable before saving
 * [ ] 5. show a subtle adult-visible status indicator when the listener is unreachable; retry a failed send once
 * [ ] 6. log every press (incl. failed sends) to a dated CSV on both Communicator and Listener
 * [ ] 7. debounce rapid repeat taps of the same tile (~2 s) so the listener isn't flooded; still log them
 * [ ] 8. if exactly three displays are detected, open full-screen on the 7" display; otherwise open in a borderless 1024x600 window. Auto-start with Windows.
 * [ ] 9. make the Listener harder to miss: per-tile sound, flash taskbar button, and/or bring window to front
 * 
 */

using System.Net.Http;
using System.Text;

namespace PECSCommunicator;

public partial class MainForm : Form
{
    // Settings (listener URL, columns, tiles) come from appsettings.json next to the exe,
    // so tiles and the listener address can change without a rebuild.
    private static readonly AppSettings Settings = AppSettings.Load();

    // Short timeout so a missing listener never leaves requests hanging.
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    private Label? _overlayLabel;

    // Hidden adult access: hold the top-left 80x80 px corner for 3 seconds.
    private readonly CornerHoldGesture _cornerHold;

    public MainForm()
    {
        InitializeComponent();
        SetupPECSTiles();
        SetupOverlay();

        _cornerHold = new CornerHoldGesture(this, new Size(80, 80), TimeSpan.FromSeconds(3));
        _cornerHold.HoldCompleted += (_, _) => BeginInvoke(OpenSettings);
        FormClosed += (_, _) => _cornerHold.Dispose(); // remove the app-wide message filter
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
        using var dlg = new SettingsForm(Settings.ListenerUrl);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            AppSettings.SaveListenerUrl(AppSettings.DefaultPath, dlg.ListenerUrl);
            Settings.ListenerUrl = dlg.ListenerUrl; // takes effect on the next tap
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

    private async void Tile_Click(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string label) return;

        // Releasing a completed corner hold must not count as a tap.
        if (_cornerHold.ShouldSuppressClick) return;

        // Fire-and-forget notification to the listener
        _ = NotifyListenerAsync(label);

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

    private async Task NotifyListenerAsync(string tile)
    {
        try
        {
            // Serialize properly so tile names with quotes or special characters stay valid JSON.
            var json = System.Text.Json.JsonSerializer.Serialize(new { tile, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(Settings.ListenerUrl, content);
        }
        catch
        {
            // Silent fail - we don't want to interrupt Ian
        }
    }
}
