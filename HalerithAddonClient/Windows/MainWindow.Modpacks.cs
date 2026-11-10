using Avalonia.Controls;
using Avalonia.Interactivity;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    private string _packSortColumn = "";
    private bool _packSortDescending;
    private void ContentGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        var column = e.Column.SortMemberPath;
        _packSortDescending = column == _packSortColumn && !_packSortDescending;
        _packSortColumn = column;
        var rows = ContentItemViewModel.OrderForColumn(_vm.ContentItems, column, _packSortDescending).ToList();
        _vm.ContentItems.Clear(); foreach (var row in rows) _vm.ContentItems.Add(row);
    }
    private async void ReviewModpackFiles_Click(object? sender, RoutedEventArgs e)
    {
        try { await _vm.ReviewPackFilesAsync(); }
        catch (Exception error) { _vm.AppendConsole("Could not review pack files: " + error.Message); }
    }
}
