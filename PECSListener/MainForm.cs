// In elevated cmd prompt:
// netsh http add urlacl url=http://+:5050/pecs/ user=Everyone

using System.Net;
using System.Text;
using System.Text.Json;

namespace PECSListener;

public partial class MainForm : Form
{
    private HttpListener? _listener;
    private Label _statusLabel = null!;
    private Label _eventLabel = null!;
    private ListBox _logList = null!;

    public MainForm()
    {
        InitializeComponent();
        SetupUI();
        StartListener();
    }

    private void SetupUI()
    {
        Text = "PECS Listener";
        WindowState = FormWindowState.Normal;
        Size = new Size(700, 500);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;

        _statusLabel = new Label
        {
            Text = "Listening...",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.LightGreen,
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        };

        _eventLabel = new Label
        {
            Text = "Waiting for Ian...",
            Font = new Font("Segoe UI", 36, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(0, 100, 0),
            Dock = DockStyle.Top,
            Height = 120,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _logList = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 12),
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None
        };

        // TODO #6: small, unobtrusive link to the CSV logs.
        var logsLink = new LinkLabel
        {
            Text = "Open log folder",
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 10, 0),
            LinkColor = Color.Gray,
            ActiveLinkColor = Color.White
        };
        logsLink.LinkClicked += (_, _) => CsvLog.OpenFolder();

        Controls.Add(_logList);
        Controls.Add(logsLink);
        Controls.Add(_eventLabel);
        Controls.Add(_statusLabel);
    }

    /// <summary>The one URL prefix this Listener owns (must match the netsh urlacl reservation).</summary>
    private const string ListenerPrefix = "http://+:5050/pecs/";
    private const int ListenerPort = 5050;

    private void StartListener()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(ListenerPrefix);
        try
        {
            _listener.Start();
            _statusLabel.Text = $"Listening on port {ListenerPort}";
            Task.Run(ListenLoop);
        }
        catch (HttpListenerException ex)
        {
            // Translate the Windows error code into a plain cause the user can act on.
            var cause = ex.ErrorCode switch
            {
                183 or 32 => $"Port {ListenerPort} is already in use, probably another PECS Listener is already running.",
                5 => $"Windows hasn't reserved this URL; run: netsh http add urlacl url={ListenerPrefix} user=Everyone (as admin)",
                _ => ex.Message
            };
            ShowStartError($"Failed to start on {ListenerPrefix} (error {ex.ErrorCode}): {cause}");
        }
        catch (Exception ex)
        {
            ShowStartError($"Failed to start on {ListenerPrefix}: {ex.Message}");
        }
    }

    /// <summary>Shows a start-up error in the status line, enlarged so the whole message fits.</summary>
    private void ShowStartError(string message)
    {
        _statusLabel.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _statusLabel.Height = 70;
        _statusLabel.ForeColor = Color.OrangeRed;
        _statusLabel.Text = message;
    }

    private async Task ListenLoop()
    {
        while (_listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                var request = context.Request;

                // GET = connection test from the Communicator's Settings screen.
                // Answer with a small JSON identity; no banner, no log entry, no sound.
                if (request.HttpMethod == "GET")
                {
                    var reply = JsonSerializer.Serialize(new { status = "ok", app = "PECSListener", machine = Environment.MachineName });
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();

                    var from = request.RemoteEndPoint?.Address.ToString() ?? "?";
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(() => _statusLabel.Text = $"Listening on port {ListenerPort} - last connection test from {from} at {DateTime.Now:HH:mm:ss}");
                    continue;
                }

                // Only POST carries a tile press; anything else is rejected.
                if (request.HttpMethod != "POST")
                {
                    context.Response.StatusCode = 405;
                    context.Response.AddHeader("Allow", "GET, POST");
                    context.Response.Close();
                    continue;
                }

                string body;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    body = await reader.ReadToEndAsync();

                // TODO #6: log the press (off the UI thread) before showing it.
                LogPress(body, request.RemoteEndPoint?.Address.ToString() ?? "");

                // Respond immediately
                context.Response.StatusCode = 200;
                context.Response.Close();

                // Parse and display on UI thread (skip if the window is closing)
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(() => HandleIncoming(body));
            }
            catch { /* ignore transient errors */ }
        }
    }

    // TODO #6: every received press (POST only; health-check GETs are not logged)
    // goes to %LOCALAPPDATA%\PECS\Logs\listener-yyyy-MM-dd.csv.
    private static readonly CsvLog _pressLog = new("listener", "ReceivedAt", "SentAt", "Tile", "FromIp");

    /// <summary>Logs one received press; a body that isn't valid press JSON is logged as "(unparseable)".</summary>
    private static void LogPress(string body, string fromIp)
    {
        string tile = "(unparseable)", sentAt = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            tile = root.GetProperty("tile").GetString() ?? "(unparseable)";
            sentAt = root.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "";
        }
        catch { /* keep the "(unparseable)" defaults */ }

        _pressLog.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), sentAt, tile, fromIp);
    }

    private void HandleIncoming(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tile = root.GetProperty("tile").GetString() ?? "?";
            var time = root.GetProperty("time").GetString() ?? DateTime.Now.ToString("HH:mm:ss");

            // Big passive banner
            _eventLabel.Text = $"Ian pressed: {tile}";
            _eventLabel.BackColor = tile switch
            {
                "Toilet" => Color.FromArgb(0, 80, 160),
                "Snack" => Color.FromArgb(0, 120, 60),
                "Water" => Color.FromArgb(0, 140, 140),
                "Walk" => Color.FromArgb(160, 120, 0),
                _ => Color.FromArgb(0, 100, 0)
            };

            // Log entry
            _logList.Items.Insert(0, $"{time}  →  {tile}");
            if (_logList.Items.Count > 100) _logList.Items.RemoveAt(100);

            // Optional soft sound (non-blocking)
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch
        {
            _logList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  →  (unparseable)");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _listener?.Stop();
        _listener?.Close();
        base.OnFormClosing(e);
    }
}