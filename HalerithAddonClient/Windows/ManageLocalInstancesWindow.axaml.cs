using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;

namespace HalerithAddonClient.Windows;

/// <summary>
/// Window that lists all local instances and allows the user to Add, Remove,
/// and Build them.  Opened from the "Manage Local" button at the bottom of
/// the main window's server sidebar.
///
/// Changes are written directly to <see cref="AppConfig.LocalInstances"/>
/// and saved to app storage via <see cref="AppConfigStore"/>.
/// The caller should refresh the sidebar after this window closes.
/// </summary>
public partial class ManageLocalInstancesWindow : Window
{
    private readonly AppConfig _appConfig;
    private List<InstanceRow> _rows = [];

    /// <summary>True if any instances were added/removed/built (caller should refresh).</summary>
    public bool InstancesChanged { get; private set; }

    public ManageLocalInstancesWindow(AppConfig appConfig)
    {
        _appConfig = appConfig;
        InitializeComponent();
        RefreshGrid();
        Loaded += async (_, _) => await CheckAllBuildStatusAsync();
    }

    // ── Grid binding ────────────────────────────────────────────────────

    private void RefreshGrid()
    {
        _rows = _appConfig.LocalInstances
            .Select(i => new InstanceRow(i))
            .ToList();
        _instancesGrid.ItemsSource = null;
        _instancesGrid.ItemsSource = _rows;
    }

    /// <summary>
    /// Checks build readiness for every instance in the grid on load.
    /// </summary>
    private async Task CheckAllBuildStatusAsync()
    {
        foreach (var row in _rows)
        {
            // Check for an active build first.
            var active = ActiveBuildTracker.Get(
                row.Instance.MinecraftVersion,
                row.Instance.Loader,
                row.Instance.LoaderVersion);

            if (active is not null && !active.BuildTask.IsCompleted)
            {
                row.BuildStatusText = "Building…";
                continue;
            }

            try
            {
                var runtimeRoot = System.IO.Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
                using var db = new RuntimeDatabase(runtimeRoot);
                if (db.InstanceExists(row.Instance.MinecraftVersion, row.Instance.Loader, row.Instance.LoaderVersion))
                {
                    row.BuildStatusText = "Ready";
                    continue;
                }

                using var builder = new GameEnvironmentBuilder(database: db);
                var report = await builder.CheckReadinessAsync(
                    row.Instance.MinecraftVersion,
                    row.Instance.Loader,
                    row.Instance.LoaderVersion);

                if (report.IsReady)
                    await builder.RecordInstanceAsync(report);

                row.BuildStatusText = report.IsReady ? "Ready" : "Not Built";
            }
            catch
            {
                row.BuildStatusText = "Not Built";
            }
        }
    }

    // ── Add ─────────────────────────────────────────────────────────────

    private async void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AddLocalInstanceWindow();
        var result = await dialog.ShowDialog<LocalInstance?>(this);

        if (result is null) return;

