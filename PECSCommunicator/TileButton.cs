using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace PECSCommunicator;

/// <summary>
/// A PECS tile: an owner-drawn Button that shows a picture scaled to fit with the label beneath.
/// Being a single Button, the whole tile (picture and label) is one click/touch target.
/// With no picture it falls back to the standard big-text Button look.
/// </summary>
public sealed class TileButton : Button
{
    private Image? _picture;

    /// <summary>Font used for the label under the picture.</summary>
    // Set in code only; keep the WinForms designer from serializing it.
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Font LabelFont { get; set; } = new Font("Segoe UI", 32, FontStyle.Bold);

    public TileButton()
    {
        // Repaint fully on resize so the picture rescales cleanly.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>The tile's picture, or null for text only. The tile owns and disposes it.</summary>
    // Set in code only; keep the WinForms designer from serializing it.
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? Picture
    {
        get => _picture;
        set
        {
            if (ReferenceEquals(_picture, value)) return;
            _picture?.Dispose();
            _picture = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Loads an image file into memory (so the file is not locked). Returns null if the
    /// name is empty or the file is missing or unreadable.
    /// </summary>
    public static Image? TryLoadPicture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var img = Image.FromStream(fs);
            return new Bitmap(img); // copy detaches it from the stream
        }
        catch
        {
            return null;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_picture == null)
        {
            base.OnPaint(e); // text-only fallback
            return;
        }

        var g = e.Graphics;
        g.Clear(BackColor);

        // Thin border like the flat Button.
        using (var pen = new Pen(Color.FromArgb(120, 0, 0, 0)))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        // Layout: label strip at the bottom, picture fills the rest.
        const int pad = 10;
        int labelHeight = (int)Math.Ceiling(LabelFont.GetHeight(g)) + pad;
        var picArea = new Rectangle(pad, pad, Width - 2 * pad, Height - labelHeight - 2 * pad);
        var labelArea = new Rectangle(pad, Height - labelHeight - pad, Width - 2 * pad, labelHeight);

        if (picArea.Width > 0 && picArea.Height > 0)
        {
            // Scale to fit, preserving aspect ratio, centered.
            float scale = Math.Min((float)picArea.Width / _picture.Width, (float)picArea.Height / _picture.Height);
            int w = (int)(_picture.Width * scale), h = (int)(_picture.Height * scale);
            var dest = new Rectangle(picArea.X + (picArea.Width - w) / 2, picArea.Y + (picArea.Height - h) / 2, w, h);

            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(_picture, dest);
        }

        TextRenderer.DrawText(g, Text, LabelFont, labelArea, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _picture?.Dispose();
            _picture = null;
            LabelFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
