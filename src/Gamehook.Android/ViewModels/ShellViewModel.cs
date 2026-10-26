using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gamehook.Domain;
using Gamehook.Infrastructure;
using Gamehook.Infrastructure.Drivers;
using Microsoft.Extensions.DependencyInjection;

namespace Gamehook.Android.ViewModels;

/// Root view model: follows the foreground service's lifecycle and, while the engine is running,
/// keeps one InstanceViewModel per GamehookInstances entry (instances can also be added or
/// removed over the Web API, so the list is reconciled against the engine rather than owned here).
internal sealed partial class ShellViewModel : ObservableObject
{
    private GamehookInstances? instances;
    private int syncQueued;

    public ShellViewModel()
    {
        GamehookRuntime.StateChanged += _ => Dispatcher.UIThread.Post(OnRuntimeStateChanged);
        OnRuntimeStateChanged();
    }

    public ObservableCollection<InstanceViewModel> Instances { get; } = [];

    [ObservableProperty]
    private InstanceViewModel? selectedInstance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsServerMessageVisible), nameof(ServerTitle), nameof(ServerDetail), nameof(CanStart))]
    private RuntimeState runtime = GamehookRuntime.State;

    [ObservableProperty]
    private bool isMenuOpen;

    [ObservableProperty]
    private bool isInstancesOpen;

    public bool IsRunning => Runtime.Status == RuntimeStatus.Running && SelectedInstance is not null;

    public bool IsServerMessageVisible => !IsRunning;

    public bool CanStart => Runtime.Status is RuntimeStatus.Stopped or RuntimeStatus.Failed;

    public bool HasMultipleInstances => Instances.Count > 1;

    public string InstanceLabel => SelectedInstance is null ? "" : $"{SelectedInstance.Index + 1}/{Instances.Count}";

    public string ApiUrl => GamehookRuntime.ApiUrl;

    public string DocsUrl => $"{GamehookRuntime.ApiUrl}/scalar";

    public string ServerTitle => Runtime.Status switch
    {
        RuntimeStatus.Starting => "Starting Gamehook…",
        RuntimeStatus.Failed => "Gamehook could not start",
        RuntimeStatus.Stopped => "Gamehook is stopped",
        _ => "Loading…",
    };

    public string ServerDetail => Runtime.Status switch
    {
        RuntimeStatus.Starting => "The first start downloads the official mappers, so it needs a network connection.",
        RuntimeStatus.Failed => Runtime.Error ?? "Unknown error.",
        RuntimeStatus.Stopped => "Start it to load a mapper and serve the local Web API.",
        _ => string.Empty,
    };

    private void OnRuntimeStateChanged()
    {
        Runtime = GamehookRuntime.State;
        if (Runtime.Status == RuntimeStatus.Running && GamehookRuntime.Services is { } services)
        {
            Attach(services);
        }
        else
        {
            Detach();
        }

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsServerMessageVisible));
    }

    private FilesystemProvider? filesystem;
    private RetroArchConfigurationService? retroArch;
    private SettingsService? settings;
    private DriverRegistration[] drivers = [];

    private void Attach(IServiceProvider services)
    {
        if (instances is not null) return;

        instances = services.GetRequiredService<GamehookInstances>();
        filesystem = services.GetRequiredService<FilesystemProvider>();
        retroArch = services.GetRequiredService<RetroArchConfigurationService>();
        settings = services.GetRequiredService<SettingsService>();
        drivers = services.GetServices<DriverRegistration>().ToArray();
        instances.InstanceAdded += OnInstancesChanged;
        instances.InstanceRemoved += OnInstancesChanged;
        SyncInstances();
    }

    private void Detach()
    {
        if (instances is not null)
        {
            instances.InstanceAdded -= OnInstancesChanged;
            instances.InstanceRemoved -= OnInstancesChanged;
            instances = null;
        }

        foreach (var instance in Instances) instance.Dispose();
        Instances.Clear();
        SelectedInstance = null;
        IsMenuOpen = false;
        IsInstancesOpen = false;
    }

    // Add/remove can come from a Kestrel thread, and several can land before the UI thread runs -
    // coalesce into one reconcile against the engine's current list.
    private void OnInstancesChanged(int index, GamehookRouter router)
    {
        if (Interlocked.Exchange(ref syncQueued, 1) == 0) Dispatcher.UIThread.Post(SyncInstances);
    }

    private void SyncInstances()
    {
        Interlocked.Exchange(ref syncQueued, 0);
        if (instances is null) return;

        var routers = instances.Snapshot();
        var previous = SelectedInstance is null ? 0 : Instances.IndexOf(SelectedInstance);

        foreach (var removed in Instances.Where(instance => !routers.Contains(instance.Router)).ToArray())
        {
            Instances.Remove(removed);
            removed.Dispose();
        }

        for (var index = 0; index < routers.Count; index++)
        {
            var existing = Instances.FirstOrDefault(instance => ReferenceEquals(instance.Router, routers[index]));
            if (existing is null)
            {
                existing = new InstanceViewModel(routers[index], this, filesystem!, retroArch!, settings!, drivers);
                Instances.Insert(index, existing);
            }
            else if (Instances.IndexOf(existing) is var current && current != index)
            {
                Instances.Move(current, index);
            }

            existing.Index = index;
        }

        if (SelectedInstance is null || !Instances.Contains(SelectedInstance))
            SelectedInstance = Instances.Count == 0 ? null : Instances[Math.Clamp(previous, 0, Instances.Count - 1)];

        OnPropertyChanged(nameof(HasMultipleInstances));
        OnPropertyChanged(nameof(InstanceLabel));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsServerMessageVisible));
    }

    partial void OnSelectedInstanceChanged(InstanceViewModel? value)
    {
        OnPropertyChanged(nameof(InstanceLabel));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsServerMessageVisible));
    }

    [RelayCommand]
    private void AddInstance()
    {
        if (instances is null) return;
        var (_, router) = instances.Add();
        SyncInstances();
        SelectedInstance = Instances.FirstOrDefault(instance => ReferenceEquals(instance.Router, router));
        IsInstancesOpen = false;
    }

    [RelayCommand]
    private void CloseInstance(InstanceViewModel? instance)
    {
        instance ??= SelectedInstance;
        if (instances is null || instance is null || Instances.Count <= 1) return;
        var index = instances.IndexOf(instance.Router);
        if (index >= 0 && instances.Remove(index, out _)) SyncInstances();
    }

    [RelayCommand]
    private void SelectInstance(InstanceViewModel instance)
    {
        SelectedInstance = instance;
        IsInstancesOpen = false;
    }

    [RelayCommand]
    private void ToggleMenu() => IsMenuOpen = !IsMenuOpen;

    [RelayCommand]
    private void ToggleInstances() => IsInstancesOpen = !IsInstancesOpen;

    [RelayCommand]
    private void CloseSheets()
    {
        IsMenuOpen = false;
        IsInstancesOpen = false;
    }

    [RelayCommand]
    private void StartServer() => GamehookService.Start(global::Android.App.Application.Context);

    [RelayCommand]
    private void StopServer()
    {
        IsMenuOpen = false;
        GamehookService.Stop(global::Android.App.Application.Context);
    }

    [RelayCommand]
    private void OpenDocs()
    {
        IsMenuOpen = false;
        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView, global::Android.Net.Uri.Parse(DocsUrl))!
            .AddFlags(global::Android.Content.ActivityFlags.NewTask);
        global::Android.App.Application.Context.StartActivity(intent);
    }

    /// Android back: closes sheets, then peels the selected instance's layers. False = let Android handle it.
    public bool HandleBack()
    {
        if (IsMenuOpen || IsInstancesOpen)
        {
            CloseSheets();
            return true;
        }

        return SelectedInstance?.GoBack() ?? false;
    }
}
