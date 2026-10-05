using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conqueror.Net.WebBrowserUi.Services;

namespace Conqueror.Net.WebBrowserUi.ViewModels;

public partial class ExtensionManagementDialogViewModel : ObservableObject
{
    private readonly IExtensionService _extensionService;

    [ObservableProperty]
    private ObservableCollection<ExtensionItemViewModel> _extensions = new();

    /// <summary>
    /// Live-filtered view of <see cref="Extensions"/> driven by <see cref="SearchQuery"/>.
    /// A plain LINQ property (not a CollectionView) because Avalonia 11 binds IEnumerable
    /// fine and this avoids the extra ICollectionView dependency for a small list.
    /// </summary>
    public IEnumerable<ExtensionItemViewModel> FilteredExtensions =>
        string.IsNullOrWhiteSpace(SearchQuery)
            ? Extensions
            : Extensions.Where(e =>
                e.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)
                || e.Description.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)
                || e.Id.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

    [ObservableProperty]
    private ExtensionItemViewModel? _selectedExtension;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public ExtensionManagementDialogViewModel(IExtensionService extensionService)
    {
        _extensionService = extensionService;
    }

    // Default constructor for design-time / XAML previews
    public ExtensionManagementDialogViewModel() : this(null!) { }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading extensions...";
        try
        {
            Extensions.Clear();
            if (_extensionService != null)
            {
                var loadedExtensions = await _extensionService.GetInstalledExtensionsAsync();
                foreach (var ext in loadedExtensions)
                {
                    Extensions.Add(new ExtensionItemViewModel(
                        ext.Id, ext.Name, ext.Version, ext.Description, ext.IsEnabled, ext.IconPath, this));
                }
            }
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load extensions: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task InstallFromManifestAsync(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return;

        IsLoading = true;
        try
        {
            var newExt = await _extensionService.LoadUnpackedExtensionAsync(manifestPath);
            Extensions.Add(new ExtensionItemViewModel(
                newExt.Id, newExt.Name, newExt.Version, newExt.Description, newExt.IsEnabled, newExt.IconPath, this));
            RefreshFiltered();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Installation failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ToggleExtensionAsync(string extensionId, bool enable)
    {
        if (_extensionService == null) return;
        
        if (enable)
            await _extensionService.EnableExtensionAsync(extensionId);
        else
            await _extensionService.DisableExtensionAsync(extensionId);
    }

    public async Task UninstallExtensionAsync(string extensionId)
    {
        if (_extensionService == null) return;

        await _extensionService.UnloadExtensionAsync(extensionId);
        var item = Extensions.FirstOrDefault(x => x.Id == extensionId);
        if (item != null)
        {
            Extensions.Remove(item);
            RefreshFiltered();
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        RefreshFiltered();
    }

    private void RefreshFiltered() => OnPropertyChanged(nameof(FilteredExtensions));
}