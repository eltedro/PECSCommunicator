using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;

namespace PECSListenerAndroid;

/// <summary>
/// TODO #2: always-on foreground service that owns the PecsHttpServer, so presses are received
/// with the app in the background and the screen off. Shows a persistent low-importance
/// notification and holds a partial wake lock + Wi-Fi lock while running.
/// The activity (when open) subscribes to the static events below to show the banner.
/// Declared in AndroidManifest.xml (foregroundServiceType="specialUse"; see the comment there).
/// </summary>
[Android.Runtime.Register("com.pecs.listener.ListenerService")]
public class ListenerService : Service
{
    public const string ActionStop = "com.pecs.listener.STOP";
    private const string ChannelId = "listener_status";
    private const int NotificationId = 1;

    // --- State and events for the UI (raised on a background thread). ---
    /// <summary>True while the server is listening.</summary>
    public static bool IsRunning { get; private set; }
    /// <summary>Start-up error, or null if running fine.</summary>
    public static string? StartError { get; private set; }
    /// <summary>Most recent press (tile, sender's time, received time), so a newly opened activity can show it.</summary>
    public static (string Tile, string Time, DateTime Received)? LastPress { get; private set; }
    public static event Action<string, string, string>? PressReceived;
    public static event Action<string>? HealthChecked;
    /// <summary>Running / stopped / error changed.</summary>
    public static event Action? StateChanged;

    private PecsHttpServer? _server;
    private PowerManager.WakeLock? _wakeLock;
    private WifiManager.WifiLock? _wifiLock;

    /// <summary>Starts (or keeps) the service running; safe to call repeatedly.</summary>
    public static void Start(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(ListenerService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Must call StartForeground promptly after StartForegroundService, every time.
        GoForeground();

        if (_server == null) StartServer();

        // START_STICKY: if Android kills the process, it recreates the service when it can.
        return StartCommandResult.Sticky;
    }

    private void GoForeground()
    {
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);
    }

    private void StartServer()
    {
        var server = new PecsHttpServer($"{Build.Manufacturer} {Build.Model}");
        server.PressReceived += (tile, time, from) =>
        {
            LastPress = (tile, time, DateTime.Now);
            PressReceived?.Invoke(tile, time, from);
        };
        server.HealthChecked += from => HealthChecked?.Invoke(from);

        try
        {
            server.Start();
            _server = server;
            StartError = null;
            IsRunning = true;
            AcquireLocks();
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            StartError = $"Port {PecsHttpServer.Port} is already in use, probably another PECS Listener is already running.";
        }
        catch (Exception ex)
        {
            StartError = $"Failed to start on port {PecsHttpServer.Port}: {ex.Message}";
        }
        StateChanged?.Invoke();
    }

    /// <summary>Keeps the CPU and Wi-Fi radio awake so requests arrive with the screen off.</summary>
    private void AcquireLocks()
    {
        var pm = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "PECSListener::Server");
        _wakeLock?.Acquire();

        var wifi = (WifiManager?)ApplicationContext?.GetSystemService(WifiService);
        // FullHighPerf is deprecated (API 34) in favor of FullLowLatency, but low-latency only
        // applies while the app is visible with the screen on; high-perf is the one that matters
        // with the screen off, and is still honored. Both are cheap; take the one that helps here.
#pragma warning disable CA1422
        _wifiLock = wifi?.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "PECSListener::Wifi");
#pragma warning restore CA1422
        _wifiLock?.SetReferenceCounted(false);
        _wifiLock?.Acquire();
    }

    private Notification BuildNotification()
    {
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        // Low importance: always visible in the shade, but no sound or pop-up.
        nm.CreateNotificationChannel(new NotificationChannel(ChannelId, "Listener status", NotificationImportance.Low));

        var openApp = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var stop = PendingIntent.GetService(this, 1,
            new Intent(this, typeof(ListenerService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable);

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("PECS Listener")
            .SetContentText($"PECS Listener running on {NetworkInfo.ListenUrl(this)}")
            .SetSmallIcon(Android.Resource.Drawable.StatNotifyChat)
            .SetContentIntent(openApp)
            .SetOngoing(true)
            .AddAction(new Notification.Action.Builder(null, "Stop", stop).Build())
            .Build();
    }

    public override void OnDestroy()
    {
        _server?.Dispose();
        _server = null;
        IsRunning = false;
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        if (_wifiLock?.IsHeld == true) _wifiLock.Release();
        StateChanged?.Invoke();
        base.OnDestroy();
    }
}
