using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HalerithAddonClient.Core;

namespace HalerithAddonClient;

public sealed class ModpackFileReviewDialog : Window
{
    public ModpackFileReviewDialog(ModpackFileReview review)
    {
        Title = "Review modpack files"; Width = 960; Height = 560; MinWidth = 640; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(new TextBlock { Text = "Choose which pack files to apply. Existing files are backed up before replacement or removal.", TextWrapping = TextWrapping.Wrap });
        if (review.Files.Any(f => f.FixedAction is not null)) header.Children.Add(new TextBlock { Text = "Addon changes follow the selected pack release. Configuration and other bundled files can be kept individually.", TextWrapping = TextWrapping.Wrap });
        var bulk = new ComboBox { ItemsSource = new[] { "Apply proposed actions", "Keep files", "Mixed selections" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.Children.Add(bulk); layout.Children.Add(header);
        bool updating = false;
        void RefreshBulk()
        {
            updating = true;
            var changed = review.Files.Where(f => f.ProposedAction != "No change" && f.FixedAction is null).ToList();
            bulk.SelectedIndex = changed.All(f => f.SelectedAction == f.ProposedAction) ? 0 : changed.All(f => f.SelectedAction == "Keep") ? 1 : 2;
            updating = false;
        }
        bulk.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            if (bulk.SelectedIndex is < 0 or > 1) { RefreshBulk(); return; }
            var keep = bulk.SelectedIndex == 1;
            updating = true;
            foreach (var file in review.Files.Where(f => f.ProposedAction != "No change" && f.FixedAction is null)) file.SelectedAction = keep ? "Keep" : file.ProposedAction;
            updating = false; RefreshBulk();
        };
        System.ComponentModel.PropertyChangedEventHandler changedHandler = (_, _) => { if (!updating) RefreshBulk(); };
        foreach (var choice in review.Files) choice.PropertyChanged += changedHandler;
        Closed += (_, _) => { foreach (var choice in review.Files) choice.PropertyChanged -= changedHandler; };
        RefreshBulk();
        var grid = new DataGrid { ItemsSource = review.Files, AutoGenerateColumns = false, IsReadOnly = true, CanUserSortColumns = true };
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "File", Width = new DataGridLength(1, DataGridLengthUnitType.Star), SortMemberPath = "Path",
            CellTemplate = new FuncDataTemplate<ModpackFileChoice>((item, _) => new TextBlock { Text = item.Path, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6), MaxWidth = 580 })
        });
        grid.Columns.Add(new DataGridTextColumn { Header = "Change", Binding = new Binding("Change"), Width = new DataGridLength(150) });
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Action", Width = new DataGridLength(160),
            CellTemplate = new FuncDataTemplate<ModpackFileChoice>((item, _) =>
            {
                var combo = new ComboBox { ItemsSource = item.Actions, DataContext = item, Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Stretch };
                combo.Bind(SelectingItemsControl.SelectedItemProperty, new Binding("SelectedAction") { Mode = BindingMode.TwoWay });
                return combo;
            })
        });
        Grid.SetRow(grid, 1); layout.Children.Add(grid);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; cancel.Click += (_, _) => Close(false);
        var apply = new Button { Content = "Accept choices", IsDefault = true }; apply.Click += (_, _) => Close(true);
        footer.Children.Add(cancel); footer.Children.Add(apply); Grid.SetRow(footer, 2); layout.Children.Add(footer); Content = layout;
    }

    public static Task<bool> ShowReviewAsync(Window owner, ModpackFileReview review, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var dialog = new ModpackFileReviewDialog(review);
                using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
                completion.TrySetResult(await dialog.ShowDialog<bool>(owner));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }
}
