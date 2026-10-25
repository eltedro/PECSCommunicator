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
 * [ ] 1. move tile definitions (label, color, image file) into appsettings.json so tiles change without a rebuild
 * [ ] 2. add pictures to the Resources folder matching the tile names; show picture with a small label beneath (data-driven via #1)
 * [ ] 3. add a hidden Settings screen (e.g. 3-second corner press + keyboard shortcut) to change the listener URL without editing appsettings.json
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
    private Label? _overlayLabel;

    public MainForm()
    {
        InitializeComponent();
        SetupPECSTiles();
        SetupOverlay();
    }

    private void SetupPECSTiles()
    {
        var tiles = new[]
        {
            new { Text = "Toilet", Color = Color.LightBlue },
            new { Text = "Snack", Color = Color.LightGreen },
            new { Text = "Water", Color = Color.LightCyan },
            new { Text = "Walk", Color = Color.LightYellow }
        };

        const int NUMCOLUMNS = 2;
        tableLayoutPanel1.RowCount = (int)Math.Ceiling((double)tiles.Length / NUMCOLUMNS);
        tableLayoutPanel1.ColumnCount = NUMCOLUMNS;
        tableLayoutPanel1.Dock = DockStyle.Fill;
        tableLayoutPanel1.Padding = new Padding(20);

        for (int i = 0; i < tiles.Length; i++)
        {
            var btn = new Button
            {
                Text = tiles[i].Text,
                Font = new Font("Segoe UI", 72, FontStyle.Bold),
                BackColor = tiles[i].Color,
                Dock = DockStyle.Fill,
                Margin = new Padding(15),
                FlatStyle = FlatStyle.Flat,
                Tag = tiles[i].Text
            };
            btn.Click += Tile_Click;
            tableLayoutPanel1.Controls.Add(btn, i % NUMCOLUMNS, i / NUMCOLUMNS);  
        }
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

    // Short timeout so a missing listener never leaves requests hanging.
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    // Listener address comes from appsettings.json (next to the exe) so it can change without a rebuild.
    private static readonly string ListenerUrl = LoadListenerUrl();

    private static string LoadListenerUrl()
    {
        const string fallback = "http://localhost:5050/pecs";
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return fallback;
            var options = new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true };
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), options);
            return doc.RootElement.TryGetProperty("ListenerUrl", out var url) && !string.IsNullOrWhiteSpace(url.GetString())
                ? url.GetString()!
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private async void Tile_Click(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string label) return;

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
            using var response = await _httpClient.PostAsync(ListenerUrl, content);
        }
        catch
        {
            // Silent fail – we don’t want to interrupt Ian
        }
    }

}
