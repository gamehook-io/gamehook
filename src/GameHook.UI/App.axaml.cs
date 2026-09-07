using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GameHook.Infrastructure.AppUpdate;
using GameHook.UI.ViewModels;
using GameHook.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameHook.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = Services.GetRequiredService<MainWindowViewModel>();
            var logger = Services.GetRequiredService<ILogger<App>>();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

#if !DEBUG
            // A deliberate early exit is not a failed launch.
            desktop.Exit += (_, _) => CrashGuard.MarkHealthy();
            // A short soak, not "window created" itself - a build that crashes a second or two
            // into rendering (not just instantly) should still count against CrashGuard's
            // threshold in Program.cs rather than resetting it prematurely.
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(
                _ => CrashGuard.MarkHealthy(),
                TaskScheduler.Default);
#endif

            // Last-resort net: any exception that reaches the dispatcher loop uncaught (an
            // async void event handler, a RelayCommand, a binding callback, ...) would otherwise
            // terminate the whole process. Surface it as a status message instead.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                viewModel.Status = $"Error: {e.Exception.Message}";
                logger.LogError(e.Exception, "Unhandled exception on the UI dispatcher.");
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
