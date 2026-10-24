namespace PECSCommunicator;

public partial class MainForm : Form
{
    public MainForm()
    {
        InitializeComponent();
        SetupPECSTiles();
    }

    private void SetupPECSTiles()
    {
        // Simple 2x2 grid of large buttons for the shell
        var tiles = new[]
        {
            new { Text = "Toilet", Color = Color.LightBlue },
            new { Text = "Snack", Color = Color.LightGreen },
            new { Text = "Water", Color = Color.LightCyan },
            new { Text = "Walk", Color = Color.LightYellow },
            // new { Text = "Help", Color = Color.LightCoral },
            // new { Text = "Done", Color = Color.LightGray }
        };

        tableLayoutPanel1.RowCount = 2;
        tableLayoutPanel1.ColumnCount = 2;
        tableLayoutPanel1.Dock = DockStyle.Fill;
        tableLayoutPanel1.Padding = new Padding(20);

        for (int i = 0; i < tiles.Length; i++)
        {
            var btn = new Button
            {
                Text = tiles[i].Text,
                Font = new Font("Segoe UI", 28, FontStyle.Bold),
                BackColor = tiles[i].Color,
                Dock = DockStyle.Fill,
                Margin = new Padding(15),
                FlatStyle = FlatStyle.Flat,
                Tag = tiles[i].Text
            };
            btn.Click += Tile_Click;
            tableLayoutPanel1.Controls.Add(btn, i % 3, i / 3);
        }
    }

    private void Tile_Click(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.Tag is string label)
        {
            // Stage 1: just confirm the press. Messaging comes later.
            MessageBox.Show($"You pressed: {label}\n\n(This will send a message in a later stage.)",
                            "PECS", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}