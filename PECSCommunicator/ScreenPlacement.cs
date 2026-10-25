using System.Diagnostics;
using System.Text;

namespace PECSCommunicator;

/// <summary>
/// TODO #8: decides where the Communicator window goes.
///  - Exactly 3 displays (the jukebox PC): full screen on the 7" touch panel. That is the screen whose
///    bounds equal TouchScreenSize (default 1024x600), or, if none matches exactly, the smallest-area
///    non-primary screen.
///  - Any other count (e.g. a dev PC): a borderless 1024x600 window centered on the primary screen's
///    working area, not maximized.
/// The choice itself is a pure function (<see cref="PickTouchScreen"/>) so it can be tested without real monitors.
/// </summary>
public static class ScreenPlacement
{
    /// <summary>Size of the windowed (non-3-screen) mode.</summary>
    public static readonly Size WindowedSize = new(1024, 600);

    /// <summary>
    /// Returns the index of the touch screen in <paramref name="bounds"/>, or null if there aren't exactly 3 screens.
    /// </summary>
    /// <param name="bounds">Bounds of every screen.</param>
    /// <param name="primaryIndex">Index of the primary screen (never chosen by the fallback).</param>
    /// <param name="touchSize">Exact size of the touch panel.</param>
    public static int? PickTouchScreen(IReadOnlyList<Rectangle> bounds, int primaryIndex, Size touchSize)
    {
        if (bounds.Count != 3) return null;

        // 1. An exact size match wins (first one, if somehow several match).
        for (int i = 0; i < bounds.Count; i++)
            if (bounds[i].Size == touchSize) return i;

        // 2. Otherwise the smallest non-primary screen by area.
        int? best = null;
        for (int i = 0; i < bounds.Count; i++)
        {
            if (i == primaryIndex) continue;
            long area = (long)bounds[i].Width * bounds[i].Height;
            if (best is null || area < (long)bounds[best.Value].Width * bounds[best.Value].Height) best = i;
        }
        return best;
    }

    /// <summary>A WindowedSize rectangle centered in <paramref name="workingArea"/> (clipped if the area is smaller).</summary>
    public static Rectangle CenteredWindow(Rectangle workingArea)
    {
        int w = Math.Min(WindowedSize.Width, workingArea.Width);
        int h = Math.Min(WindowedSize.Height, workingArea.Height);
        return new Rectangle(workingArea.X + (workingArea.Width - w) / 2, workingArea.Y + (workingArea.Height - h) / 2, w, h);
    }

    /// <summary>
    /// Places <paramref name="form"/> on the current displays and returns a short description of the choice
    /// (also written to Debug output). Safe to call again after a display change.
    /// TopMost is deliberately NOT set: the jukebox app shares this PC, and two TopMost windows fight
    /// over z-order. The touch panel is normally used only by the Communicator anyway.
    /// </summary>
    public static string Apply(Form form, Size touchSize)
    {
        var screens = Screen.AllScreens;
        var index = PickTouchScreen(screens.Select(s => s.Bounds).ToList(),
                                    Array.FindIndex(screens, s => s.Primary), touchSize);

        form.FormBorderStyle = FormBorderStyle.None;
        form.StartPosition = FormStartPosition.Manual;
        form.WindowState = FormWindowState.Normal; // un-maximize first so the new bounds take effect

        string text;
        if (index is int i)
        {
            var b = screens[i].Bounds;
            form.Bounds = b;
            form.WindowState = FormWindowState.Maximized; // maximizes on the screen the bounds are on
            text = $"Full screen on display {i + 1} of 3 ({b.Width}x{b.Height} at {b.X},{b.Y})";
        }
        else
        {
            var primary = Screen.PrimaryScreen ?? screens[0];
            form.Bounds = CenteredWindow(primary.WorkingArea);
            text = $"{screens.Length} display(s): {form.Width}x{form.Height} window centered on primary";
        }

        Debug.WriteLine("PECS placement: " + text);
        return text;
    }

    /// <summary>
    /// Self-test of <see cref="PickTouchScreen"/>, run with "PECSCommunicator.exe --selftest-placement".
    /// Writes PASS/FAIL lines to %TEMP%\pecs_selftest.txt; returns true if all pass.
    /// </summary>
    public static bool RunSelfTest()
    {
        var touch = new Size(1024, 600);
        Rectangle R(int x, int w, int h) => new(x, 0, w, h);
        var cases = new (string Name, Rectangle[] Screens, int Primary, int? Expected)[]
        {
            ("1080p primary + 1080p + 1024x600 -> touch panel", new[] { R(0,1920,1080), R(1920,1920,1080), R(3840,1024,600) }, 0, 2),
            ("touch panel listed first -> touch panel",        new[] { R(-1024,1024,600), R(0,1920,1080), R(1920,1280,1024) }, 1, 0),
            ("no exact match -> smallest non-primary",         new[] { R(0,1920,1080), R(1920,1280,800), R(3200,800,480) }, 0, 2),
            ("primary is smallest -> not chosen by fallback",  new[] { R(0,800,480), R(800,1920,1080), R(2720,1280,1024) }, 0, 2),
            ("2 screens -> windowed (null)",                   new[] { R(0,1920,1080), R(1920,1024,600) }, 0, null),
            ("1 screen -> windowed (null)",                    new[] { R(0,1920,1080) }, 0, null),
            ("4 screens -> windowed (null)",                   new[] { R(0,1920,1080), R(1920,1024,600), R(2944,800,480), R(3744,1280,720) }, 0, null),
        };

        var sb = new StringBuilder();
        bool allPass = true;
        foreach (var c in cases)
        {
            var got = PickTouchScreen(c.Screens, c.Primary, touch);
            bool pass = got == c.Expected;
            allPass &= pass;
            sb.AppendLine($"{(pass ? "PASS" : "FAIL")}  {c.Name}  (expected {c.Expected?.ToString() ?? "null"}, got {got?.ToString() ?? "null"})");
        }
        var centered = CenteredWindow(new Rectangle(0, 0, 1920, 1040));
        bool cPass = centered == new Rectangle(448, 220, 1024, 600);
        allPass &= cPass;
        sb.AppendLine($"{(cPass ? "PASS" : "FAIL")}  centered in 1920x1040 working area -> {centered}");
        sb.AppendLine(allPass ? "ALL PASS" : "SOME FAILED");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pecs_selftest.txt"), sb.ToString());
        return allPass;
    }
}
