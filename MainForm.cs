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

    private async void Tile_Click(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string label) return;

        System.Media.SystemSounds.Asterisk.Play();

        // Brief highlight on the button
        var originalColor = btn.BackColor;
        btn.BackColor = Color.White;
        await Task.Delay(150);
        btn.BackColor = originalColor;

        // Show overlay
        if (_overlayLabel != null)
        {
            _overlayLabel.Text = $"{label}\nSent!";
            _overlayLabel.Visible = true;
            await Task.Delay(1200);
            _overlayLabel.Visible = false;
        }

        // TODO Stage 3: actual messaging here
        // Debug.WriteLine($"Would send: {label}");
    }
}