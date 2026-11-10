using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace HalerithAddonClient;

/// <summary>
/// A simple modal dialog that lets the user select one or more items from a list.
/// </summary>
public partial class SelectionDialog : Window
{
    public List<string> SelectedItems { get; } = [];

    public SelectionDialog()
    {
        InitializeComponent();
    }

    public SelectionDialog(string title, IReadOnlyList<string> items, bool multiSelect)
    {
        InitializeComponent();

        Title = title;
        _listBox.SelectionMode = multiSelect
            ? SelectionMode.Multiple
            : SelectionMode.Single;

        foreach (var item in items)
            _listBox.Items.Add(item);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Confirm();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void ListBox_DoubleTapped(object sender, TappedEventArgs e)
    {
        if (_listBox.SelectedItem is not null)
            Confirm();
    }

    private void Confirm()
    {
        SelectedItems.Clear();
        foreach (var item in _listBox.SelectedItems?.OfType<string>() ?? Enumerable.Empty<string>())
            SelectedItems.Add(item);

        if (SelectedItems.Count > 0)
        {
            Close(true);
        }
    }
}
