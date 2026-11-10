using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HalerithAddonClient;

public enum MessageBoxButton { OK, YesNo }
public enum MessageBoxImage { None, Information, Warning, Error }

/// <summary>
/// Simple message box replacement for Avalonia (WPF MessageBox equivalent).
/// </summary>
public class MessageBoxWindow : Window
{
    private bool _result;

    public MessageBoxWindow(
        string title,
        string message,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        string? affirmativeButtonText = null,
        string? negativeButtonText = null)
    {
        Title = title;
        Width = 420;
        MinHeight = 160;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20),
            FontSize = 14
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        if (buttons == MessageBoxButton.OK)
        {
            var okBtn = new Button { Content = affirmativeButtonText ?? "OK", MinWidth = 81 };
            okBtn.Click += (_, _) => { _result = true; Close(_result); };
            buttonPanel.Children.Add(okBtn);
        }
        else if (buttons == MessageBoxButton.YesNo)
        {
            var yesBtn = new Button { Content = affirmativeButtonText ?? "Yes", MinWidth = 81 };
            yesBtn.Click += (_, _) => { _result = true; Close(_result); };
            buttonPanel.Children.Add(yesBtn);

            var noBtn = new Button { Content = negativeButtonText ?? "No", MinWidth = 81 };
            noBtn.Click += (_, _) => { _result = false; Close(_result); };
            buttonPanel.Children.Add(noBtn);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children = { messageText, buttonPanel }
        };
    }

    /// <summary>
    /// Shows the dialog modally and returns true if the user clicked OK/Yes.
    /// </summary>
    public new async Task<bool> ShowDialog(Window owner)
    {
        var result = await base.ShowDialog<bool>(owner);
        return result;
    }
}
