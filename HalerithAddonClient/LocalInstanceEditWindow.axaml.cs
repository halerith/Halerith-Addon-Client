using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;
using HalerithAddonClient.Windows;

// Named controls are assigned by InitializeComponent() and are guaranteed
// non-null after construction — suppress nullable dereference noise.
#pragma warning disable CS8602

namespace HalerithAddonClient;

/// <summary>
/// Modal dialog for editing a single <see cref="LocalInstance"/>.
/// Tab 0 = Settings (Java/JVM). Tab 1 = Build (readiness check + build).
/// </summary>
public partial class LocalInstanceEditWindow : Window
{
    private readonly LocalInstance _instance;

    /// <summary>True if the user clicked Save (dialog result = true).</summary>
    public bool Saved { get; private set; }
    public bool Deleted { get; private set; }
    private bool _deleting;

    private async void DeleteInstance_Click(object? sender, RoutedEventArgs e)
    {
        if (_deleting) return;
        if (ActiveBuildTracker.Get(_instance.MinecraftVersion, _instance.Loader, _instance.LoaderVersion) is { BuildTask.IsCompleted: false })
        {
            await new MessageBoxWindow("Delete instance", "Wait for the build to finish before deleting this instance.", MessageBoxButton.OK, MessageBoxImage.Warning).ShowDialog(this);
            return;
        }
        if (!await new MessageBoxWindow("Delete instance", $"Permanently delete '{_instance.Name}' and all its local files, including worlds, mods, configuration and pack backups?\nOther instances and shared Minecraft runtimes will be kept. This cannot be undone.", MessageBoxButton.YesNo, MessageBoxImage.Warning).ShowDialog(this)) return;
        _deleting = true;
        _deleteInstanceButton.IsEnabled = _saveButton.IsEnabled = _buildEnvButton.IsEnabled = false;
        try
        {
            await Task.Run(() => LocalInstanceRemoval.Delete(_instance));
            Deleted = true; Close(false);
        }
        catch (Exception error)
        {
            await new MessageBoxWindow("Instance not deleted", "Could not finish deletion. The instance remains listed so you can retry.\n" + error.Message, MessageBoxButton.OK, MessageBoxImage.Error).ShowDialog(this);
        }
        finally
        {
            _deleting = false;
            _deleteInstanceButton.IsEnabled = _saveButton.IsEnabled = true;
        }
    }

    /// <summary>True if a build was started or completed while the window was open.</summary>
    public bool BuildChanged { get; private set; }

    public LocalInstanceEditWindow(LocalInstance instance)
    {
        _instance = instance;
        InitializeComponent();
        Closing += (_, e) => { if (_deleting && !Deleted) e.Cancel = true; };

        Title = $"Edit — {instance.Name}";

        AppWindowIcon.Apply(this);

        LoadFromInstance();
        Opened += (_, _) => StartGuidedTips();
        _ = RefreshReadinessPanelAsync();
    }

    // ══════════════════════════════════════════════════════════════════
    //  Load / Save
    // ══════════════════════════════════════════════════════════════════

    private void LoadFromInstance()
    {
        // Instance info
        _instanceInfoLabel.Text = _instance.VersionSummary;

        // Server address
        _serverAddressBox.Text = _instance.ServerAddress ?? "";

        // Java settings
        var javaMode = _instance.JavaMode ?? "Auto";
        _javaModeAuto.IsChecked   = string.Equals(javaMode, "Auto",   StringComparison.OrdinalIgnoreCase);
        _javaModeManual.IsChecked = string.Equals(javaMode, "Manual", StringComparison.OrdinalIgnoreCase);
        _javaModeCustom.IsChecked = string.Equals(javaMode, "Custom", StringComparison.OrdinalIgnoreCase);
        SetJavaMaxMemorySliderValue(_instance.JavaMaxMemoryMb);
        SetSelectedGarbageCollector(_instance.JavaGarbageCollector);
        _javaCustomArgsBox.Text = string.IsNullOrWhiteSpace(_instance.JavaCustomArgs)
            ? JavaLaunchSettings.DefaultCustomArgs
            : _instance.JavaCustomArgs;
        RefreshJavaModePanels();
    }

