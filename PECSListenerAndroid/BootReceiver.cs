using Android.Content;

namespace PECSListenerAndroid;

/// <summary>
/// TODO #2: starts the listener service after the phone reboots. Android 15+ blocks some foreground
/// service types (dataSync, camera, mediaPlayback, microphone, ...) from BOOT_COMPLETED, but
/// specialUse is allowed. Requires the app to have been opened at least once since install.
/// </summary>
[Android.Runtime.Register("com.pecs.listener.BootReceiver")]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context != null && intent?.Action == Intent.ActionBootCompleted)
            ListenerService.Start(context);
    }
}
