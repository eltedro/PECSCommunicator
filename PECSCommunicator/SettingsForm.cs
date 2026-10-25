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

    public SettingsForm(string currentUrl, string? lastStatus = null)
    {
        ListenerUrl = currentUrl;

        Text = "PECS Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        Font = new Font("Segoe UI", 18);
        ClientSize = new Size(820, 340);
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

        // Status line: starts with the main screen's last connection status (TODO #5), then shows test/validation results.
        _errorLabel = new Label { AutoSize = true, Font = new Font("Segoe UI", 14), ForeColor = Color.DimGray, Text = lastStatus is null ? "" : "Last status: " + lastStatus };

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

        // TODO #6: small link to the CSV press logs, below the buttons.
        var logsLink = new LinkLabel
        {
            Text = "Open log folder",
            Dock = DockStyle.Bottom,
            Height = 34,
            Font = new Font("Segoe UI", 12),
            TextAlign = ContentAlignment.MiddleRight
        };
        logsLink.LinkClicked += (_, _) => CsvLog.OpenFolder();
        Controls.Add(logsLink);

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


    /// <summary>Shows a status line under the URL box: green for success, red for problems.</summary>
    private void ShowStatus(string text, bool ok)
    {
        _errorLabel.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        _errorLabel.Text = text;
    }

    /// <summary>
    /// Checks the URL in the box (without saving it) using the shared health check.
    /// Runs async so the dialog stays responsive.
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
        var result = await ListenerClient.CheckAsync(url);
        if (IsDisposed) return;
        ShowStatus(result.Message, result.Ok);
        _testButton.Enabled = true;
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
