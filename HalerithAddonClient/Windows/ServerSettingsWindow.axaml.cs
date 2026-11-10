using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;
using System.Collections.ObjectModel;

#pragma warning disable CS8602

namespace HalerithAddonClient.Windows;

public partial class ServerSettingsWindow : Window
{
    private readonly HostConfig _managerScope;
    private readonly ServerConfig _server;
    private readonly ObservableCollection<IgnoreRuleRow> _ignoreRuleRows = [];
    private readonly ObservableCollection<PinnedItemRow> _pinnedItemRows = [];
    private readonly List<ManifestMod> _serverMods = [];
    private readonly List<ManifestResourcePack> _serverResourcePacks = [];
    private readonly List<ManifestShader> _serverShaders = [];

    public bool Saved { get; private set; }
    public bool BuildChanged { get; private set; }

    public ServerSettingsWindow(HostConfig managerScope, ServerConfig server)
    {
        _managerScope = managerScope;
        _server = server;

        InitializeComponent();
        Title = $"Settings - {DisplayName(server)}";
        AppWindowIcon.Apply(this);

        LoadPrimaryServerOptions();
        LoadFromServer();
        Opened += (_, _) => StartGuidedTips();
        _ = RefreshReadinessPanelAsync();
        _ = LoadServerItemsAsync();
    }

    private void LoadFromServer()
    {
        _modpackFilePolicy.ItemsSource = ModpackFilePolicies.Labels;
        _modpackFilePolicy.SelectedIndex = Array.IndexOf(ModpackFilePolicies.Values, ModpackFilePolicies.Normalize(_server.ModpackFilePolicy));
        _serverTitleLabel.Text = DisplayName(_server);
        _serverRuntimeLabel.Text = RuntimeDisplay(_server);
        _buildInfoLabel.Text = RuntimeDisplay(_server);

        _secondaryCheckBox.IsChecked = _server.IsSecondary;
        SelectPrimaryServer(_server.PrimaryServerPublicId);

        var javaMode = string.IsNullOrWhiteSpace(_server.JavaMode) ? JavaLaunchSettings.ModeAuto : _server.JavaMode;
        _javaModeAuto.IsChecked = string.Equals(javaMode, JavaLaunchSettings.ModeAuto, StringComparison.OrdinalIgnoreCase);
        _javaModeManual.IsChecked = string.Equals(javaMode, JavaLaunchSettings.ModeManual, StringComparison.OrdinalIgnoreCase);
        _javaModeCustom.IsChecked = string.Equals(javaMode, JavaLaunchSettings.ModeCustom, StringComparison.OrdinalIgnoreCase);
        SetJavaMaxMemorySliderValue(_server.JavaMaxMemoryMb);
        SetSelectedGarbageCollector(_server.JavaGarbageCollector);
        _javaCustomArgsBox.Text = string.IsNullOrWhiteSpace(_server.JavaCustomArgs)
            ? JavaLaunchSettings.DefaultCustomArgs
            : _server.JavaCustomArgs;

        _ignoreRuleRows.Clear();
        foreach (var rule in _server.IgnoreRules ?? [])
        {
            _ignoreRuleRows.Add(new IgnoreRuleRow(rule.ContentType, ContentTypes.ToDisplay(rule.ContentType), rule.Pattern));
        }

        _pinnedItemRows.Clear();
        foreach (var item in _server.PinnedItems ?? [])
        {
            _pinnedItemRows.Add(new PinnedItemRow(
                item.ContentType,
                ContentTypes.ToDisplay(item.ContentType),
                item.FileName,
                item.Slug,
                item.Slug));
        }

        _ignoreRulesGrid.ItemsSource = _ignoreRuleRows;
        _pinnedItemsGrid.ItemsSource = _pinnedItemRows;
        ResetIgnoreRuleRows();
        ResetPinnedItemRows();
        RefreshJavaModePanels();
        RefreshSecondaryMode();
    }

