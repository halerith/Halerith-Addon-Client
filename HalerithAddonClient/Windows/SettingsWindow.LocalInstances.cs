using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using HalerithAddonClient;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Windows;

public partial class SettingsWindow
{
    // ══════════════════════════════════════════════════════════════════════
    //  Environments tab
    // ══════════════════════════════════════════════════════════════════════

    private readonly ObservableCollection<InstanceRow> _instanceRows = [];
    private bool _instancesLoaded;

    private void InvalidateInstancesTab()
    {
        if (!_instancesLoaded)
            return;

        _instancesLoaded = false;
        if (_tabControl?.SelectedIndex == InstancesTabIndex)
            LoadInstances();
    }

    /// <summary>
    /// Populates the Environments DataGrid from the RuntimeDatabase.
    /// Called lazily when the Environments tab is first selected.
    /// </summary>
    private void LoadInstances()
    {
        _instanceRows.Clear();
        _instancesGrid.ItemsSource = _instanceRows;

        var runtimeRoot = Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");

        if (!Directory.Exists(runtimeRoot))
        {
            _instanceStatusLabel.Text = "No instances found — Runtime folder is empty.";
            return;
        }

        try
        {
            using var db = new RuntimeDatabase(runtimeRoot);
            var instances = db.GetAllInstances();
            var activeBuilds = ActiveBuildTracker.Snapshot()
                .Where(b => !b.IsCompleted)
                .ToList();

            foreach (var inst in instances)
            {
                var inUseReason = GetRuntimeInUseReason(
                    inst.MinecraftVersion,
                    inst.Loader,
                    inst.LoaderVersion,
                    activeBuilds);

                // Calculate size: DB-tracked shared files + version-specific directory
                var versionDirSize = GetDirectorySize(Path.Combine(runtimeRoot, "versions", inst.MinecraftVersion));
                var loaderDirSize = 0L;
                if (string.Equals(inst.Loader, "forge", StringComparison.OrdinalIgnoreCase))
                    loaderDirSize = GetDirectorySize(Path.Combine(runtimeRoot, "forge", inst.LoaderVersion));
                else if (string.Equals(inst.Loader, "neoforge", StringComparison.OrdinalIgnoreCase))
                    loaderDirSize = GetDirectorySize(Path.Combine(runtimeRoot, "neoforge", inst.LoaderVersion));

                _instanceRows.Add(new InstanceRow
                {
                    DbId = inst.Id,
                    MinecraftVersion = inst.MinecraftVersion,
                    Loader = inst.Loader,
                    LoaderVersion = inst.LoaderVersion,
                    SizeBytes = inst.TotalSizeBytes + versionDirSize + loaderDirSize,
                    InUse = !string.IsNullOrWhiteSpace(inUseReason),
                    InUseReason = inUseReason,
                    RuntimeRoot = runtimeRoot,
                    FileCount = inst.FileCount,
                });
            }

            // Scan for incomplete Forge/NeoForge installs (cache dir exists, no .installed marker)
            foreach (var loaderName in new[] { "forge", "neoforge" })
            {
                var loaderDir = Path.Combine(runtimeRoot, loaderName);
                if (!Directory.Exists(loaderDir)) continue;

                foreach (var versionDir in Directory.GetDirectories(loaderDir))
                {
                    if (File.Exists(Path.Combine(versionDir, ".installed")))
                        continue; // completed install — already in DB

                    var loaderVersion = Path.GetFileName(versionDir);

                    // Skip if a completed DB row already covers this loader version
                    if (_instanceRows.Any(r =>
                        string.Equals(r.Loader, loaderName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.LoaderVersion, loaderVersion, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Extract MC version from Forge version string (e.g. "1.21.8-58.1.18" → "1.21.8")
                    var mcVersion = loaderVersion.Contains('-')
                        ? loaderVersion[..loaderVersion.IndexOf('-')]
                        : loaderVersion;

                    var inUseReason = activeBuilds.Any(b =>
                        string.Equals(b.MinecraftVersion, mcVersion, StringComparison.OrdinalIgnoreCase))
                            ? "Build running"
                            : "";

                    _instanceRows.Add(new InstanceRow
                    {
                        DbId = -1,
                        MinecraftVersion = mcVersion,
                        Loader = loaderName,
                        LoaderVersion = loaderVersion,
                        SizeBytes = GetDirectorySize(versionDir),
                        InUse = !string.IsNullOrWhiteSpace(inUseReason),
                        InUseReason = inUseReason,
                        IsIncomplete = true,
                        IncompletePath = versionDir,
                        RuntimeRoot = runtimeRoot,
                        FileCount = 0,
                    });
                }
            }

            _instanceStatusLabel.Text = _instanceRows.Count == 0
                ? "No instances found. Build an environment in a server's Instance tab."
                : $"{_instanceRows.Count} instance(s) — select one for details.";
        }
        catch (Exception ex)
        {
            _instanceStatusLabel.Text = $"Error loading instances: {ex.Message}";
        }

        _instancesLoaded = true;
    }

    private void InstancesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var row = _instancesGrid.SelectedItem as InstanceRow;
        _deleteInstanceButton.IsEnabled = row is not null && !row.InUse;

        if (row is not null)
        {
            _instanceStatusLabel.Text = row.IsIncomplete
                ? row.InUse
                    ? $"{row.LoaderDisplay} {row.MinecraftVersion} — incomplete build in use ({row.InUseReason})"
                    : $"{row.LoaderDisplay} {row.MinecraftVersion} — incomplete build (can delete)"
                : row.InUse
                    ? $"{row.LoaderDisplay} {row.MinecraftVersion} — in use ({row.InUseReason})"
                    : $"{row.LoaderDisplay} {row.MinecraftVersion} — {row.FileCount:N0} tracked files";
        }
    }

    private async void DeleteInstanceButton_Click(object? sender, RoutedEventArgs e)
    {
        var row = _instancesGrid.SelectedItem as InstanceRow;
        if (row is null || row.InUse) return;

        var currentUse = GetRuntimeInUseReason(
            row.MinecraftVersion,
            row.Loader,
            row.LoaderVersion,
            ActiveBuildTracker.Snapshot().Where(b => !b.IsCompleted).ToList());
        if (!string.IsNullOrWhiteSpace(currentUse))
        {
            _instanceStatusLabel.Text = $"{row.LoaderDisplay} {row.MinecraftVersion} — in use ({currentUse})";
            _deleteInstanceButton.IsEnabled = false;
            return;
        }

        // Incomplete builds: just delete the cache directory
        if (row.IsIncomplete)
        {
            await DeleteIncompleteInstanceAsync(row);
            return;
        }

        // Confirm deletion
        var confirmDialog = new Window
        {
            Title = "Confirm Delete",
            Width = 440, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            CanResize = false,
        };

        var confirmed = false;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Delete the {row.LoaderDisplay} {row.MinecraftVersion} instance?\n\nThis removes version files, loader caches, and any shared libraries or assets no longer used by other instances.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = AppTheme.TextSecondary,
            FontSize = 13,
        });

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var deleteBtn = new Button { Content = "Delete", MinWidth = 81 };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 81, Margin = new Thickness(8, 0, 0, 0) };

