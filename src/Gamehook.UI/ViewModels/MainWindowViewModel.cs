using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gamehook.Domain;
using Gamehook.Infrastructure;
using Gamehook.Infrastructure.Drivers;

namespace Gamehook.UI.ViewModels;

// The window shell: one tab (InstanceViewModel) per GamehookInstances entry, plus the settings
// that apply to every instance. Instances can be added and removed from the UI or the REST API;
// either way the tabs are reconciled against GamehookInstances, which owns the order.
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly GamehookInstances instances;
    private readonly FilesystemProvider filesystemProvider;
    private readonly RetroArchConfigurationService retroArchConfiguration;
    private readonly SettingsService settings;
    private readonly IReadOnlyList<DriverRegistration> driverRegistrations;
    private int syncQueued;
    private bool disposed;

    public ObservableCollection<InstanceViewModel> Instances { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private InstanceViewModel? selectedInstance;

    [ObservableProperty]
    private bool isContinuousReadEnabled;

    public string WindowTitle => SelectedInstance?.WindowTitle ?? "Gamehook";

    public event Func<IReadOnlyList<string>, Task<bool>>? RetroArchNetworkCommandsUnavailable;

    public MainWindowViewModel(
        GamehookInstances instances,
        FilesystemProvider filesystemProvider,
        RetroArchConfigurationService retroArchConfiguration,
        SettingsService settings,
        IEnumerable<DriverRegistration> driverRegistrations)
    {
        this.instances = instances;
        this.filesystemProvider = filesystemProvider;
        this.retroArchConfiguration = retroArchConfiguration;
        this.settings = settings;
        this.driverRegistrations = driverRegistrations.ToArray();
        isContinuousReadEnabled = instances.IsContinuousReadEnabled;

        instances.InstanceAdded += OnInstancesChanged;
        instances.InstanceRemoved += OnInstancesChanged;
        instances.ContinuousReadChanged += OnContinuousReadChanged;
        SyncInstances();
    }

    [RelayCommand]
    private void AddInstance()
    {
        var (_, router) = instances.Add();
        SyncInstances();
        SelectedInstance = Instances.FirstOrDefault(instance => ReferenceEquals(instance.Router, router));
    }

    private bool CanCloseInstance(InstanceViewModel? instance) => Instances.Count > 1;

    [RelayCommand(CanExecute = nameof(CanCloseInstance))]
    private void CloseInstance(InstanceViewModel? instance)
    {
        instance ??= SelectedInstance;
        if (instance is null) return;

        var index = instances.IndexOf(instance.Router);
        if (index >= 0 && instances.Remove(index, out _))
        {
            SyncInstances();
        }
    }

    [RelayCommand]
    private void ToggleContinuousRead() => settings.Update(!instances.IsContinuousReadEnabled);

    // Add/remove can come from a Kestrel thread, and several can land before the UI thread runs -
    // coalesce into one reconcile against the current list rather than replaying each event.
    private void OnInstancesChanged(int index, GamehookRouter router)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            SyncInstances();
            return;
        }

        if (Interlocked.Exchange(ref syncQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(SyncInstances);
        }
    }

    private void OnContinuousReadChanged(bool enabled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnContinuousReadChanged(instances.IsContinuousReadEnabled));
            return;
        }

        if (!disposed) IsContinuousReadEnabled = enabled;
    }

    private void SyncInstances()
    {
        Interlocked.Exchange(ref syncQueued, 0);
        if (disposed) return;

        var routers = instances.Snapshot();
        var previousSelection = SelectedInstance;
        var previousSelectionIndex = previousSelection is null ? 0 : Instances.IndexOf(previousSelection);

        foreach (var removed in Instances.Where(instance => !routers.Contains(instance.Router)).ToArray())
        {
            Instances.Remove(removed);
            removed.PropertyChanged -= OnInstancePropertyChanged;
            removed.RetroArchNetworkCommandsUnavailable -= OnRetroArchNetworkCommandsUnavailable;
            removed.Dispose();
        }

        for (var index = 0; index < routers.Count; index++)
        {
            var existing = Instances.FirstOrDefault(instance => ReferenceEquals(instance.Router, routers[index]));
            if (existing is null)
            {
                existing = new InstanceViewModel(routers[index], filesystemProvider, retroArchConfiguration, settings, driverRegistrations);
                existing.PropertyChanged += OnInstancePropertyChanged;
                existing.RetroArchNetworkCommandsUnavailable += OnRetroArchNetworkCommandsUnavailable;
                Instances.Insert(index, existing);
            }
            else if (Instances.IndexOf(existing) is var current && current != index)
            {
                Instances.Move(current, index);
            }

            existing.Index = index;
            existing.ShowIndex = routers.Count > 1;
        }

        if (SelectedInstance is null || !Instances.Contains(SelectedInstance))
        {
            SelectedInstance = Instances.Count == 0
                ? null
                : Instances[Math.Clamp(previousSelectionIndex, 0, Instances.Count - 1)];
        }

        CloseInstanceCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedInstanceChanged(InstanceViewModel? oldValue, InstanceViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    private void OnInstancePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedInstance) && e.PropertyName == nameof(InstanceViewModel.WindowTitle))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    private Task<bool> OnRetroArchNetworkCommandsUnavailable(IReadOnlyList<string> files) =>
        RetroArchNetworkCommandsUnavailable?.Invoke(files) ?? Task.FromResult(false);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        instances.InstanceAdded -= OnInstancesChanged;
        instances.InstanceRemoved -= OnInstancesChanged;
        instances.ContinuousReadChanged -= OnContinuousReadChanged;
        foreach (var instance in Instances)
        {
            instance.PropertyChanged -= OnInstancePropertyChanged;
            instance.RetroArchNetworkCommandsUnavailable -= OnRetroArchNetworkCommandsUnavailable;
            instance.Dispose();
        }
    }
}
