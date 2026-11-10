using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using HalerithAddonClient.Core;

namespace HalerithAddonClient;

public sealed class MediumConfidenceUpdateDialog : Window
{
    private const double DialogWidth = 1204;
    private const double ContentMargin = 32;
    private const double CheckboxColumnWidth = 52;
    private const double GridPaddingAllowance = 72;
    private const double MinItemColumnWidth = 160;
    private const double MaxItemColumnWidth = 360;
    private const double MinTypeColumnWidth = 96;
    private const double MaxTypeColumnWidth = 160;
    private const double MinFileColumnWidth = 260;

    private readonly ObservableCollection<MediumConfidenceUpdateChoice> _choices;
    private readonly CheckBox _selectAll;
    private readonly double _itemColumnWidth;
    private readonly double _typeColumnWidth;
    private readonly double _fileColumnWidth;

    public IReadOnlyList<SyncAction> SelectedActions =>
        _choices.Where(x => x.IsSelected).Select(x => x.Action).ToList();

    public MediumConfidenceUpdateDialog(IReadOnlyList<SyncAction> actions)
    {
        _choices = new ObservableCollection<MediumConfidenceUpdateChoice>(
            actions.Select(a => new MediumConfidenceUpdateChoice { Action = a, IsSelected = true }));
        (_itemColumnWidth, _typeColumnWidth, _fileColumnWidth) = CalculateColumnWidths(_choices);

        Title = "Review Updates";
        Width = DialogWidth;
        Height = 420;
        MinWidth = 720;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = "Segoe UI";

        _selectAll = new CheckBox
        {
            Content = "Select all",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        _selectAll.Click += (_, _) =>
        {
            var selected = _selectAll.IsChecked == true;
            foreach (var choice in _choices)
                choice.IsSelected = selected;
        };

        Content = BuildContent();
    }

    private Control BuildContent()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(16)
        };

        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = "Confirm Updates",
            FontSize = 16,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = "These local files look like the same server item by name or slug, but they were not exact filename or hash matches.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.Gray
        });
        header.Children.Add(_selectAll);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var dataGrid = new DataGrid
        {
            Name = "_updatesGrid",
            ItemsSource = _choices,
            AutoGenerateColumns = false,
            IsReadOnly = false,
            CanUserResizeColumns = true,
            Margin = new Thickness(0, 12, 0, 12),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        dataGrid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "",
            Binding = new Binding(nameof(MediumConfidenceUpdateChoice.IsSelected)) { Mode = BindingMode.TwoWay },
            Width = new DataGridLength(52)
        });
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Item",
            Binding = new Binding(nameof(MediumConfidenceUpdateChoice.Name)),
            Width = new DataGridLength(_itemColumnWidth)
        });
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Type",
            Binding = new Binding(nameof(MediumConfidenceUpdateChoice.ContentType)),
            Width = new DataGridLength(_typeColumnWidth)
        });
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Local File",
            Binding = new Binding(nameof(MediumConfidenceUpdateChoice.LocalFile)),
            Width = new DataGridLength(_fileColumnWidth)
        });
        dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Target File",
            Binding = new Binding(nameof(MediumConfidenceUpdateChoice.TargetFile)),
            Width = new DataGridLength(_fileColumnWidth)
        });
        Grid.SetRow(dataGrid, 1);
        grid.Children.Add(dataGrid);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var continueButton = new Button
        {
            Content = "Continue",
            MinWidth = 97,
            Height = 33
        };
        continueButton.Click += (_, _) => Close(true);
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 97,
            Height = 33
        };
        cancelButton.Click += (_, _) => Close(false);
        buttons.Children.Add(continueButton);
        buttons.Children.Add(cancelButton);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        return grid;
    }

    private static (double Item, double Type, double File) CalculateColumnWidths(
        IReadOnlyCollection<MediumConfidenceUpdateChoice> choices)
    {
        var item = Clamp(EstimateTextWidth(choices.Select(x => x.Name), "Item"), MinItemColumnWidth, MaxItemColumnWidth);
        var type = Clamp(EstimateTextWidth(choices.Select(x => x.ContentType), "Type"), MinTypeColumnWidth, MaxTypeColumnWidth);
        var remaining = DialogWidth
            - ContentMargin
            - CheckboxColumnWidth
            - GridPaddingAllowance
            - item
            - type;
        var file = Math.Max(MinFileColumnWidth, Math.Floor(remaining / 2));

        return (item, type, file);
    }

    private static double EstimateTextWidth(IEnumerable<string> values, string header)
    {
        var maxLen = values
            .Append(header)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Length)
            .DefaultIfEmpty(header.Length)
            .Max();

        return Math.Ceiling(maxLen * 7.2 + 28);
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));
}
