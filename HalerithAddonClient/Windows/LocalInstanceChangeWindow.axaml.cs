using Avalonia.Controls;
using Avalonia.Interactivity;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;

namespace HalerithAddonClient.Windows;

public partial class LocalInstanceChangeWindow : Window
{
    private readonly LocalInstance _instance;
    private readonly MojangMetaClient _mojangMeta = new();
    private readonly FabricMetaClient _fabricMeta = new();
    private readonly ForgeMetaClient _forgeMeta = new();
    private readonly NeoForgeMetaClient _neoForgeMeta = new();
    private readonly QuiltMetaClient _quiltMeta = new();

    private List<MojangVersionEntry> _mcVersions = [];
    private bool _loadingVersions;
    private bool _applying;
    private bool _packLocked;

    public LocalInstanceChangeWindow(LocalInstance instance)
    {
        _instance = instance;
        InitializeComponent();
        _packLocked = HalerithAddonClient.Core.LocalModpacks.LocalModpackStore.Load(instance.GetInstanceDirectory()) is not null;

        _currentRuntimeText.Text = $"Current: {RuntimeVersionInfo.From(instance).Display}";
        SelectLoader(instance.Loader);
        if (!_packLocked) Loaded += async (_, _) => await LoadMcVersionsAsync();
        else
        {
            _statusText.Text = "Runtime versions are set by the modpack. Choose a release from the modpack row instead.";
            SetInputsEnabled(false);
        }
        Closed += (_, _) => DisposeMetaClients();
        UpdateTargetState();
    }

