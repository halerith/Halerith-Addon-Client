using Avalonia.Controls;
using Avalonia.Interactivity;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.LocalModpacks;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    private string _localSortColumn = "";
    private bool _localSortDescending;
    private void LocalContentGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (_selectedLocalInstance is not { } item) return;
        _localSortDescending = _localSortColumn == e.Column.SortMemberPath && !_localSortDescending;
        _localSortColumn = e.Column.SortMemberPath;
        _localAddonGrid.ItemsSource = ContentItemViewModel.OrderForColumn(item.CachedItems, _localSortColumn, _localSortDescending).ToList();
    }
    private void LocalPackMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ContentItemViewModel row || _selectedLocalInstance is not { } item || item.IsRefreshing) return;
        var actions = new List<MenuItem>();
        void Add(string label, Func<Task> action)
        {
            var menuItem = new MenuItem { Header = label };
            menuItem.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception error) { await new MessageBoxWindow("Local modpack", error.Message, MessageBoxButton.OK, MessageBoxImage.Error).ShowDialog(this); }
            };
            actions.Add(menuItem);
        }
        if (row.IsModpack)
        {
            Add("Choose release / file policy…", () => ManageLocalPackAsync(item));
            Add("Reapply / review files…", async () =>
            {
                var pack = LocalModpackStore.Load(item.Instance.GetInstanceDirectory());
                if (pack is not null) await ApplyLocalPackAsync(item, pack, default, forceCurrent: true);
                await RefreshLocalInstanceContentAsync(item);
            });
            Add("Remove modpack…", async () =>
            {
                if (!await new MessageBoxWindow("Remove local modpack", "Remove this pack and its tracked referenced addons? Bundled configuration and other files remain untracked. Worlds and independently added content are kept. Removed files are backed up.", MessageBoxButton.YesNo, MessageBoxImage.Warning).ShowDialog(this)) return;
                new LocalPackInstaller(_vm.AppendConsole).Remove(item.Instance.Id, item.Instance.GetInstanceDirectory());
                await RefreshLocalInstanceContentAsync(item);
            });
        }
        else if (row.LocalPlanItem?.PackMember is { } member)
        {
            if (member.Enabled && !member.Overridden)
                Add("Download…", async () =>
                {
                    await new LocalPackDownloadWindow(item.Instance, member).ShowDialog<bool>(this);
                    await RefreshLocalInstanceContentAsync(item);
                });
            Add("Edit pack member…", async () =>
            {
                if (member.Path.Length == 0) throw new InvalidOperationException("This file's metadata is unavailable. Use Reapply on the modpack row to retry.");
                await new LocalPackMemberWindow(item.Instance, member).ShowDialog(this);
                await RefreshLocalInstanceContentAsync(item);
            });
        }
        else Add("Review pack files…", async () =>
        {
            if (LocalModpackStore.Load(item.Instance.GetInstanceDirectory()) is { } pack) await ApplyLocalPackAsync(item, pack, default, true);
            await RefreshLocalInstanceContentAsync(item);
        });
        new ContextMenu { ItemsSource = actions }.Open(button);
    }
    private async Task ManageLocalPackAsync(LocalInstanceItemViewModel item, bool update = false)
    {
        var targetVersion = update ? item.CachedPlan?.Items.FirstOrDefault(p => p.Pack is not null)?.LatestVersionResult?.SourceVersionId : null;
        await new AddLocalInstanceWindow(item.Instance, initialVersionId: targetVersion).ShowDialog<LocalInstance?>(this);
        await CheckLocalInstanceReadinessAsync([item]);
        await RefreshLocalInstanceContentAsync(item);
        if (_selectedLocalInstance == item) ShowLocalInstanceContent(item);
    }
    private async Task<bool> ApplyLocalPackAsync(LocalInstanceItemViewModel item, LocalModpack pack, CancellationToken ct, bool forceCurrent = false)
    {
        if (!forceCurrent && item.CachedPlan?.Items.Any(p => p.Pack is not null && p.LatestVersionResult is not null) == true)
        {
            var result = await new AddLocalInstanceWindow(item.Instance, initialVersionId: item.CachedPlan.Items.First(p => p.Pack is not null).LatestVersionResult?.SourceVersionId).ShowDialog<LocalInstance?>(this);
            await CheckLocalInstanceReadinessAsync([item]);
            return result is not null;
        }
        var installer = new LocalPackInstaller(_vm.AppendConsole);
        var wasRefreshing = item.IsRefreshing;
        item.IsRefreshing = true;
        try
        {
        if (pack.Members.Any(m => m.Missing))
        {
            var source = new Core.ContentSearch.ContentSearchResult { Id = pack.ProjectId, Source = pack.Provider, Name = pack.Name };
            var release = new Core.ContentSearch.ContentVersionResult { SourceVersionId = pack.VersionId, VersionName = pack.Version };
            var refreshed = await Task.Run(() => new LocalPackInspector().InspectAsync(pack.ArchivePath, source, release, ct), ct);
            refreshed.Policy = pack.Policy; pack = refreshed;
        }
        using var prepared = await Task.Run(() => installer.PrepareAsync(item.Instance.GetInstanceDirectory(), pack, ct: ct, instanceId: item.Instance.Id), ct);
        if (!await ModpackFileReviewDialog.ShowReviewAsync(this, prepared.Review, ct)) return false;
        await Task.Run(() => installer.ApplyAsync(item.Instance.Id, prepared, ct), ct);
        return true;
        }
        finally { item.IsRefreshing = wasRefreshing; }
    }
}
