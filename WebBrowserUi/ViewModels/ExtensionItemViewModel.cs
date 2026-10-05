using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Conqueror.Net.WebBrowserUi.ViewModels;

public partial class ExtensionItemViewModel : ObservableObject
{
    private readonly ExtensionManagementDialogViewModel _parentViewModel;

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
    public string? IconPath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool _isEnabled;

    /// <summary>Secondary line under the name: version plus a disabled marker.</summary>
    public string DisplayName => IsEnabled ? Version : $"{Version} — disabled";

    [ObservableProperty]
    private bool _isBusy;

    public ExtensionItemViewModel(
        string id, 
        string name, 
        string version, 
        string description, 
        bool isEnabled, 
        string? iconPath,
        ExtensionManagementDialogViewModel parentViewModel)
    {
        Id = id;
        Name = name;
        Version = version;
        Description = description;
        IconPath = iconPath;
        _parentViewModel = parentViewModel;
        // Assign the backing field (not the property) so OnIsEnabledChanged does not fire
        // and issue a service call for the initial loaded state.
        _isEnabled = isEnabled;
    }

    /// <summary>
    /// Called when the ToggleSwitch flips (via OnIsEnabledChanged below), not via Command:
    /// binding both IsChecked and Command double-fires — the switch toggles, then the
    /// command's re-entrancy toggles it back before the service call completes.
    /// </summary>
    partial void OnIsEnabledChanged(bool value)
    {
        ToggleStateCommand.Execute(null);
    }

    [RelayCommand]
    private async Task ToggleStateAsync()
    {
        // The switch already flipped IsEnabled; capture the desired state up front so the
        // revert path below restores the previous value on failure.
        var desired = IsEnabled;
        IsBusy = true;
        try
        {
            // Call back to main VM to perform the extension store operations.
            await _parentViewModel.ToggleExtensionAsync(Id, desired);
        }
        catch
        {
            // Revert state on failure
            IsEnabled = !desired;
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        IsBusy = true;
        try
        {
            await _parentViewModel.UninstallExtensionAsync(Id);
        }
        finally
        {
            IsBusy = false;
        }
    }
}