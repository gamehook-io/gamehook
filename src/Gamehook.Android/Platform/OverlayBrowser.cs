using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Webkit;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using AndroidUri = Android.Net.Uri;

namespace Gamehook.Android;

/// The in-app browser for the hosted overlays (https://overlays.gamehook.io). One WebView lives
/// for the whole process so an overlay keeps running, and keeps its state, while the user looks
/// at another tab.
internal static class OverlayBrowser
{
    public const string HomeUrl = "https://overlays.gamehook.io/";

    // The overlays site may only navigate within gamehook.io; anything else opens in the real browser.
    private const string AllowedHostSuffix = ".gamehook.io";

    private static WebView? webView;

    public static bool CanGoBack => webView?.CanGoBack() == true;

    public static void GoBack() => webView?.GoBack();

    public static void Reload()
    {
        if (webView is null) return;
        if (webView.Url is null) webView.LoadUrl(HomeUrl);
        else webView.Reload();
    }

    public static WebView GetOrCreate()
    {
        if (webView is not null) return webView;

        var view = new WebView((Context?)MainActivity.Current ?? Application.Context);
        var settings = view.Settings;
        settings.JavaScriptEnabled = true;
        // Overlays keep their own state in localStorage.
        settings.DomStorageEnabled = true;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = false;
        settings.SetSupportMultipleWindows(false);
        // The page is https; the Web API is plain http on loopback. Chromium treats loopback as
        // potentially trustworthy, but WebView's own policy blocks mixed content unless relaxed.
        settings.MixedContentMode = MixedContentHandling.CompatibilityMode;
        view.SetBackgroundColor(Color.ParseColor("#151617"));
        view.SetWebViewClient(new Client());
        view.SetWebChromeClient(new WebChromeClient());
#if DEBUG
        WebView.SetWebContentsDebuggingEnabled(true);
#endif
        view.LoadUrl(HomeUrl);
        return webView = view;
    }

    private sealed class Client : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url is not { } url) return false;
            if (IsAllowed(url)) return false;

            var intent = new Intent(Intent.ActionView, url).AddFlags(ActivityFlags.NewTask);
            ((Context?)MainActivity.Current ?? Application.Context).StartActivity(intent);
            return true;
        }

        private static bool IsAllowed(AndroidUri url) =>
            url.Scheme == "https" && url.Host is { } host
            && (host == AllowedHostSuffix[1..] || host.EndsWith(AllowedHostSuffix, StringComparison.Ordinal));
    }
}

/// Hosts the shared WebView inside the Avalonia tree. Avalonia creates and destroys the native
/// control as this host enters and leaves the visual tree; the WebView itself must outlive that.
internal sealed class OverlayBrowserHost : NativeControlHost
{
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var view = OverlayBrowser.GetOrCreate();
        (view.Parent as global::Android.Views.ViewGroup)?.RemoveView(view);
        return new AndroidViewControlHandle(view);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Detach only. Disposing the handle would be fine for a throwaway view, not for this one.
        if (control is AndroidViewControlHandle { View: { } view })
            (view.Parent as global::Android.Views.ViewGroup)?.RemoveView(view);
    }
}
