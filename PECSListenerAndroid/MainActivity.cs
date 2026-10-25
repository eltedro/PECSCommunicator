// PECS Listener for Android - receives tile presses from PECSCommunicator over Wi-Fi.
//
// Android TODO list:
// [x] 1. Android listener project: embedded HTTP server on port 5050 at /pecs/,
//        same GET health check / POST press / 405 behavior as the Windows Listener.
// [x] 2. Always-on foreground service with boot start and battery-optimization prompt.
// [x] 3. Alerts: per-tile color banner + high-priority notification, per-tile sound,
//        TTS "Ian wants {tile}", vibrate.
// [x] 4. Log screen + CSV in the same format as the laptop.
// [ ] 5. Communicator multi-listener: ListenerUrls list with per-listener status / Test Connection.
// [ ] 6. Fixed IPs: router DHCP reservations + phone "Phone MAC" setting.

using System.Globalization;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace PECSListenerAndroid;

/// <summary>
/// Shows the listener's status and the latest press. The server itself lives in ListenerService
/// (TODO #2), so presses are received whether or not this screen is open.
/// </summary>
[Activity(Label = "PECS Listener", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
          Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
          ScreenOrientation = ScreenOrientation.Portrait)]
public class MainActivity : Activity
{
    private const int NotificationPermissionRequest = 1;

    private TextView _statusText = null!;
    private TextView _batteryText = null!;
    private TextView _bannerText = null!;
    private TextView _timeText = null!;
    private LinearLayout _logRows = null!;   // TODO #4: recent presses, newest first
    private const int MaxLogRows = 200;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);   // keep visible while open
        SetContentView(BuildLayout());

        ListenerService.Start(this);
        RequestNotificationPermission();   // then the battery prompt, see OnRequestPermissionsResult
    }

    protected override void OnResume()
    {
        base.OnResume();
        ListenerService.PressReceived += OnPress;
        ListenerService.HealthChecked += OnHealthCheck;
        ListenerService.StateChanged += OnStateChanged;
        ShowServiceState();
        ShowBatteryState();   // refresh after returning from the battery settings dialog

        RefreshFromLog();
    }

    /// <summary>Rebuilds the list from today's CSV (covers presses while closed) and shows the
    /// last VALID press in the banner: service memory first, else today's CSV.
    /// Called on resume and again when the service comes up (on a cold start the log
    /// doesn't exist yet when OnResume runs).</summary>
    private void RefreshFromLog()
    {
        LoadTodaysLog();   // TODO #4
        if (ListenerService.LastPress is { } last)
            ShowPress(last.Tile, last.Time, last.Received);
        else if (_lastValidFromCsv is { } fromCsv)
            ShowPress(fromCsv.Tile, fromCsv.Time, fromCsv.Received);
    }

    protected override void OnPause()
    {
        ListenerService.PressReceived -= OnPress;
        ListenerService.HealthChecked -= OnHealthCheck;
        ListenerService.StateChanged -= OnStateChanged;
        base.OnPause();
    }

    // ---------- Layout ----------

    /// <summary>Builds the screen in code: status lines on top, big banner below (mirrors the laptop).</summary>
    private View BuildLayout()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.Rgb(30, 30, 30));
        // Base 32px margin plus whatever the system bars need (see SystemBarInsetsPadding).
        root.SetOnApplyWindowInsetsListener(new SystemBarInsetsPadding(32));

        var fullWidth = () => new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        _statusText = new TextView(this) { Text = "Starting..." };
        _statusText.SetTextColor(Color.LightGreen);
        _statusText.TextSize = 16;
        root.AddView(_statusText, fullWidth());

        _batteryText = new TextView(this);
        _batteryText.TextSize = 14;
        root.AddView(_batteryText, fullWidth());

        _bannerText = new TextView(this) { Text = "Waiting for Ian...", Gravity = GravityFlags.Center };
        _bannerText.SetTextColor(Color.White);
        _bannerText.SetBackgroundColor(Color.Rgb(0, 100, 0));
        _bannerText.TextSize = 34;
        _bannerText.SetTypeface(null, TypefaceStyle.Bold);
        _bannerText.SetPadding(16, 80, 16, 80);
        var bannerParams = fullWidth();
        bannerParams.TopMargin = 32;
        root.AddView(_bannerText, bannerParams);

        _timeText = new TextView(this) { Gravity = GravityFlags.Center };
        _timeText.SetTextColor(Color.LightGray);
        _timeText.TextSize = 18;
        root.AddView(_timeText, fullWidth());

        // TODO #4: log list under the banner, filling the rest of the screen and scrolling.
        var logTitle = new TextView(this) { Text = "Today's presses (newest first)" };
        logTitle.SetTextColor(Color.Gray);
        logTitle.TextSize = 14;
        var titleParams = fullWidth();
        titleParams.TopMargin = 24;
        root.AddView(logTitle, titleParams);

        _logRows = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var scroll = new ScrollView(this);
        scroll.SetBackgroundColor(Color.Rgb(45, 45, 45));
        scroll.AddView(_logRows);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        return root;
    }

    // ---------- Service events (arrive on a server thread) ----------

    private void OnPress(string tile, string time, string fromIp)
    {
        var received = DateTime.Now;
        RunOnUiThread(() =>
        {
            if (ListenerService.IsValidTile(tile)) ShowPress(tile, time, received);   // malformed POSTs leave the banner alone
            AddLogRow(received.ToString("HH:mm:ss"), time, tile, fromIp, atTop: true);
        });
    }

    private void OnHealthCheck(string from) =>
        RunOnUiThread(() => _statusText.Text = $"Listening on {NetworkInfo.ListenUrl(this)} - last connection test from {from} at {DateTime.Now:HH:mm:ss}");

    private void OnStateChanged() => RunOnUiThread(() =>
    {
        ShowServiceState();
        if (ListenerService.IsRunning) RefreshFromLog();
    });

    private void ShowPress(string tile, string time, DateTime received)
    {
        _bannerText.Text = $"Ian pressed: {tile}";
        _bannerText.SetBackgroundColor(ListenerService.Settings.BannerColorFor(tile));   // TODO #3: tile color
        _timeText.Text = string.IsNullOrEmpty(time) ? $"received {received:HH:mm:ss}" : $"sent {ClockTime(time)}, received {received:HH:mm:ss}";
    }

    /// <summary>"yyyy-MM-dd HH:mm:ss[.fff]" -> "HH:mm:ss"; anything else is returned unchanged.</summary>
    private static string ClockTime(string stamp) =>
        stamp.Length >= 19 && stamp[10] == ' ' ? stamp.Substring(11, 8) : stamp;