    private async Task LoadMcVersionsAsync()
    {
        try
        {
            _loadingVersions = true;
            var manifest = await _mojangMeta.GetVersionManifestAsync();
            _mcVersions = manifest.Versions
                .Where(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var versions = _mcVersions.Select(v => v.Id).ToList();
            _mcVersionComboBox.ItemsSource = versions;
            _mcVersionComboBox.PlaceholderText = "Select version";

            var currentIndex = versions.FindIndex(v =>
                string.Equals(v, _instance.MinecraftVersion, StringComparison.OrdinalIgnoreCase));
            _mcVersionComboBox.SelectedIndex = currentIndex >= 0 ? currentIndex : 0;
        }
        catch
        {
            _mcVersionComboBox.PlaceholderText = "Failed to load versions";
        }
        finally
        {
            _loadingVersions = false;
            await LoadLoaderVersionsAsync(preferredVersion: _instance.LoaderVersion);
            UpdateTargetState();
        }
    }

    private async void McVersionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingVersions) return;
        await LoadLoaderVersionsAsync();
        UpdateTargetState();
    }

    private async void LoaderComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var loader = GetSelectedLoader();
        _loaderVersionPanel.IsVisible = !string.IsNullOrWhiteSpace(loader);

        if (string.IsNullOrWhiteSpace(loader))
        {
            _loaderVersionComboBox.ItemsSource = null;
        }
        else
        {
            await LoadLoaderVersionsAsync(preferredVersion:
                string.Equals(loader, _instance.Loader, StringComparison.OrdinalIgnoreCase)
                    ? _instance.LoaderVersion
                    : "");
        }

        UpdateTargetState();
    }

    private void LoaderVersionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateTargetState();
    }

    private async Task LoadLoaderVersionsAsync(string preferredVersion = "")
    {
        var loader = GetSelectedLoader();
        var mcVersion = _mcVersionComboBox.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(loader) || string.IsNullOrWhiteSpace(mcVersion))
            return;

        _loaderVersionComboBox.ItemsSource = null;
        _loaderVersionComboBox.PlaceholderText = "Loading...";

        try
        {
            List<string> versions = loader switch
            {
                "fabric" => (await _fabricMeta.GetLoaderVersionsAsync(mcVersion))
                    .Select(v => v.Loader?.Version ?? "")
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList(),
                "quilt" => (await _quiltMeta.GetLoaderVersionsAsync(mcVersion))
                    .Select(v => v.Loader?.Version ?? "")
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList(),
                "forge" => await _forgeMeta.GetAvailableVersionsAsync(mcVersion),
                "neoforge" => await _neoForgeMeta.GetAvailableVersionsAsync(mcVersion),
                _ => []
            };

            _loaderVersionComboBox.ItemsSource = versions;
            _loaderVersionComboBox.PlaceholderText = versions.Count > 0 ? "Select version" : "No versions available";

            var preferred = !string.IsNullOrWhiteSpace(preferredVersion)
                ? versions.FirstOrDefault(v => string.Equals(v, preferredVersion, StringComparison.OrdinalIgnoreCase))
                : null;
            var selected = preferred ?? versions.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(selected))
            {
                _loaderVersionComboBox.SelectedItem = selected;
            }
        }
        catch
        {
            _loaderVersionComboBox.PlaceholderText = "Failed to load versions";
        }
    }

    private async void ApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_applying) return;
        if (HalerithAddonClient.Core.LocalModpacks.LocalModpackStore.Load(_instance.GetInstanceDirectory()) is not null)
        {
            _statusText.Text = "This instance's runtime is set by its modpack. Choose a pack release from the modpack row instead.";
            return;
        }

        var target = GetTargetRuntime();
        if (target is null) return;

        var confirm = await new MessageBoxWindow(
            "Change Local Instance",
            $"Change this local instance from {RuntimeVersionInfo.From(_instance).Display} to {target.Display}?\n\nThe existing instance folder will be kept. Content may need updating after the change.",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning).ShowDialog(this);

        if (!confirm) return;

        _applying = true;
        SetInputsEnabled(false);
        _buildProgressBar.IsVisible = true;
        _statusText.Text = $"Building {target.Display}...";

        try
        {
            var existing = ActiveBuildTracker.Get(target.MinecraftVersion, target.Loader, target.LoaderVersion);
            if (existing is not null && !existing.BuildTask.IsCompleted)
            {
                await PollBuildProgressAsync(existing);
                if (!string.IsNullOrWhiteSpace(existing.Error))
                    throw new InvalidOperationException(existing.Error);
            }
            else
            {
                if (existing is not null)
                    ActiveBuildTracker.Remove(target.MinecraftVersion, target.Loader, target.LoaderVersion);

                _statusText.Text = $"Building {target.Display}...";

                var buildTask = Task.Run(() => ManageLocalInstancesWindow.RunBuildPipelineAsync(
                    target.MinecraftVersion, target.Loader, target.LoaderVersion));
                var state = ActiveBuildTracker.Register(target.MinecraftVersion, target.Loader, target.LoaderVersion, buildTask);

                await PollBuildProgressAsync(state);
                await buildTask;

                if (!string.IsNullOrWhiteSpace(state.Error))
                    throw new InvalidOperationException(state.Error);

                ActiveBuildTracker.Remove(target.MinecraftVersion, target.Loader, target.LoaderVersion);
            }

            _instance.MinecraftVersion = target.MinecraftVersion;
            _instance.Loader = target.Loader;
            _instance.LoaderVersion = target.LoaderVersion;

            Close(true);
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Change failed: {ex.Message}";
            _applying = false;
            SetInputsEnabled(true);
        }
    }

    private async Task PollBuildProgressAsync(ActiveBuildTracker.BuildState state)
    {
        while (!state.BuildTask.IsCompleted)
        {
            await Task.Delay(250);
            var total = Math.Max(state.Total, 1);
            _buildProgressBar.Maximum = total;
            _buildProgressBar.Value = state.Completed;
            _statusText.Text = string.IsNullOrWhiteSpace(state.Label) ? "Building..." : state.Label;
        }

        _buildProgressBar.Maximum = Math.Max(state.Total, 1);
        _buildProgressBar.Value = state.Completed;
        _statusText.Text = string.IsNullOrWhiteSpace(state.Label) ? "Build complete." : state.Label;
    }

    private RuntimeVersionInfo? GetTargetRuntime()
    {
        var mcVersion = _mcVersionComboBox.SelectedItem as string ?? "";
        if (string.IsNullOrWhiteSpace(mcVersion))
            return null;

        var loader = GetSelectedLoader();
        var loaderVersion = string.IsNullOrWhiteSpace(loader)
            ? ""
            : (_loaderVersionComboBox.SelectedItem as string ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(loader) && string.IsNullOrWhiteSpace(loaderVersion))
            return null;

        return new RuntimeVersionInfo(mcVersion.Trim(), loader.Trim(), loaderVersion);
    }

    private void UpdateTargetState()
    {
        var target = GetTargetRuntime();
        _targetRuntimeText.Text = target is null ? "Target: select a version" : $"Target: {target.Display}";

        var changed = target is not null
            && RuntimeVersionComparison.Compare(RuntimeVersionInfo.From(_instance), target).HasChange;

        _applyButton.IsEnabled = !_packLocked && !_applying && changed;
    }

    private string GetSelectedLoader()
    {
        if (_loaderComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return tag;
        return "";
    }

    private void SelectLoader(string loader)
    {
        foreach (var item in _loaderComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(tag, loader ?? "", StringComparison.OrdinalIgnoreCase))
            {
                _loaderComboBox.SelectedItem = item;
                return;
            }
        }

        _loaderComboBox.SelectedIndex = 0;
    }

    private void SetInputsEnabled(bool enabled)
    {
        enabled &= !_packLocked;
        _mcVersionComboBox.IsEnabled = enabled;
        _loaderComboBox.IsEnabled = enabled;
        _loaderVersionComboBox.IsEnabled = enabled;
        _applyButton.IsEnabled = enabled && (GetTargetRuntime() is { } target
            && RuntimeVersionComparison.Compare(RuntimeVersionInfo.From(_instance), target).HasChange);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_applying)
            Close(false);
    }

    private void DisposeMetaClients()
    {
        _mojangMeta.Dispose();
        _fabricMeta.Dispose();
        _forgeMeta.Dispose();
        _neoForgeMeta.Dispose();
        _quiltMeta.Dispose();
    }
}
