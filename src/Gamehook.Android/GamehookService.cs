using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Gamehook.Android;

/// Foreground service that owns the in-process Gamehook runtime. Android freezes or kills
/// background processes; a foreground service (with its notification) is what keeps the Web API
/// reachable for displays and emulators while the user is in another app.
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[Property("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE", Value = "Local Web API server for emulator displays")]
public class GamehookService : Service
{
    private const string ChannelId = "gamehook-service";
    private const int NotificationId = 1;
    private const string StopAction = "io.gamehook.mobile.STOP";

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(GamehookService));
        context.StartForegroundService(intent);
    }

    public static void Stop(Context context) =>
        context.StartService(new Intent(context, typeof(GamehookService)).SetAction(StopAction));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            _ = Task.Run(async () =>
            {
                await GamehookRuntime.StopAsync();
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            });
            return StartCommandResult.NotSticky;
        }

        // The foreground service type is an Android 14 requirement; earlier releases take no type.
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, BuildNotification());
        _ = Task.Run(GamehookRuntime.StartAsync);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _ = Task.Run(GamehookRuntime.StopAsync);
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Gamehook server", NotificationImportance.Low));

        var open = PendingIntent.GetActivity(
            this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var stop = PendingIntent.GetService(
            this, 1, new Intent(this, typeof(GamehookService)).SetAction(StopAction), PendingIntentFlags.Immutable);

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("Gamehook is running")
            .SetContentText($"Web API at {GamehookRuntime.ApiUrl}")
            .SetSmallIcon(ApplicationInfo!.Icon)
            .SetContentIntent(open)
            .SetOngoing(true)
            .AddAction(new Notification.Action.Builder(null, "Stop", stop).Build())
            .Build()!;
    }
}