        _appConfig.LocalInstances.Add(result);
        AppConfigStore.SaveAppConfig(_appConfig);
        InstancesChanged = true;
        RefreshGrid();
        await CheckAllBuildStatusAsync();
    }

    // ── Remove ──────────────────────────────────────────────────────────

    private void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_instancesGrid.SelectedItem is not InstanceRow selected) return;

        _appConfig.LocalInstances.Remove(selected.Instance);
        AppConfigStore.SaveAppConfig(_appConfig);
        InstancesChanged = true;
        RefreshGrid();
    }

    // ── Build ───────────────────────────────────────────────────────────

    private async void BuildButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_instancesGrid.SelectedItem is not InstanceRow selected) return;

        var inst      = selected.Instance;
        var mcVersion = inst.MinecraftVersion;
        var loader    = inst.Loader;
        var loaderVer = inst.LoaderVersion;

        if (string.IsNullOrWhiteSpace(mcVersion)) return;

        // If a build is already running for this environment, do nothing.
        var existing = ActiveBuildTracker.Get(mcVersion, loader, loaderVer);
        if (existing is not null && !existing.BuildTask.IsCompleted)
            return;

        // Launch the build as a background task (survives this window closing).
        var buildTask = Task.Run(() => RunBuildPipelineAsync(mcVersion, loader, loaderVer));
        var state = ActiveBuildTracker.Register(mcVersion, loader, loaderVer, buildTask);

        InstancesChanged = true;

        // Update the row status and show progress bar.
        selected.BuildStatusText = "Building…";
        _buildButton.IsEnabled = false;
        _buildProgressPanel.IsVisible = true;
        _buildStatusLabel.Text = $"Building {inst.Name}…";
        _buildPercentLabel.Text = "";
        _buildProgressBar.Value = 0;

        _ = PollBuildProgressAsync(selected, mcVersion, loader, loaderVer, state);
    }

    private async Task PollBuildProgressAsync(
        InstanceRow row,
        string mcVersion, string loader, string loaderVer,
        ActiveBuildTracker.BuildState state)
    {
        try
        {
            while (!state.BuildTask.IsCompleted)
            {
                await Task.Delay(300);

                var total = Math.Max(state.Total, 1);
                var pct = (int)(state.Completed / (double)total * 100);
                _buildProgressBar.Maximum = total;
                _buildProgressBar.Value = state.Completed;
                _buildStatusLabel.Text = $"Building {row.Instance.Name}… {state.Label}";
                _buildPercentLabel.Text = $"{pct}%";
                row.BuildStatusText = $"Building… {pct}%";
            }

            // Final state.
            if (state.Error is not null)
            {
                _buildStatusLabel.Text = $"Build failed: {state.Error}";
                _buildPercentLabel.Text = "";
                row.BuildStatusText = "Failed";
            }
            else
            {
                _buildProgressBar.Value = _buildProgressBar.Maximum;
                _buildStatusLabel.Text = $"{row.Instance.Name} — Build complete";
                _buildPercentLabel.Text = "100%";
                row.BuildStatusText = "Ready";
            }

            ActiveBuildTracker.Remove(mcVersion, loader, loaderVer);
        }
        catch
        {
            // Window closed — build continues in background via tracker.
        }
        finally
        {
            _buildButton.IsEnabled = true;
        }
    }

    // ── Close ───────────────────────────────────────────────────────────

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    // ── Build pipeline (static, background) ─────────────────────────────

    /// <summary>
    /// The actual build pipeline. Runs entirely on background threads.
    /// Reuses the same validation pattern as the settings dialogs.
    /// Internal so MainWindow can also trigger builds.
    /// </summary>
    internal static async Task RunBuildPipelineAsync(string mcVersion, string loader, string loaderVer)
    {
        var state = ActiveBuildTracker.Get(mcVersion, loader, loaderVer);
        if (state is null) return;

        try
        {
            var runtimeRoot = System.IO.Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
            using var db = new RuntimeDatabase(runtimeRoot);
            using var builder = new GameEnvironmentBuilder(database: db);

            var report = await builder.CheckReadinessAsync(mcVersion, loader, loaderVer);
            if (report.IsReady)
            {
                await builder.RecordInstanceAsync(report);
                state.Label = "Already ready";
                return;
            }

            state.Total = report.MissingCount;

            var progress = new Progress<(int completed, int total, string label)>(p =>
            {
                state.Completed = p.completed;
                state.Total = p.total;
                state.Label = $"{p.completed}/{p.total}";
            });

            await builder.BuildAsync(report, progress);

            state.Label = "Recording instance…";
            var finalReport = await builder.CheckReadinessAsync(mcVersion, loader, loaderVer);
            await builder.RecordInstanceAsync(finalReport);

            state.Completed = state.Total;
            state.Label = "Complete";
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Label = $"Build failed: {ex.Message}";
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Row wrapper — binds LocalInstance + live build status to the grid
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Wraps a <see cref="LocalInstance"/> with a bindable build status string
    /// so the DataGrid can show a live "Build" column.
    /// </summary>
    internal sealed class InstanceRow : INotifyPropertyChanged
    {
        private string _buildStatusText = "Checking…";

        public InstanceRow(LocalInstance instance) => Instance = instance;

        public LocalInstance Instance { get; }

        public string BuildStatusText
        {
            get => _buildStatusText;
            set
            {
                if (_buildStatusText == value) return;
                _buildStatusText = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