    private void SaveToServer()
    {
        _server.ModpackFilePolicy = ModpackFilePolicies.Values[Math.Clamp(_modpackFilePolicy.SelectedIndex, 0, 3)];
        _server.IsSecondary = _secondaryCheckBox.IsChecked == true;
        _server.PrimaryServerPublicId = _server.IsSecondary
            ? (_primaryServerComboBox.SelectedItem as ServerOption)?.ServerPublicId ?? ""
            : "";

        if (_server.IsSecondary)
        {
            _server.IgnoreRules = [];
            _server.PinnedItems = [];
            return;
        }

        _server.JavaMode = _javaModeManual.IsChecked == true ? JavaLaunchSettings.ModeManual
            : _javaModeCustom.IsChecked == true ? JavaLaunchSettings.ModeCustom
            : JavaLaunchSettings.ModeAuto;
        _server.JavaMinMemoryMb = 0;
        _server.JavaMaxMemoryMb = GetJavaMaxMemoryMb();
        _server.JavaGarbageCollector = GetSelectedGarbageCollector();
        _server.JavaExtraArgs = "";
        _server.JavaCustomArgs = string.IsNullOrWhiteSpace(_javaCustomArgsBox.Text)
            ? JavaLaunchSettings.DefaultCustomArgs
            : _javaCustomArgsBox.Text.Trim();
        _server.IgnoreRules = _ignoreRuleRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => new IgnoreRule { ContentType = r.ContentType, Pattern = r.Pattern.Trim() })
            .GroupBy(r => $"{r.ContentType}|{r.Pattern}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.ContentType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Pattern, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _server.PinnedItems = _pinnedItemRows
            .Where(r => !string.IsNullOrWhiteSpace(r.FileName) && !string.IsNullOrWhiteSpace(r.Slug))
            .Select(r => new LocalPinnedItem { ContentType = r.ContentType, FileName = r.FileName.Trim(), Slug = r.Slug.Trim() })
            .GroupBy(r => $"{r.ContentType}|{r.FileName}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.ContentType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LoadPrimaryServerOptions()
    {
        _primaryServerComboBox.Items.Clear();
        foreach (var server in _managerScope.VisibleServersFor(AccountStore.GetDefaultAccountId())
                     .Where(s => !s.IsSecondary
                         && !string.Equals(s.ServerPublicId, _server.ServerPublicId, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            _primaryServerComboBox.Items.Add(new ServerOption(server.ServerPublicId, DisplayName(server)));
        }
    }

    private void SelectPrimaryServer(string serverPublicId)
    {
        for (var i = 0; i < _primaryServerComboBox.Items.Count; i++)
        {
            if (_primaryServerComboBox.Items[i] is ServerOption option &&
                string.Equals(option.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase))
            {
                _primaryServerComboBox.SelectedIndex = i;
                return;
            }
        }

        _primaryServerComboBox.SelectedIndex = _primaryServerComboBox.Items.Count > 0 ? 0 : -1;
    }

    private void SecondaryCheckBox_Changed(object? sender, RoutedEventArgs e) => RefreshSecondaryMode();

    private void RefreshSecondaryMode()
    {
        var secondary = _secondaryCheckBox.IsChecked == true;
        _secondaryPanel.IsVisible = secondary;
        _primarySettingsPanel.IsVisible = !secondary;
        _buildTab.IsVisible = !secondary;
        _rulesTab.IsVisible = !secondary;

        if (secondary && _primaryServerComboBox.SelectedIndex < 0 && _primaryServerComboBox.Items.Count > 0)
            _primaryServerComboBox.SelectedIndex = 0;

        if (secondary && _tabControl.SelectedItem is TabItem item && (ReferenceEquals(item, _buildTab) || ReferenceEquals(item, _rulesTab)))
            _tabControl.SelectedIndex = 0;
    }

    private void JavaModeRadio_Click(object sender, RoutedEventArgs e) => RefreshJavaModePanels();

    private void RefreshJavaModePanels()
    {
        _javaAutoPanel.IsVisible = _javaModeAuto.IsChecked == true;
        _javaManualPanel.IsVisible = _javaModeManual.IsChecked == true;
        _javaCustomPanel.IsVisible = _javaModeCustom.IsChecked == true;

        if (_javaModeCustom.IsChecked == true && string.IsNullOrWhiteSpace(_javaCustomArgsBox.Text))
            _javaCustomArgsBox.Text = JavaLaunchSettings.DefaultCustomArgs;
    }

    private void JavaMaxMemorySlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => UpdateJavaMaxMemoryLabel();

    private void SetJavaMaxMemorySliderValue(int memoryMb)
    {
        var memoryGb = memoryMb > 0
            ? (int)Math.Round(memoryMb / 1024.0)
            : JavaLaunchSettings.DefaultMaxMemoryGb;

        memoryGb = Math.Clamp(
            memoryGb,
            JavaLaunchSettings.MinManualMaxMemoryGb,
            JavaLaunchSettings.MaxManualMaxMemoryGb);

        _javaMaxMemorySlider.Value = memoryGb;
        UpdateJavaMaxMemoryLabel();
    }

    private int GetJavaMaxMemoryMb()
    {
        var memoryGb = (int)Math.Round(_javaMaxMemorySlider.Value);
        memoryGb = Math.Clamp(
            memoryGb,
            JavaLaunchSettings.MinManualMaxMemoryGb,
            JavaLaunchSettings.MaxManualMaxMemoryGb);
        return memoryGb * 1024;
    }

    private void UpdateJavaMaxMemoryLabel()
        => _javaMaxMemoryValueLabel.Text = $"{(int)Math.Round(_javaMaxMemorySlider.Value)} GB";

    private void SetSelectedGarbageCollector(string? value)
    {
        var normalized = JavaLaunchSettings.NormalizeGarbageCollector(value);
        for (var i = 0; i < _javaGarbageCollectorComboBox.Items.Count; i++)
        {
            if (_javaGarbageCollectorComboBox.Items[i] is ComboBoxItem item
                && string.Equals(item.Content?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                _javaGarbageCollectorComboBox.SelectedIndex = i;
                return;
            }
        }

        _javaGarbageCollectorComboBox.SelectedIndex = 0;
    }

    private string GetSelectedGarbageCollector()
    {
        if (_javaGarbageCollectorComboBox.SelectedItem is ComboBoxItem item)
            return JavaLaunchSettings.NormalizeGarbageCollector(item.Content?.ToString());

        return JavaLaunchSettings.GarbageCollectorZgc;
    }

    private async Task RefreshReadinessPanelAsync()
    {
        var mcVersion = _server.MinecraftVersion;
        var loader = _server.Loader;
        var loaderVersion = _server.LoaderVersion;

        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            _readinessItemsControl.ItemsSource = null;
            _buildEnvButton.IsEnabled = false;
            _buildStatusLabel.Text = "No Minecraft version set.";
            return;
        }

        var activeBuild = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
        if (activeBuild is not null && !activeBuild.BuildTask.IsCompleted)
        {
            _buildEnvButton.IsEnabled = false;
            _buildProgressBar.IsVisible = true;
            _buildProgressBar.Maximum = Math.Max(activeBuild.Total, 1);
            _buildProgressBar.Value = activeBuild.Completed;
            _buildStatusLabel.Text = activeBuild.Label;
            _readinessItemsControl.ItemsSource = null;
            _ = PollBuildProgressAsync(mcVersion, loader, loaderVersion, activeBuild);
            return;
        }

        if (activeBuild is not null)
            ActiveBuildTracker.Remove(mcVersion, loader, loaderVersion);

        _buildStatusLabel.Text = "Checking...";
        _buildEnvButton.IsEnabled = false;

        try
        {
            var runtimeRoot = Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
            using var db = new RuntimeDatabase(runtimeRoot);
            var isRegistered = db.InstanceExists(mcVersion, loader, loaderVersion);

            using var builder = new GameEnvironmentBuilder(database: db);
            var report = await builder.CheckReadinessAsync(mcVersion, loader, loaderVersion);

            _readinessItemsControl.ItemsSource = report.Items.Select(i => new ReadinessItemVm
            {
                StatusIcon = i.Present ? "✓" : "✗",
                Label = $"[{i.Category}] {i.Label}",
            }).ToList();

            if (isRegistered && report.IsReady)
            {
                _buildStatusLabel.Text = "Ready";
                _buildEnvButton.Content = "Re-Build";
                _buildEnvButton.IsEnabled = true;
            }
            else if (!isRegistered && report.IsReady)
            {
                _buildStatusLabel.Text = "Not Built - files present";
                _buildEnvButton.Content = "Build";
                _buildEnvButton.IsEnabled = true;
            }
            else
            {
                var sizeMb = report.MissingBytes / (1024.0 * 1024.0);
                var suffix = report.MissingCount == 1 ? "item" : "items";
                var size = sizeMb > 1 ? $" (~{sizeMb:F0} MB)" : "";
                _buildStatusLabel.Text = isRegistered
                    ? $"Incomplete - {report.MissingCount} {suffix} missing{size}"
                    : $"Not Built - {report.MissingCount} {suffix} missing{size}";
                _buildEnvButton.Content = "Build";
                _buildEnvButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _buildStatusLabel.Text = $"Error: {ex.Message}";
            _buildEnvButton.IsEnabled = false;
        }
    }

    private async void BuildEnvironment_Click(object? sender, RoutedEventArgs e)
    {
        var mcVersion = _server.MinecraftVersion;
        var loader = _server.Loader;
        var loaderVersion = _server.LoaderVersion;

        if (string.IsNullOrWhiteSpace(mcVersion))
            return;

        var existing = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
        if (existing is not null && !existing.BuildTask.IsCompleted)
            return;

        var config = AppConfigStore.LoadAppConfig().ToRuntimeConfig(_managerScope, _server.ServerPublicId);
        var gate = await ManagedServerAccessGate.EnsureAsync(
            config, _server.ServerPublicId, ResolveSelectedMinecraftUuid());

        if (!gate.Allowed)
        {
            _footerStatusLabel.Text = string.IsNullOrWhiteSpace(gate.Message)
                ? "Server access could not be verified."
                : gate.Message;
            return;
        }

        _buildEnvButton.IsEnabled = false;
        _buildStatusLabel.Text = "Building...";
        _buildProgressBar.IsVisible = true;
        _buildProgressBar.Value = 0;

        var buildTask = Task.Run(() => ManageLocalInstancesWindow.RunBuildPipelineAsync(mcVersion, loader, loaderVersion));
        var state = ActiveBuildTracker.Register(mcVersion, loader, loaderVersion, buildTask);

        BuildChanged = true;
        _ = PollBuildProgressAsync(mcVersion, loader, loaderVersion, state);
    }

    private void OpenServerFolder_Click(object? sender, RoutedEventArgs e)
    {
        var instanceDirectory = Path.Combine(
            LauncherPaths.GetAppDataDirectory(),
            "Runtime",
            "instances",
            _server.ServerPublicId);

        if (FolderOpenService.TryOpenFolder(instanceDirectory, out var message))
            _footerStatusLabel.Text = "Opened server folder.";
        else
            _footerStatusLabel.Text = message;
    }

    private async Task PollBuildProgressAsync(string mcVersion, string loader, string loaderVersion, ActiveBuildTracker.BuildState state)
    {
        try
        {
            while (!state.BuildTask.IsCompleted)
            {
                await Task.Delay(250);
                _buildProgressBar.Maximum = Math.Max(state.Total, 1);
                _buildProgressBar.Value = state.Completed;
                _buildStatusLabel.Text = state.Label;
            }

            if (state.Error is not null)
            {
                _buildStatusLabel.Text = $"Build failed: {state.Error}";
                _footerStatusLabel.Text = $"Build failed: {state.Error}";
            }
            else
            {
                _buildProgressBar.Value = _buildProgressBar.Maximum;
                _buildStatusLabel.Text = "Complete";
                _footerStatusLabel.Text = "Game environment built successfully.";
            }

            ActiveBuildTracker.Remove(mcVersion, loader, loaderVersion);
            await RefreshReadinessPanelAsync();
        }
        catch
        {
            // Window was closed; the shared tracker keeps the build state.
        }
        finally
        {
            try
            {
                _buildEnvButton.IsEnabled = true;
                _buildProgressBar.IsVisible = false;
            }
            catch { }
        }
    }

    private string ResolveSelectedMinecraftUuid()
    {
        var selected = _server.SelectedAccountId;
        if (string.IsNullOrWhiteSpace(selected))
            selected = AccountStore.GetDefaultAccountId();

        return MinecraftProfileClient.NormalizeUuid(selected ?? "") ?? "";
    }

    private async void AddModIgnoreRule_Click(object? sender, RoutedEventArgs e)
        => await AddIgnoreRuleAsync(ContentTypes.Mod, GetContentFolder(ContentTypes.Mod), "*.jar");

    private async void AddPackIgnoreRule_Click(object? sender, RoutedEventArgs e)
        => await AddIgnoreRuleAsync(ContentTypes.ResourcePack, GetContentFolder(ContentTypes.ResourcePack), "*.zip");

    private async void AddShaderIgnoreRule_Click(object? sender, RoutedEventArgs e)
        => await AddIgnoreRuleAsync(ContentTypes.Shader, GetContentFolder(ContentTypes.Shader), "*.zip");

    private async Task AddIgnoreRuleAsync(string contentType, string folder, string searchPattern)
    {
        var typeName = ContentTypes.ToDisplay(contentType);
        if (!Directory.Exists(folder))
        {
            _footerStatusLabel.Text = $"{typeName} folder does not exist. Build or sync this server first.";
            return;
        }

        var files = Directory.GetFiles(folder, searchPattern, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            _footerStatusLabel.Text = $"No {searchPattern} files found in the {typeName} folder.";
            return;
        }

        var picker = new SelectionDialog($"Select File to Ignore ({typeName})", files!, multiSelect: true);
        var selected = await picker.ShowDialog<bool?>(this) == true ? picker.SelectedItems : [];
        if (selected.Count == 0)
            return;

        var added = 0;
        foreach (var file in selected)
        {
            if (_ignoreRuleRows.Any(row =>
                    string.Equals(row.ContentType, contentType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.Pattern, file, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _ignoreRuleRows.Add(new IgnoreRuleRow(contentType, typeName, file));
            added++;
        }

        if (added > 0)
        {
            ResetIgnoreRuleRows();
            _footerStatusLabel.Text = $"Added {added} ignore rule(s).";
        }
    }

    private void RemoveIgnoreRule_Click(object? sender, RoutedEventArgs e)
    {
        if (_ignoreRulesGrid.SelectedItem is IgnoreRuleRow row)
        {
            _ignoreRuleRows.Remove(row);
            _footerStatusLabel.Text = $"Removed ignore rule for {row.Pattern}.";
        }
    }

    private async void AddPinnedItem_Click(object? sender, RoutedEventArgs e) => await AddPinnedItemAsync();

    private void RemovePinnedItem_Click(object? sender, RoutedEventArgs e)
    {
        if (_pinnedItemsGrid.SelectedItem is PinnedItemRow row)
        {
            _pinnedItemRows.Remove(row);
            ResetPinnedItemRows();
            _footerStatusLabel.Text = $"Removed pinned mapping for {row.FileName}.";
        }
    }

    private async Task LoadServerItemsAsync()
    {
        _addMappingButton.IsEnabled = false;

        try
        {
            var config = AppConfigStore.LoadAppConfig();
            var host = config.Hosts.FirstOrDefault(h =>
                string.Equals(h.Id, _managerScope.Id, StringComparison.OrdinalIgnoreCase));
            var runtime = config.ToRuntimeConfig(host, _server.ServerPublicId);

            using var apiClient = new ApiClient(runtime);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var manifest = await apiClient.GetManifestAsync(new ManifestRequest
            {
                ClientVersion = typeof(ServerSettingsWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0"
            }, cts.Token);

            if (!manifest.Ok || !manifest.Authorized)
                throw new InvalidOperationException(manifest.Message ?? manifest.Error ?? "Manifest failed.");
            if (manifest.Modpack?.ForceConfig == true)
            {
                _modpackFilePolicy.IsEnabled = false;
                _modpackPolicyNotice.Text = "This server requires its pack files. Your saved preference will apply if the Manager disables forced configuration.";
            }

            _serverMods.Clear();
            _serverMods.AddRange(manifest.Mods.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
            _serverResourcePacks.Clear();
            _serverResourcePacks.AddRange(manifest.ResourcePacks.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
            _serverShaders.Clear();
            _serverShaders.AddRange(manifest.Shaders.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));

            RefreshPinnedItemNames();
            _addMappingButton.IsEnabled = true;
            _footerStatusLabel.Text = "Server items loaded.";
        }
        catch (Exception ex)
        {
            _footerStatusLabel.Text = $"Failed to load server items: {ex.Message}";
        }
    }

    private async Task AddPinnedItemAsync()
    {
        var typeChoices = ContentTypes.All.Select(ContentTypes.ToDisplay).ToList();
        var typePicker = new SelectionDialog("Select Content Type", typeChoices, multiSelect: false);
        if (await typePicker.ShowDialog<bool?>(this) != true || typePicker.SelectedItems.Count == 0)
            return;

        var selectedTypeDisplay = typePicker.SelectedItems[0];
        var contentType = ContentTypes.All.First(x => ContentTypes.ToDisplay(x) == selectedTypeDisplay);
        var searchPattern = contentType == ContentTypes.Mod ? "*.jar" : "*.zip";
        var folder = GetContentFolder(contentType);

        if (!Directory.Exists(folder))
        {
            _footerStatusLabel.Text = $"{selectedTypeDisplay} folder does not exist. Build or sync this server first.";
            return;
        }

        var localFiles = Directory.GetFiles(folder, searchPattern, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (localFiles.Count == 0)
        {
            _footerStatusLabel.Text = $"No {searchPattern} files found.";
            return;
        }

        var localPicker = new SelectionDialog($"Select Local File ({selectedTypeDisplay})", localFiles!, multiSelect: false);
        if (await localPicker.ShowDialog<bool?>(this) != true || localPicker.SelectedItems.Count == 0)
            return;

        var selectedFile = localPicker.SelectedItems[0];
        var serverItems = GetServerItemDisplays(contentType);
        if (serverItems.Count == 0)
        {
            _footerStatusLabel.Text = "Server items are not loaded yet.";
            return;
        }

        var serverPicker = new SelectionDialog($"Select Server {selectedTypeDisplay}", serverItems, multiSelect: false);
        if (await serverPicker.ShowDialog<bool?>(this) != true || serverPicker.SelectedItems.Count == 0)
            return;

        var selectedServerDisplay = serverPicker.SelectedItems[0];
        var (slug, serverName) = ResolveServerItem(contentType, selectedServerDisplay);
        if (string.IsNullOrWhiteSpace(slug))
            return;

        var existing = _pinnedItemRows.FirstOrDefault(row =>
            string.Equals(row.ContentType, contentType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.FileName, selectedFile, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Slug = slug;
            existing.ServerName = serverName;
        }
        else
        {
            _pinnedItemRows.Add(new PinnedItemRow(contentType, selectedTypeDisplay, selectedFile, slug, serverName));
        }

        ResetPinnedItemRows();
        _footerStatusLabel.Text = $"Pinned {selectedFile} to {serverName}.";
    }

    private List<string> GetServerItemDisplays(string contentType)
        => contentType switch
        {
            ContentTypes.Mod => _serverMods.Select(x => $"{x.Name} ({x.Slug})").ToList(),
            ContentTypes.ResourcePack => _serverResourcePacks.Select(x => $"{x.Name} ({x.Slug})").ToList(),
            ContentTypes.Shader => _serverShaders.Select(x => $"{x.Name} ({x.Slug})").ToList(),
            _ => [],
        };

    private (string Slug, string Name) ResolveServerItem(string contentType, string selectedDisplay)
    {
        switch (contentType)
        {
            case ContentTypes.Mod:
                var mod = _serverMods.FirstOrDefault(x => $"{x.Name} ({x.Slug})" == selectedDisplay);
                return mod is null ? ("", "") : (mod.Slug, mod.Name);
            case ContentTypes.ResourcePack:
                var pack = _serverResourcePacks.FirstOrDefault(x => $"{x.Name} ({x.Slug})" == selectedDisplay);
                return pack is null ? ("", "") : (pack.Slug, pack.Name);
            case ContentTypes.Shader:
                var shader = _serverShaders.FirstOrDefault(x => $"{x.Name} ({x.Slug})" == selectedDisplay);
                return shader is null ? ("", "") : (shader.Slug, shader.Name);
            default:
                return ("", "");
        }
    }

    private void RefreshPinnedItemNames()
    {
        foreach (var row in _pinnedItemRows)
        {
            row.ServerName = row.ContentType switch
            {
                ContentTypes.Mod => _serverMods.FirstOrDefault(x => string.Equals(x.Slug, row.Slug, StringComparison.OrdinalIgnoreCase))?.Name ?? row.Slug,
                ContentTypes.ResourcePack => _serverResourcePacks.FirstOrDefault(x => string.Equals(x.Slug, row.Slug, StringComparison.OrdinalIgnoreCase))?.Name ?? row.Slug,
                ContentTypes.Shader => _serverShaders.FirstOrDefault(x => string.Equals(x.Slug, row.Slug, StringComparison.OrdinalIgnoreCase))?.Name ?? row.Slug,
                _ => row.Slug
            };
        }

        ResetPinnedItemRows();
    }

    private void ResetIgnoreRuleRows()
    {
        var ordered = _ignoreRuleRows
            .OrderBy(x => x.ContentType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Pattern, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _ignoreRuleRows.Clear();
        foreach (var item in ordered)
            _ignoreRuleRows.Add(item);
    }

    private void ResetPinnedItemRows()
    {
        var ordered = _pinnedItemRows
            .OrderBy(x => x.ContentType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _pinnedItemRows.Clear();
        foreach (var item in ordered)
            _pinnedItemRows.Add(item);
    }

    private string GetContentFolder(string contentType)
    {
        var instanceDir = Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime", "instances", _server.ServerPublicId);
        return contentType switch
        {
            ContentTypes.Mod => Path.Combine(instanceDir, "mods"),
            ContentTypes.ResourcePack => Path.Combine(instanceDir, "resourcepacks"),
            ContentTypes.Shader => Path.Combine(instanceDir, "shaderpacks"),
            _ => instanceDir,
        };
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_secondaryCheckBox.IsChecked == true && _primaryServerComboBox.SelectedItem is not ServerOption)
        {
            _footerStatusLabel.Text = "Select a primary server for this secondary.";
            return;
        }

        SaveToServer();
        Saved = true;
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private string DisplayName(ServerConfig server)
        => DemoMode.ServerName(server, Math.Max(0, DemoMode.VisibleServers(_managerScope.Servers).FindIndex(s => s.ServerPublicId == server.ServerPublicId)));

    private static string RuntimeDisplay(ServerConfig server)
    {
        var mc = string.IsNullOrWhiteSpace(server.MinecraftVersion) ? "MC Unknown" : $"MC {server.MinecraftVersion}";
        var loader = string.IsNullOrWhiteSpace(server.Loader)
            ? "Vanilla"
            : string.IsNullOrWhiteSpace(server.LoaderVersion)
                ? server.Loader
                : $"{server.Loader} {server.LoaderVersion}";
        return $"{mc} - {loader}";
    }

    private sealed record ServerOption(string ServerPublicId, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record IgnoreRuleRow(string ContentType, string TypeDisplay, string Pattern);

    private sealed class PinnedItemRow
    {
        public PinnedItemRow(string contentType, string typeDisplay, string fileName, string slug, string serverName)
        {
            ContentType = contentType;
            TypeDisplay = typeDisplay;
            FileName = fileName;
            Slug = slug;
            ServerName = serverName;
        }

        public string ContentType { get; }
        public string TypeDisplay { get; }
        public string FileName { get; }
        public string Slug { get; set; }
        public string ServerName { get; set; }
    }

    public sealed class ReadinessItemVm
    {
        public string StatusIcon { get; set; } = "";
        public string Label { get; set; } = "";
    }
}
