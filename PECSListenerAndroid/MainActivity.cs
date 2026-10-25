// PECS Listener for Android - receives tile presses from PECSCommunicator over Wi-Fi.
//
// Android TODO list:
// [x] 1. Android listener project: embedded HTTP server on port 5050 at /pecs/,
//        same GET health check / POST press / 405 behavior as the Windows Listener.
// [ ] 2. Always-on foreground service with boot start and battery-optimization prompt.
// [ ] 3. Alerts: per-tile color banner + high-priority notification, per-tile sound,
//        TTS "Ian wants {tile}", vibrate.
// [ ] 4. Log screen + CSV in the same format as the laptop.
// [ ] 5. Communicator multi-listener: ListenerUrls list with per-listener status / Test Connection.
// [ ] 6. Fixed IPs: router DHCP reservations + phone "Phone MAC" setting.

using Android.Graphics;
using Android.Net;
using Android.Net.Wifi;
using Android.Views;
using Android.Widget;
using System.Net.Sockets;

namespace PECSListenerAndroid;

[Activity(Label = "PECS Listener", MainLauncher = true, Theme = "@android:style/Theme.DeviceDefault.NoActionBar", ScreenOrientation = Android.Content.PM.ScreenOrientation.Portrait)]
public class MainActivity : Activity
{
    private TextView _statusText = null!;
    private TextView _bannerText = null!;
    private TextView _timeText = null!;

    // TODO #2: this moves into the foreground service; the activity will just display its events.
    private PecsHttpServer? _server;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);   // keep visible during testing
        SetContentView(BuildLayout());
        StartServer();
    }

    /// <summary>Builds the screen in code: status line on top, big banner below (mirrors the laptop).</summary>
    private View BuildLayout()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.Rgb(30, 30, 30));
        // Base 32px margin plus whatever the system bars need (see SystemBarInsetsPadding).
        root.SetOnApplyWindowInsetsListener(new SystemBarInsetsPadding(32));

        _statusText = new TextView(this) { Text = "Starting..." };
        _statusText.SetTextColor(Color.LightGreen);
        _statusText.TextSize = 16;
        // Full width so long messages wrap inside the margins instead of running off screen.
        root.AddView(_statusText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _bannerText = new TextView(this) { Text = "Waiting for Ian...", Gravity = GravityFlags.Center };
        _bannerText.SetTextColor(Color.White);
        _bannerText.SetBackgroundColor(Color.Rgb(0, 100, 0));
        _bannerText.TextSize = 34;
        _bannerText.SetTypeface(null, TypefaceStyle.Bold);
        _bannerText.SetPadding(16, 80, 16, 80);
        var bannerParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = 32 };
        root.AddView(_bannerText, bannerParams);

        _timeText = new TextView(this) { Gravity = GravityFlags.Center };
        _timeText.SetTextColor(Color.LightGray);
        _timeText.TextSize = 18;
        root.AddView(_timeText);

        return root;
    }

    private void StartServer()
    {
        var machine = $"{Android.OS.Build.Manufacturer} {Android.OS.Build.Model}";
        _server = new PecsHttpServer(machine);
        _server.PressReceived += OnPress;
        _server.HealthChecked += from =>
            RunOnUiThread(() => _statusText.Text = $"{ListeningText()} - last connection test from {from} at {DateTime.Now:HH:mm:ss}");

        try
        {
            _server.Start();
            _statusText.Text = ListeningText();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            ShowStartError($"Port {PecsHttpServer.Port} is already in use, probably another PECS Listener is already running.");
        }
        catch (Exception ex)
        {
            ShowStartError($"Failed to start on port {PecsHttpServer.Port}: {ex.Message}");
        }
    }

    private void ShowStartError(string message)
    {
        _statusText.SetTextColor(Color.OrangeRed);
        _statusText.Text = message;
    }

    /// <summary>Called on a server thread; marshals to the UI thread to update the banner.</summary>
    private void OnPress(string tile, string time, string fromIp)
    {
        var received = DateTime.Now.ToString("HH:mm:ss");
        RunOnUiThread(() =>
        {
            _bannerText.Text = $"Ian pressed: {tile}";
            _timeText.Text = string.IsNullOrEmpty(time) ? $"received {received}" : $"sent {time}, received {received}";
        });
    }

    /// <summary>"Listening on 192.168.x.y:5050" so the URL to put in the Communicator is visible.</summary>
    private string ListeningText()
    {
        var ip = GetWifiIp();
        return ip == null ? $"Listening on port {PecsHttpServer.Port}" : $"Listening on http://{ip}:{PecsHttpServer.Port}/pecs";
    }

    /// <summary>
    /// The phone's Wi-Fi IPv4 address: a non-loopback site-local (192.168.x / 10.x / 172.16-31.x)
    /// address, preferring wlan0. Falls back to the active network's LinkProperties.
    /// </summary>
    private string? GetWifiIp()
    {
        try
        {
            var candidates = new List<(string Name, string Ip)>();
            var nics = Java.Net.NetworkInterface.NetworkInterfaces;
            while (nics != null && nics.HasMoreElements)
            {
                if (nics.NextElement() is not Java.Net.NetworkInterface nic || !nic.IsUp || nic.IsLoopback) continue;
                var addrs = nic.InetAddresses;
                while (addrs != null && addrs.HasMoreElements)
                {
                    if (addrs.NextElement() is Java.Net.Inet4Address a && a.IsSiteLocalAddress && a.HostAddress != null)
                        candidates.Add((nic.Name ?? "", a.HostAddress));
                }
            }
            var best = candidates.FirstOrDefault(c => c.Name == "wlan0");
            if (best.Ip != null) return best.Ip;
            if (candidates.Count > 0) return candidates[0].Ip;
        }
        catch { /* fall through to ConnectivityManager */ }

        try
        {
            var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
            var props = cm?.GetLinkProperties(cm.ActiveNetwork);
            return props?.LinkAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a is Java.Net.Inet4Address)?.HostAddress;
        }
        catch { return null; }
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

    protected override void OnDestroy()
    {
        _server?.Dispose();
        base.OnDestroy();
    }
}
