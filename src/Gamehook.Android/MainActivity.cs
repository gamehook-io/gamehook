using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Gamehook.Android;

[Application]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    protected AndroidApp(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}

[Activity(
    Label = "Gamehook",
    Theme = "@style/Theme.AppCompat.NoActionBar",
    MainLauncher = true,
    Icon = "@drawable/icon",
    ScreenOrientation = ScreenOrientation.Portrait,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    /// The visible activity; the overlay browser needs an Activity context for its dialogs and pickers.
    public static MainActivity? Current { get; private set; }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;

        // The foreground service runs without it, but its notification (and Stop button) is hidden.
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.PostNotifications], 0);
        }

        // Serve from launch: the point of the app is a Web API that's up whenever it's installed.
        if (GamehookRuntime.State.Status == RuntimeStatus.Stopped)
            GamehookService.Start(this);
    }
}
