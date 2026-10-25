namespace PECSCommunicator;

/// <summary>
/// Hidden adult settings dialog (opened by a 3-second corner hold or Ctrl+Shift+S).
/// Large, touch-friendly controls. Currently edits only the listener URL.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly TextBox _urlBox;
    private readonly Label _errorLabel;

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
        // TODO #4: add a "Test Connection" button here (buttons.Controls.Add(test)),
        // which POSTs to _urlBox.Text and reports success/failure in _errorLabel.

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

    private void Save_Click(object? sender, EventArgs e)
    {
        var text = _urlBox.Text.Trim();
        if (!IsValidUrl(text))
        {
            _errorLabel.Text = "Enter a full http:// or https:// address.";
            _urlBox.Focus();
            return;
        }
        ListenerUrl = text;
        DialogResult = DialogResult.OK;
        Close();
    }
}
