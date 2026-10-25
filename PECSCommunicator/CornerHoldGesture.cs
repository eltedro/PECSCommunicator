namespace PECSCommunicator;

/// <summary>
/// Detects a press-and-hold in a hidden hot zone (top-left corner of a form) for adults.
/// Implemented as an application-wide message filter so it works even when the press
/// lands on a tile button; it never consumes messages, so ordinary taps are unaffected.
/// </summary>
public sealed class CornerHoldGesture : IMessageFilter, IDisposable
{
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    private readonly Form _form;
    private readonly Size _zoneSize;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _tracking;
    private bool _suppressClick;

    /// <summary>Raised on the UI thread once the hold has lasted long enough.</summary>
    public event EventHandler? HoldCompleted;

    public CornerHoldGesture(Form form, Size zoneSize, TimeSpan holdTime)
    {
        _form = form;
        _zoneSize = zoneSize;
        _timer = new System.Windows.Forms.Timer { Interval = (int)holdTime.TotalMilliseconds };
        _timer.Tick += OnTimerTick;
        Application.AddMessageFilter(this);
    }

    /// <summary>
    /// True if a click should be ignored because it ends a completed hold.
    /// Tile click handlers call this first. Cleared by the next fresh press.
    /// </summary>
    public bool ShouldSuppressClick => _suppressClick;

    public bool PreFilterMessage(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_LBUTTONDOWN:
                _suppressClick = false; // a new press starts a fresh interaction
                if (IsOurForm() && InZone())
                {
                    _tracking = true;
                    _timer.Start();
                }
                break;

            case WM_MOUSEMOVE:
                if (_tracking && !InZone()) Cancel(); // dragged out of the corner
                break;

            case WM_LBUTTONUP:
                Cancel(); // released before the hold time
                break;
        }
        return false; // never swallow input
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        Cancel();
        _suppressClick = true; // the release that follows must not count as a tile tap
        HoldCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel()
    {
        _tracking = false;
        _timer.Stop();
    }

    /// <summary>Only react while the main form (not a dialog) is the active window.</summary>
    private bool IsOurForm() => Form.ActiveForm == _form;

    /// <summary>Is the pointer inside the hot zone, in the form's client coordinates?</summary>
    private bool InZone()
    {
        var p = _form.PointToClient(Cursor.Position);
        return p.X >= 0 && p.Y >= 0 && p.X < _zoneSize.Width && p.Y < _zoneSize.Height;
    }

    public void Dispose()
    {
        Application.RemoveMessageFilter(this);
        _timer.Dispose();
    }
}
