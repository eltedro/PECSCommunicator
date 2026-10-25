namespace PECSCommunicator;

/// <summary>
/// Hidden adult settings dialog (opened by a 3-second corner hold or Ctrl+Shift+S).
/// Large, touch-friendly controls. Edits the listener list (Android TODO #5: one URL per line).
/// Sized to fit the 1024x600 touch panel; the status area has a fixed height so long
/// messages can never push the buttons off the dialog.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly TextBox _urlBox;
    private readonly Label _errorLabel;
    private readonly Button _testButton;
    private readonly CheckBox _autoStartBox;

    /// <summary>State of "Start with Windows"; applied by the caller after Save (TODO #8).</summary>
    public bool StartWithWindows => _autoStartBox.Checked;

    /// <summary>The validated listener URLs after the user presses Save.</summary>
    public IReadOnlyList<string> ListenerUrls { get; private set; }

    public SettingsForm(IReadOnlyList<string> currentUrls, string? lastStatus = null, string? placement = null)
    {
        ListenerUrls = currentUrls;

        Text = "PECS Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        Font = new Font("Segoe UI", 18);
        ClientSize = new Size(900, 540);   // + title bar stays under the panel's 600 px height
        Padding = new Padding(24, 16, 24, 8);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));   // URL list: ~3 lines, scrolls beyond
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));   // status: fixed height (see summary)
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var urlLabel = new Label { Text = "Listener URLs, one per line (e.g. http://192.168.4.28:5050/pecs):", AutoSize = true, Font = new Font("Segoe UI", 14) };

        _urlBox = new TextBox
        {
            Text = string.Join("\r\n", currentUrls),
            Multiline = true,
            AcceptsReturn = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 16),
            Margin = new Padding(0, 6, 0, 6)
        };
        _urlBox.TextChanged += (_, _) => _errorLabel!.Text = "";

        // Status area: starts with the main screen's last per-listener status (TODO #5), then shows
        // test/validation results. Fixed size + ellipsis: text that doesn't fit is cut, never the buttons.
        _errorLabel = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 11),
            ForeColor = Color.DimGray,
            Text = lastStatus is null ? "" : "Last status: " + lastStatus
        };

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

        // TODO #4: "Test Connection" checks every URL in the box without saving them.
        _testButton = MakeButton("Test Connection");
        _testButton.Width = 260;
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
            Height = 30,
            Font = new Font("Segoe UI", 12),
            TextAlign = ContentAlignment.MiddleRight
        };
        logsLink.LinkClicked += (_, _) => CsvLog.OpenFolder();
        Controls.Add(logsLink);

        // TODO #8: auto-start checkbox (current state read from the registry) and the display placement line.
        _autoStartBox = new CheckBox
        {
            Text = "Start with Windows",
            Checked = AutoStart.IsEnabled,
            Dock = DockStyle.Bottom,
            Height = 40,
            Font = new Font("Segoe UI", 14)
        };
        Controls.Add(_autoStartBox);

        var placementLabel = new Label
        {
            Text = string.IsNullOrEmpty(placement) ? "" : "Display: " + placement,
            Dock = DockStyle.Bottom,
            Height = 30,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 12),
            ForeColor = Color.DimGray
        };
        Controls.Add(placementLabel);

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

    /// <summary>
    /// Splits the box into URLs (blank lines ignored, duplicates removed).
    /// Returns an error message naming the first bad line, or null if all are valid.
    /// </summary>
    private string? ReadUrls(out List<string> urls)
    {
        urls = _urlBox.Lines.Select(l => l.Trim()).Where(l => l.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0) return "Enter at least one listener URL.";
        var bad = urls.FirstOrDefault(u => !IsValidUrl(u));
        return bad is null ? null : $"Not a full http:// or https:// address: {bad}";
    }

    /// <summary>Shows text in the status area: green for success, red for problems.</summary>
    private void ShowStatus(string text, bool ok)
    {
        _errorLabel.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        _errorLabel.Text = text;
    }

    /// <summary>
    /// Checks every URL in the box (without saving) in parallel and lists each one's result.
    /// Runs async so the dialog stays responsive.
    /// </summary>
    private async void TestButton_Click(object? sender, EventArgs e)
    {
        if (ReadUrls(out var urls) is { } error)
        {
            ShowStatus(error, ok: false);
            return;
        }

        _testButton.Enabled = false;
        ShowStatus($"Testing {urls.Count} listener(s)...", ok: true);
        var outcomes = await ListenerClient.CheckAllAsync(urls);
        if (IsDisposed) return;
        ShowStatus(MainForm.DescribeOutcomes(outcomes, $"Test at {DateTime.Now:HH:mm:ss}"), outcomes.All(o => o.Result.Ok));
        if (!outcomes.All(o => o.Result.Ok) && outcomes.Any(o => o.Result.Ok))
            _errorLabel.ForeColor = Color.DarkOrange;   // some OK: amber, like the main screen's dot
        _testButton.Enabled = true;
    }

    private void Save_Click(object? sender, EventArgs e)
    {
        if (ReadUrls(out var urls) is { } error)
        {
            ShowStatus(error, ok: false);
            _urlBox.Focus();
            return;
        }
        ListenerUrls = urls;
        DialogResult = DialogResult.OK;
        Close();
    }
}