using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace HalerithAddonClient.Windows;

public sealed class MacUpdateStatusWindow : Window
{
    private static readonly string[] IconResourceNames =
    [
        "HalerithAddonClient.HalerithAddonClient.png",
        "HalerithAddonClient.HalerithAddonClient.ico",
    ];

    public MacUpdateStatusWindow()
    {
        Title = "Halerith Addon Client Update";
        Width = 340;
        Height = 104;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        SystemDecorations = SystemDecorations.None;

        AppWindowIcon.Apply(this);

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Background = Application.Current?.FindResource("SurfaceBrush") as IBrush ?? new SolidColorBrush(Color.FromRgb(32, 37, 46)),
            BorderBrush = Application.Current?.FindResource("BorderBrush") as IBrush ?? new SolidColorBrush(Color.FromRgb(58, 67, 80)),
            BorderThickness = new Thickness(1),
            Child = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(21, GridUnitType.Pixel),
                    new RowDefinition(1, GridUnitType.Star),
                },
                ColumnDefinitions =
                {
                    new ColumnDefinition(48, GridUnitType.Pixel),
                    new ColumnDefinition(1, GridUnitType.Star),
                },
                Children =
                {
                    CreateCloseButton(),
                    CreateIcon(),
                    CreateText(),
                },
            },
        };
    }

    public void PlaceNear(Window owner)
    {
        var screen = Screens.ScreenFromWindow(owner) ?? Screens.Primary;
        if (screen is null)
            return;

        var x = screen.WorkingArea.Right - (int)Width - 24;
        var y = screen.WorkingArea.Bottom - (int)Height - 24;
        Position = new PixelPoint(Math.Max(screen.WorkingArea.X + 12, x), Math.Max(screen.WorkingArea.Y + 12, y));
    }

    private static Control CreateIcon()
    {
        var image = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            Source = LoadIcon(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetRow(image, 1);

        return image;
    }

    private static Control CreateText()
    {
        var panel = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(panel, 1);
        Grid.SetColumn(panel, 1);

        panel.Children.Add(new TextBlock
        {
            Text = "Halerith Addon Client",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Application.Current?.FindResource("TextPrimaryBrush") as IBrush ?? Brushes.White,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Update in progress...",
            FontSize = 12,
            Foreground = Application.Current?.FindResource("TextSecondaryBrush") as IBrush ?? new SolidColorBrush(Color.FromRgb(190, 198, 210)),
        });

        return panel;
    }

    private Control CreateCloseButton()
    {
        var button = new Button
        {
            Width = 21,
            Height = 21,
            MinWidth = 21,
            MinHeight = 21,
            Padding = new Thickness(0),
            Content = "x",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Application.Current?.FindResource("TextSecondaryBrush") as IBrush ?? new SolidColorBrush(Color.FromRgb(190, 198, 210)),
        };
        Grid.SetColumn(button, 1);
        button.Click += CloseButton_Click;
        return button;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
        => Close();

    private static Bitmap? LoadIcon()
    {
        foreach (var resourceName in IconResourceNames)
        {
            try
            {
                using var stream = typeof(MacUpdateStatusWindow).Assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    continue;

                return new Bitmap(stream);
            }
            catch
            {
                // Try the next embedded icon format.
            }
        }

        return null;
    }
}