// ---------- Log list (TODO #4) ----------

    /// <summary>Shows the last MaxLogRows rows of today's CSV, newest first.</summary>
    private void LoadTodaysLog()
    {
        _logRows.RemoveAllViews();
        _lastValidFromCsv = null;
        var rows = ListenerService.PressLog?.ReadToday() ?? [];
        foreach (var r in rows.TakeLast(MaxLogRows))
        {
            if (r.Length < 4) continue;
            AddLogRow(ClockTime(r[0]), r[1], r[2], r[3], atTop: true);

            // Remember the newest valid press so the banner can show it after a service restart.
            if (ListenerService.IsValidTile(r[2]) &&
                DateTime.TryParseExact(r[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                _lastValidFromCsv = (r[2], r[1], at);
        }
    }

    /// <summary>Newest valid press found in today's CSV (used when the service has none in memory).</summary>
    private (string Tile, string Time, DateTime Received)? _lastValidFromCsv;

    /// <summary>One row: color swatch, tile, then a compact "sent · received · IP" line.
    /// Malformed POSTs are shown greyed out.</summary>
    private void AddLogRow(string received, string sent, string tile, string fromIp, bool atTop)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetPadding(16, 12, 16, 12);
        row.SetGravity(GravityFlags.CenterVertical);

        var swatch = new View(this);
        bool valid = ListenerService.IsValidTile(tile);
        swatch.SetBackgroundColor(valid ? ListenerService.Settings.BannerColorFor(tile) : Color.Rgb(90, 90, 90));
        row.AddView(swatch, new LinearLayout.LayoutParams(36, 36) { RightMargin = 20 });

        var text = new TextView(this)
        {
            Text = $"{tile}\nsent {(string.IsNullOrEmpty(sent) ? "-" : ClockTime(sent))} · rcvd {received} · {fromIp}"
        };
        text.SetTextColor(valid ? Color.White : Color.Gray);
        text.TextSize = 15;
        row.AddView(text);

        _logRows.AddView(row, atTop ? 0 : _logRows.ChildCount);
        while (_logRows.ChildCount > MaxLogRows) _logRows.RemoveViewAt(_logRows.ChildCount - 1);
    }

    private void ShowServiceState()
    {
        if (ListenerService.StartError is { } error)
        {
            _statusText.SetTextColor(Color.OrangeRed);
            _statusText.Text = error;
        }
        else
        {
            _statusText.SetTextColor(Color.LightGreen);
            _statusText.Text = ListenerService.IsRunning ? $"Listening on {NetworkInfo.ListenUrl(this)}" : "Starting...";
        }
    }

    // ---------- First-run prompts ----------

    /// <summary>Android 13+ needs runtime permission to show the "running" notification.</summary>
    private void RequestNotificationPermission()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions(new[] { Manifest.Permission.PostNotifications }, NotificationPermissionRequest);
        else
            RequestIgnoreBatteryOptimizations();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == NotificationPermissionRequest)
        {
            ListenerService.Start(this);   // refresh the notification now that it may be shown
            RequestIgnoreBatteryOptimizations();
        }
    }

    /// <summary>Asks Android to exempt the app from battery optimization (Doze), unless already exempt.</summary>
    private void RequestIgnoreBatteryOptimizations()
    {
        if (IsIgnoringBatteryOptimizations()) return;
        try
        {
            var intent = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                                    Android.Net.Uri.Parse($"package:{PackageName}"));
            StartActivity(intent);
        }
        catch { /* some devices lack the dialog; the status line tells the user */ }
    }

    private bool IsIgnoringBatteryOptimizations() =>
        ((PowerManager?)GetSystemService(PowerService))?.IsIgnoringBatteryOptimizations(PackageName) == true;

    private void ShowBatteryState()
    {
        bool off = IsIgnoringBatteryOptimizations();
        _batteryText.SetTextColor(off ? Color.LightGreen : Color.Orange);
        _batteryText.Text = off ? "Battery optimization: off (good)"
                                : "Battery optimization: ON - presses may be missed when the phone sleeps";
    }

    /// <summary>
    /// Pads the root view by the system bar insets (status bar, nav bar, display cutout) plus a
    /// base margin, so content is never drawn under the bars when Android 15+ forces edge-to-edge.
    /// </summary>
    private sealed class SystemBarInsetsPadding(int margin) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            int l, t, r, b;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                (l, t, r, b) = (bars.Left, bars.Top, bars.Right, bars.Bottom);
            }
            else
            {
#pragma warning disable CA1422 // pre-API-30 inset properties; only used on old devices
                (l, t, r, b) = (insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop,
                                insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CA1422
            }
            v.SetPadding(l + margin, t + margin, r + margin, b + margin);
            return insets;
        }
    }
}