    private void SaveToInstance()
    {
        // Server address
        _instance.ServerAddress = (_serverAddressBox.Text ?? "").Trim();

        // Java settings
        _instance.JavaMode = _javaModeManual.IsChecked == true ? "Manual"
                           : _javaModeCustom.IsChecked == true ? "Custom"
                           : "Auto";
        _instance.JavaMinMemoryMb = 0;
        _instance.JavaMaxMemoryMb = GetJavaMaxMemoryMb();
        _instance.JavaGarbageCollector = GetSelectedGarbageCollector();
        _instance.JavaExtraArgs = "";
        _instance.JavaCustomArgs = string.IsNullOrWhiteSpace(_javaCustomArgsBox.Text)
            ? JavaLaunchSettings.DefaultCustomArgs
            : _javaCustomArgsBox.Text.Trim();
    }

    // ══════════════════════════════════════════════════════════════════
    //  Java mode radio buttons
    // ══════════════════════════════════════════════════════════════════

    private void JavaModeRadio_Click(object sender, RoutedEventArgs e) => RefreshJavaModePanels();

    private void RefreshJavaModePanels()
    {
        _javaAutoPanel.IsVisible   = _javaModeAuto.IsChecked == true;
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

    // ══════════════════════════════════════════════════════════════════
    //  Instance tab — readiness + build
    // ══════════════════════════════════════════════════════════════════

    private ReadinessReport? _lastReadinessReport;

    private async Task RefreshReadinessPanelAsync()
    {
        var mcVersion = _instance.MinecraftVersion;
        var loader = _instance.Loader;
        var loaderVersion = _instance.LoaderVersion;

        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            _readinessItemsControl.ItemsSource = null;
            _buildEnvButton.IsEnabled = false;
            _buildStatusLabel.Text = "No Minecraft version set.";
            return;
        }

        // Check for an active background build
        var activeBuild = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
        if (activeBuild is not null && !activeBuild.BuildTask.IsCompleted)
        {
            _buildEnvButton.IsEnabled = false;
            _buildProgressBar.IsVisible = true;
            _buildProgressBar.Maximum = Math.Max(activeBuild.Total, 1);
            _buildProgressBar.Value = activeBuild.Completed;
            _buildStatusLabel.Text = activeBuild.Label;
            _buildStatusLabel.Foreground = AppTheme.TextMuted;
            _buildStatusLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            _readinessItemsControl.ItemsSource = null;

            _ = PollBuildProgressAsync(mcVersion, loader, loaderVersion, activeBuild);
            return;
        }

        if (activeBuild is not null)
            ActiveBuildTracker.Remove(mcVersion, loader, loaderVersion);

        _buildStatusLabel.Text = "Checking…";
        _buildEnvButton.IsEnabled = false;

        try
        {
            var runtimeRoot = System.IO.Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
            using var db = new RuntimeDatabase(runtimeRoot);
            var isRegistered = db.InstanceExists(mcVersion, loader, loaderVersion);

            using var builder = new GameEnvironmentBuilder(database: db);
            var report = await builder.CheckReadinessAsync(mcVersion, loader, loaderVersion);
            _lastReadinessReport = report;

            var items = report.Items.Select(i => new ReadinessItemVm
            {
                StatusIcon = i.Present ? "✓" : "✗",
                Label = $"[{i.Category}] {i.Label}",
            }).ToList();

            _readinessItemsControl.ItemsSource = items;

            if (isRegistered && report.IsReady)
            {
                _buildStatusLabel.Text = "Ready";
                _buildStatusLabel.Foreground = AppTheme.StatusOk;
                _buildEnvButton.Content = "Re-Build";
                _buildEnvButton.IsEnabled = true;
            }
            else if (!isRegistered && report.IsReady)
            {
                _buildStatusLabel.Text = "Not Built - files present";
                _buildStatusLabel.Foreground = AppTheme.StatusWarning;
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
                _buildStatusLabel.Foreground = AppTheme.StatusError;
                _buildEnvButton.Content = "Build";
                _buildEnvButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _buildStatusLabel.Text = $"Error: {ex.Message}";
            _buildStatusLabel.Foreground = AppTheme.StatusError;
            _buildEnvButton.IsEnabled = false;
        }
    }

    private async void BuildEnvironment_Click(object s, RoutedEventArgs e)
    {
        var mcVersion = _instance.MinecraftVersion;
        var loader = _instance.Loader;
        var loaderVer = _instance.LoaderVersion;

        if (string.IsNullOrWhiteSpace(mcVersion)) return;

        var existing = ActiveBuildTracker.Get(mcVersion, loader, loaderVer);
        if (existing is not null && !existing.BuildTask.IsCompleted)
            return;

        // Clean up any partial Forge/NeoForge install so we start fresh.
        var loaderLower = loader.ToLowerInvariant();
        if (loaderLower is "forge" or "neoforge" && !string.IsNullOrWhiteSpace(loaderVer))
        {
            try
            {
                using var installer = new ForgeInstallerService();
                if (installer.IsPartialInstall(loaderLower, loaderVer))
                {
                    installer.CleanInstall(loaderLower, loaderVer);
                    FileLogger.Info($"[Build] Cleaned partial {loader} install for {loaderVer}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"[Build] Failed to clean partial install: {ex.Message}");
            }
        }

        _buildEnvButton.IsEnabled = false;
        _buildStatusLabel.Text = "Building…";
        _buildStatusLabel.Foreground = AppTheme.TextMuted;
        _buildStatusLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        _buildProgressBar.IsVisible = true;
        _buildProgressBar.Value = 0;

        var buildTask = Task.Run(() => ManageLocalInstancesWindow.RunBuildPipelineAsync(mcVersion, loader, loaderVer));
        var state = ActiveBuildTracker.Register(mcVersion, loader, loaderVer, buildTask);

        BuildChanged = true;

        _ = PollBuildProgressAsync(mcVersion, loader, loaderVer, state);
    }

    private void OpenInstanceFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (FolderOpenService.TryOpenFolder(_instance.GetInstanceDirectory(), out var message))
            _footerStatusLabel.Text = "Opened instance folder.";
        else
            _footerStatusLabel.Text = message;
    }

    private async Task PollBuildProgressAsync(string mcVersion, string loader, string loaderVer,
        ActiveBuildTracker.BuildState state)
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
                _buildStatusLabel.Foreground = AppTheme.StatusError;
                _footerStatusLabel.Text = $"Build failed: {state.Error}";
            }
            else
            {
                _buildProgressBar.Value = _buildProgressBar.Maximum;
                _buildStatusLabel.Text = "Complete";
                _footerStatusLabel.Text = "Game environment built successfully.";
            }

            ActiveBuildTracker.Remove(mcVersion, loader, loaderVer);
            await RefreshReadinessPanelAsync();
        }
        catch
        {
            // Window was closed — build continues via tracker.
        }
        finally
        {
            try
            {
                _buildEnvButton.IsEnabled = true;
                _buildProgressBar.IsVisible = false;
                _buildStatusLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  Footer buttons
    // ══════════════════════════════════════════════════════════════════

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveToInstance();
        Saved = true;
        Close(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close(false);
    }

    // ══════════════════════════════════════════════════════════════════
    //  View models
    // ══════════════════════════════════════════════════════════════════

    public sealed class ReadinessItemVm
    {
        public string StatusIcon { get; set; } = "";
        public string Label { get; set; } = "";
    }
}
