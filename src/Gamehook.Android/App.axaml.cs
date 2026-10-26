using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Gamehook.Android.ViewModels;
using Gamehook.Android.Views;

namespace Gamehook.Android;

public class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Android hosts a single view (no windows), unlike the desktop lifetime.
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new MainView { DataContext = new ShellViewModel() };

        base.OnFrameworkInitializationCompleted();
    }
}
