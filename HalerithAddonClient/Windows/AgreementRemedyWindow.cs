using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Windows;

/// <summary>Directs agreement and billing questions to the account portal without changing access locally.</summary>
public sealed class AgreementRemedyWindow : Window
{
    public AgreementRemedyWindow(EulaDocument document)
    {
        Title = "Agreement and billing review";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "You can review your Halerith account and subscription in the Addon portal. " +
                   "If you decline a changed Client agreement, contact billing support to discuss any required cancellation or refund review. " +
                   "Closing this window does not change your subscription.",
            TextWrapping = TextWrapping.Wrap
        });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button Add(string label, string url)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { status.Text = $"Could not open your browser. Visit {url}"; }
            };
            buttons.Children.Add(button);
            return button;
        }
        Add("Licensing portal", "https://addon.halerith.com/licensing");
        Add("Billing support", "https://halerith.com/#contact");
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