        deleteBtn.Click += (_, _) => { confirmed = true; confirmDialog.Close(); };
        cancelBtn.Click += (_, _) => { confirmDialog.Close(); };

        buttonPanel.Children.Add(deleteBtn);
        buttonPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonPanel);
        confirmDialog.Content = panel;

        await confirmDialog.ShowDialog(this);
        if (!confirmed) return;

        _deleteInstanceButton.IsEnabled = false;
        _instanceStatusLabel.Text = "Deleting…";

        try
        {
            using var db = new RuntimeDatabase(row.RuntimeRoot);

            // 1. Delete from DB and get orphaned shared files
            var orphanedFiles = db.DeleteInstance(row.DbId);

            // 2. Delete orphaned shared files from disk (libraries, assets, java)
            int deletedFiles = 0;
            foreach (var filePath in orphanedFiles)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        deletedFiles++;

                        // Clean up empty parent directories
                        var dir = Path.GetDirectoryName(filePath);
                        while (dir is not null
                            && Directory.Exists(dir)
                            && !Directory.EnumerateFileSystemEntries(dir).Any()
                            && !string.Equals(dir, row.RuntimeRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.Delete(dir);
                            dir = Path.GetDirectoryName(dir);
                        }
                    }
                }
                catch { /* skip locked files */ }
            }

            // 3. Delete version-specific directory
            var versionDir = Path.Combine(row.RuntimeRoot, "versions", row.MinecraftVersion);
            if (!IsMcVersionInUse(row.MinecraftVersion, row.RuntimeRoot,
                    excludeLoader: row.Loader, excludeLoaderVersion: row.LoaderVersion)
                && Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, recursive: true);
            }

            // 4. Delete Forge/NeoForge installer cache
            if (string.Equals(row.Loader, "forge", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(row.LoaderVersion)
                && !IsLoaderVersionInUse(row.Loader, row.LoaderVersion, row.RuntimeRoot,
                    excludeMcVersion: row.MinecraftVersion))
            {
                var forgeDir = Path.Combine(row.RuntimeRoot, "forge", row.LoaderVersion);
                if (Directory.Exists(forgeDir))
                    Directory.Delete(forgeDir, recursive: true);
            }
            else if (string.Equals(row.Loader, "neoforge", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(row.LoaderVersion)
                     && !IsLoaderVersionInUse(row.Loader, row.LoaderVersion, row.RuntimeRoot,
                         excludeMcVersion: row.MinecraftVersion))
            {
                var neoDir = Path.Combine(row.RuntimeRoot, "neoforge", row.LoaderVersion);
                if (Directory.Exists(neoDir))
                    Directory.Delete(neoDir, recursive: true);
            }

            SetSuccessStatus($"Deleted {row.LoaderDisplay} {row.MinecraftVersion} — removed {deletedFiles:N0} orphaned files.");

            // Reload the list
            _instancesLoaded = false;
            LoadInstances();
        }
        catch (Exception ex)
        {
            SetErrorStatus($"Failed to delete instance: {ex.Message}");
        }
    }

    private async Task DeleteIncompleteInstanceAsync(InstanceRow row)
    {
        // Quick confirmation dialog
        var confirmDialog = new Window
        {
            Title = "Delete Incomplete Build",
            Width = 440, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            CanResize = false,
        };

        var confirmed = false;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Delete the incomplete {row.LoaderDisplay} {row.MinecraftVersion} build?\n\nThis removes the installer cache and any unused version files.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = AppTheme.TextSecondary,
            FontSize = 13,
        });

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var deleteBtn = new Button { Content = "Delete", MinWidth = 81 };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 81, Margin = new Thickness(8, 0, 0, 0) };

        deleteBtn.Click += (_, _) => { confirmed = true; confirmDialog.Close(); };
        cancelBtn.Click += (_, _) => { confirmDialog.Close(); };

        buttonPanel.Children.Add(deleteBtn);
        buttonPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonPanel);
        confirmDialog.Content = panel;

        await confirmDialog.ShowDialog(this);
        if (!confirmed) return;

        try
        {
            // 1. Delete the loader cache directory
            if (Directory.Exists(row.IncompletePath))
                Directory.Delete(row.IncompletePath, recursive: true);

            // 2. Check whether any other instance (completed, incomplete, or configured)
            //    uses the same MC version — if not, delete the versions/ directory too.
            var mcVersion = row.MinecraftVersion;
            var runtimeRoot = row.RuntimeRoot;

            var mcVersionInUse = IsMcVersionInUse(mcVersion, runtimeRoot,
                excludeLoader: row.Loader, excludeLoaderVersion: row.LoaderVersion);

            if (!mcVersionInUse)
            {
                var versionDir = Path.Combine(runtimeRoot, "versions", mcVersion);
                if (Directory.Exists(versionDir))
                    Directory.Delete(versionDir, recursive: true);
            }

            SetSuccessStatus($"Deleted incomplete {row.LoaderDisplay} {row.MinecraftVersion} build.");
            _instancesLoaded = false;
            LoadInstances();
        }
        catch (Exception ex)
        {
            SetErrorStatus($"Failed to delete: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if any completed instance (DB), incomplete build on disk,
    /// or configured server/local instance uses the given MC version —
    /// excluding a specific loader+version being deleted.
    /// </summary>
    private bool IsMcVersionInUse(string mcVersion, string runtimeRoot,
        string excludeLoader, string excludeLoaderVersion)
    {
        // Check completed instances in DB
        try
        {
            using var db = new RuntimeDatabase(runtimeRoot);
            var dbInstances = db.GetAllInstances();
            if (dbInstances.Any(i =>
                string.Equals(i.MinecraftVersion, mcVersion, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        catch { /* DB unavailable — continue checking other sources */ }

        if (ActiveBuildTracker.Snapshot().Any(b =>
            !b.IsCompleted &&
            string.Equals(b.MinecraftVersion, mcVersion, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Check other incomplete Forge/NeoForge builds on disk
        foreach (var loaderName in new[] { "forge", "neoforge" })
        {
            var loaderDir = Path.Combine(runtimeRoot, loaderName);
            if (!Directory.Exists(loaderDir)) continue;

            foreach (var versionDir in Directory.GetDirectories(loaderDir))
            {
                var loaderVersion = Path.GetFileName(versionDir);

                // Skip the one we're deleting
                if (string.Equals(loaderName, excludeLoader, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(loaderVersion, excludeLoaderVersion, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Extract MC version from loader version (e.g. "1.21.8-58.1.18" → "1.21.8")
                var dirMcVersion = loaderVersion.Contains('-')
                    ? loaderVersion[..loaderVersion.IndexOf('-')]
                    : loaderVersion;

                if (string.Equals(dirMcVersion, mcVersion, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // Check configured servers
        var servers = _vm.Config.Hosts
            .SelectMany(h => h.Servers)
            .Where(s => s.IsAvailable && !s.IsSecondary);
        if (servers.Any(s =>
            string.Equals(s.MinecraftVersion, mcVersion, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Check configured local instances
        if (_vm.Config.LocalInstances.Any(li =>
            string.Equals(li.MinecraftVersion, mcVersion, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private bool IsLoaderVersionInUse(string loader, string loaderVersion, string runtimeRoot,
        string excludeMcVersion = "")
    {
        if (string.IsNullOrWhiteSpace(loader) || string.IsNullOrWhiteSpace(loaderVersion))
            return false;

        try
        {
            using var db = new RuntimeDatabase(runtimeRoot);
            if (db.GetAllInstances().Any(i =>
                !string.Equals(i.MinecraftVersion, excludeMcVersion, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.Loader, loader, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.LoaderVersion, loaderVersion, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        catch { /* DB unavailable — continue checking other sources */ }

        if (ActiveBuildTracker.Snapshot().Any(b =>
            !b.IsCompleted &&
            string.Equals(b.Loader, loader, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(b.LoaderVersion, loaderVersion, StringComparison.OrdinalIgnoreCase)))
            return true;

        var servers = _vm.Config.Hosts
            .SelectMany(h => h.Servers)
            .Where(s => s.IsAvailable && !s.IsSecondary);
        if (servers.Any(s =>
            string.Equals(s.Loader, loader, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.LoaderVersion, loaderVersion, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (_vm.Config.LocalInstances.Any(li =>
            string.Equals(li.Loader, loader, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(li.LoaderVersion, loaderVersion, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private string GetRuntimeInUseReason(
        string mcVersion,
        string loader,
        string loaderVersion,
        IReadOnlyCollection<ActiveBuildTracker.BuildSnapshot> activeBuilds)
    {
        if (_vm.Config.Hosts
            .SelectMany(h => h.Servers)
            .Where(s => s.IsAvailable && !s.IsSecondary)
            .Any(s => IsSameRuntime(s.MinecraftVersion, s.Loader, s.LoaderVersion, mcVersion, loader, loaderVersion)))
            return "server";

        if (_vm.Config.LocalInstances.Any(li =>
            IsSameRuntime(li.MinecraftVersion, li.Loader, li.LoaderVersion, mcVersion, loader, loaderVersion)))
            return "local instance";

        if (activeBuilds.Any(b =>
            string.Equals(b.MinecraftVersion, mcVersion, StringComparison.OrdinalIgnoreCase)))
            return "build running";

        return "";
    }

    private static bool IsSameRuntime(
        string leftMcVersion,
        string leftLoader,
        string leftLoaderVersion,
        string rightMcVersion,
        string rightLoader,
        string rightLoaderVersion)
    {
        return string.Equals(leftMcVersion?.Trim(), rightMcVersion?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftLoader?.Trim(), rightLoader?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftLoaderVersion?.Trim(), rightLoaderVersion?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }
}
