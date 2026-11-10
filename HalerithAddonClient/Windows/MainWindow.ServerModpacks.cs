using Avalonia.Controls;
using Avalonia.Interactivity;
using HalerithAddonClient.Core;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    private void ServerPackMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ContentItemViewModel row } || !row.HasServerMissingPackControls
            || row.Action is not { } action || _vm.SelectedServer is not { } server || _vm.CurrentConfig is not { } config)
            return;
        var download = new MenuItem { Header = "Manual Download…" };
        download.Click += async (_, _) =>
        {
            try
            {
                var current = () => ReferenceEquals(_vm.SelectedServer, server) && !server.IsRefreshing
                    && !_vm.IsSyncing && server.CachedItems.Contains(row);
                if (!current()) return;
                var dialog = new ServerPackDownloadWindow(action, config, current);
                var imported = await dialog.ShowDialog<bool>(this);
                if (imported && current() && dialog.InstalledPath is { } installedPath)
                    await _vm.RecordManualServerPackImportAsync(server, row, installedPath);
            }
            catch (Exception error)
            {
                await new MessageBoxWindow("Manual Download", error.Message, MessageBoxButton.OK, MessageBoxImage.Error).ShowDialog(this);
            }
        };
        new ContextMenu { ItemsSource = new[] { download } }.Open((Button)sender);
    }
}
