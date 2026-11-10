using Avalonia.Controls;
using Avalonia.Interactivity;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Windows;

/// <summary>
/// Dialog for creating a new local Minecraft instance.
/// The user picks a name, MC version (releases only), mod loader, and loader version.
/// Returns a <see cref="LocalInstance"/> via <see cref="Window.Close(object)"/>
/// when the user clicks Save, or null on Cancel.
/// </summary>
public partial class AddLocalInstanceWindow : Window
{
    private readonly MojangMetaClient   _mojangMeta   = new();
    private readonly FabricMetaClient   _fabricMeta   = new();
    private readonly ForgeMetaClient    _forgeMeta    = new();
    private readonly NeoForgeMetaClient _neoForgeMeta = new();
    private readonly QuiltMetaClient    _quiltMeta    = new();

    private List<MojangVersionEntry> _mcVersions = [];

    public AddLocalInstanceWindow(LocalInstance? existing = null, bool loadMetadata = true, string? initialVersionId = null)
    {
        _existingInstance = existing;
        _requestedVersionId = initialVersionId;
        InitializeComponent();
        InitializePackSelection();
        _loaderComboBox.SelectedIndex = 0; // Vanilla
        _nameTextBox.TextChanged += (_, _) => UpdateSaveEnabled();
        _loaderVersionComboBox.SelectionChanged += (_, _) => UpdateSaveEnabled();
        if (loadMetadata) Loaded += async (_, _) => await LoadMcVersionsAsync();
        if (existing is not null) Loaded += async (_, _) => await LoadExistingPackAsync();
        if (existing is null) Opened += (_, _) => StartGuidedTips();
    }

    // ── MC version loading ─────────────────────────────────────────────

    private async Task LoadMcVersionsAsync()
    {
        try
        {
            var manifest = await _mojangMeta.GetVersionManifestAsync();
            _mcVersions = manifest.Versions
                .Where(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase))
                .ToList();

            _mcVersionComboBox.ItemsSource = _mcVersions.Select(v => v.Id).ToList();
            _mcVersionComboBox.PlaceholderText = "Select version";

            if (_mcVersions.Count > 0)
                _mcVersionComboBox.SelectedIndex = 0; // latest release
        }
        catch
        {
            _mcVersionComboBox.PlaceholderText = "Failed to load versions";
        }
    }

    // ── MC version changed → reload loader versions ────────────────────

    private async void McVersionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSaveEnabled();
        await LoadLoaderVersionsAsync();
    }

    // ── Loader type changed → show/hide loader version panel ───────────

    private async void LoaderComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var loader = GetSelectedLoader();
        _loaderVersionPanel.IsVisible = !string.IsNullOrEmpty(loader);

        if (string.IsNullOrEmpty(loader))
        {
            _loaderVersionComboBox.ItemsSource = null;
        }
        else
        {
            await LoadLoaderVersionsAsync();
        }

        UpdateSaveEnabled();
    }

    private async Task LoadLoaderVersionsAsync()
    {
        var loader    = GetSelectedLoader();
        var mcVersion = _mcVersionComboBox.SelectedItem as string;

        if (string.IsNullOrEmpty(loader) || string.IsNullOrEmpty(mcVersion))
            return;

        _loaderVersionComboBox.ItemsSource = null;
        _loaderVersionComboBox.PlaceholderText = "Loading…";

        try
        {
            List<string> versions;

            switch (loader)
            {
                case "fabric":
                    var fabricVersions = await _fabricMeta.GetLoaderVersionsAsync(mcVersion);
                    versions = fabricVersions
                        .Select(v => v.Loader?.Version ?? "")
                        .Where(v => !string.IsNullOrEmpty(v))
                        .ToList();
                    break;

                case "quilt":
                    var quiltVersions = await _quiltMeta.GetLoaderVersionsAsync(mcVersion);
                    versions = quiltVersions
                        .Select(v => v.Loader?.Version ?? "")
                        .Where(v => !string.IsNullOrEmpty(v))
                        .ToList();
                    break;

                case "forge":
                    versions = await _forgeMeta.GetAvailableVersionsAsync(mcVersion);
                    break;

                case "neoforge":
                    versions = await _neoForgeMeta.GetAvailableVersionsAsync(mcVersion);
                    break;

                default:
                    versions = [];
                    break;
            }

            if (versions.Count > 0)
            {
                _loaderVersionComboBox.ItemsSource = versions;
                _loaderVersionComboBox.SelectedIndex = 0; // latest
                _loaderVersionComboBox.PlaceholderText = "Select version";
            }
            else
            {
                _loaderVersionComboBox.PlaceholderText = "No versions available";
            }
        }
        catch
        {
            _loaderVersionComboBox.PlaceholderText = "Failed to load versions";
        }

        UpdateSaveEnabled();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private string GetSelectedLoader()
    {
        if (_loaderComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return tag;
        return "";
    }

    private void UpdateSaveEnabled()
    {
        if (_saveButton is null) return;
        if (_mode.SelectedIndex > 0)
        {
            _saveButton.IsEnabled = !_packBusy && _selectedPack is not null && !string.IsNullOrWhiteSpace(_nameTextBox.Text);
            return;
        }
        var hasName      = !string.IsNullOrWhiteSpace(_nameTextBox.Text);
        var hasMcVersion = _mcVersionComboBox.SelectedItem is not null;
        var loader       = GetSelectedLoader();
        var hasLoader    = string.IsNullOrEmpty(loader) || _loaderVersionComboBox.SelectedItem is not null;

        _saveButton.IsEnabled = !_packBusy && hasName && hasMcVersion && hasLoader;
    }

    // ── Button handlers ────────────────────────────────────────────────

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_installing) { _packCancellation.Cancel(); return; }
        Close(null);
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_mode.SelectedIndex > 0) { await CreatePackInstanceAsync(); return; }
        var loader = GetSelectedLoader();
        var loaderVersion = string.IsNullOrEmpty(loader)
            ? ""
            : (_loaderVersionComboBox.SelectedItem as string ?? "");

        var instance = new LocalInstance
        {
            Id               = Guid.NewGuid().ToString("N")[..12],
            Name             = _nameTextBox.Text?.Trim() ?? "",
            MinecraftVersion = _mcVersionComboBox.SelectedItem as string ?? "",
            Loader           = loader,
            LoaderVersion    = loaderVersion,
            ServerAddress    = _serverAddressTextBox.Text?.Trim() ?? "",
            CreatedAtUtc     = DateTime.UtcNow,
        };

        Close(instance);
    }
}
