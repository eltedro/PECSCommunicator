using System.Net.Http;

namespace PECSCommunicator;

/// <summary>
/// Hidden adult settings dialog (opened by a 3-second corner hold or Ctrl+Shift+S).
/// Large, touch-friendly controls. Currently edits only the listener URL.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly TextBox _urlBox;
    private readonly Label _errorLabel;
    private readonly Button _testButton;

    /// <summary>The validated URL after the user presses Save.</summary>
    public string ListenerUrl { get; private set; }

    public SettingsForm(string currentUrl)
    {
        ListenerUrl = currentUrl;

        Text = "PECS Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        Font = new Font("Segoe UI", 18);
        ClientSize = new Size(820, 300);
        Padding = new Padding(24);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var urlLabel = new Label { Text = "Listener URL (e.g. http://192.168.4.28:5050/pecs):", AutoSize = true };

        _urlBox = new TextBox
        {
            Text = currentUrl,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 22),
            Margin = new Padding(0, 8, 0, 8)
        };
        _urlBox.TextChanged += (_, _) => _errorLabel!.Text = "";

        _errorLabel = new Label { AutoSize = true, ForeColor = Color.Firebrick, Text = "" };

        // Button row, right-aligned. Large targets for touch.
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancel = MakeButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var save = MakeButton("Save");
        save.Click += Save_Click;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        // TODO #4: "Test Connection" checks the URL in the box without saving it.
        _testButton = MakeButton("Test");
        _testButton.Width = 260;
        _testButton.Text = "Test Connection";
        _testButton.Click += TestButton_Click;
        buttons.Controls.Add(_testButton);

        layout.Controls.Add(urlLabel, 0, 0);
        layout.Controls.Add(_urlBox, 0, 1);
        layout.Controls.Add(_errorLabel, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        AcceptButton = save;
        CancelButton = cancel;
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        Size = new Size(180, 70),
        Margin = new Padding(12, 0, 0, 0)
    };

    /// <summary>Accepts the URL only if it is an absolute http/https address.</summary>
    public static bool IsValidUrl(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);


    // Separate client with a short timeout so a test never hangs the dialog for long.
    private static readonly HttpClient _testClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Shows a status line under the URL box: green for success, red for problems.</summary>
    private void ShowStatus(string text, bool ok)
    {
        _errorLabel.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        _errorLabel.Text = text;
    }

    /// <summary>
    /// Sends a GET to the URL in the box and checks the reply really comes from PECSListener
    /// ({"status":"ok","app":"PECSListener",...}). Runs async so the dialog stays responsive.
    /// </summary>
    private async void TestButton_Click(object? sender, EventArgs e)
    {
        var url = _urlBox.Text.Trim();
        if (!IsValidUrl(url))
        {
            ShowStatus("Enter a full http:// or https:// address.", ok: false);
            return;
        }

        _testButton.Enabled = false;
        ShowStatus("Testing...", ok: true);
        try
        {
            var machine = await TestConnectionAsync(url);
            if (machine != null)
                ShowStatus($"Connected to PECSListener on {machine}", ok: true);
            else
                ShowStatus("Wrong response: that isn't PECSListener.", ok: false);
        }
        catch (TaskCanceledException)
        {
            ShowStatus("Timed out: no answer within 3 seconds.", ok: false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException se)
        {
            ShowStatus(se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused
                ? "Refused: Listener not running on that port."
                : $"Could not connect ({se.SocketErrorCode}).", ok: false);
        }
        catch (Exception ex)
        {
            ShowStatus("Test failed: " + ex.Message, ok: false);
        }
        finally
        {
            if (!IsDisposed) _testButton.Enabled = true;
        }
    }

    /// <summary>Returns the Listener's machine name if the URL answers like PECSListener, else null.</summary>
    private static async Task<string?> TestConnectionAsync(string url)
    {
        using var response = await _testClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            bool isListener = root.TryGetProperty("status", out var s) && s.GetString() == "ok"
                           && root.TryGetProperty("app", out var a) && a.GetString() == "PECSListener";
            if (!isListener) return null;
            return root.TryGetProperty("machine", out var m) ? m.GetString() ?? "?" : "?";
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // not JSON, so not our Listener
        }
    }

    private void Save_Click(object? sender, EventArgs e)
    {
        var text = _urlBox.Text.Trim();
        if (!IsValidUrl(text))
        {
            ShowStatus("Enter a full http:// or https:// address.", ok: false);
            _urlBox.Focus();
            return;
        }
        ListenerUrl = text;
        DialogResult = DialogResult.OK;
        Close();
    }
}
