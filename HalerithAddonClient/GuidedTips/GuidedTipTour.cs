using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.GuidedTips;

internal enum GuidedTipPlacement
{
    Below,
    Above,
    WindowTopRight,
}

internal sealed record GuidedTipStep(
    string Id,
    string Title,
    string Message,
    Func<Control?> Target,
    Func<bool>? IsSatisfied = null,
    Action? Prepare = null,
    GuidedTipPlacement Placement = GuidedTipPlacement.Below);

internal sealed class GuidedTipTour : IDisposable
{
    private readonly Window _owner;
    private readonly IReadOnlyList<GuidedTipStep> _steps;
    private readonly Panel _host;
    private readonly Canvas _overlay;
    private readonly Grid _coachMark;
    private readonly TranslateTransform _translation;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly TextBlock _progress;
    private readonly Border _topPointer;
    private readonly Border _bottomPointer;
    private int _index;
    private bool _started;
    private bool _disposed;
    private bool _enabled;
    private bool _positionScheduled;
    private bool _positioning;
    private Control? _currentTarget;
    private GuidedTipPlacement _currentPlacement;

    internal GuidedTipTour(Window owner, AppConfig config, IReadOnlyList<GuidedTipStep> steps)
    {
        _owner = owner;
        _enabled = config.GuidedTipsEnabled;
        _steps = steps;

        _title = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _message = new TextBlock
        {
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#D2DAE5")),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 270,
            Margin = new Thickness(0, 6, 0, 12),
        };
        _progress = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8FA0B5")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var closeButton = new Button
        {
            Content = "Not now",
            MinWidth = 80,
            Height = 29,
            Padding = new Thickness(12, 0),
            FontSize = 12,
            Background = new SolidColorBrush(Color.Parse("#172131")),
            Foreground = new SolidColorBrush(Color.Parse("#DCE7F5")),
            BorderBrush = new SolidColorBrush(Color.Parse("#42516A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(closeButton, "Hide guided tips until next launch");
        closeButton.Click += (_, _) => Postpone();

        var nextButton = new Button
        {
            Content = "Next",
            MinWidth = 68,
            Height = 29,
            Padding = new Thickness(14, 0),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Background = new SolidColorBrush(Color.Parse("#2F80ED")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#69AAFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(nextButton, "Next guided tip");
        nextButton.Click += (_, _) => AcknowledgeCurrent();

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,8,Auto") };
        footer.Children.Add(_progress);
        Grid.SetColumn(closeButton, 1);
        footer.Children.Add(closeButton);
        Grid.SetColumn(nextButton, 3);
        footer.Children.Add(nextButton);

        var panel = new StackPanel();
        panel.Children.Add(_title);
        panel.Children.Add(_message);
        panel.Children.Add(footer);

        var pointerBrush = new SolidColorBrush(Color.Parse("#182433"));
        var pointerBorder = new SolidColorBrush(Color.Parse("#559EFF"));
        _topPointer = CreatePointer(pointerBrush, pointerBorder, new Thickness(0, 0, 0, -8));
        _bottomPointer = CreatePointer(pointerBrush, pointerBorder, new Thickness(0, -8, 0, 0));

        var bubble = new Border
        {
            Width = 330,
            Padding = new Thickness(15, 13),
            CornerRadius = new CornerRadius(10),
            Background = pointerBrush,
            BorderBrush = pointerBorder,
            BorderThickness = new Thickness(2),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Color = Color.Parse("#B0000000"),
                Blur = 22,
                Spread = 1,
                OffsetY = 6,
            }),
            Child = panel,
        };

        _translation = new TranslateTransform();
        _coachMark = new Grid
        {
            RowDefinitions = new RowDefinitions("12,Auto,12"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = _translation,
            IsVisible = false,
        };
        Grid.SetRow(_topPointer, 0);
        Grid.SetRow(_bottomPointer, 2);
        Grid.SetRow(bubble, 1);
        _coachMark.Children.Add(_topPointer);
        _coachMark.Children.Add(_bottomPointer);
        _coachMark.Children.Add(bubble);

        _host = owner.Content as Panel
            ?? throw new InvalidOperationException("Guided tips require a panel-based window root.");
        _overlay = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = false,
        };
        _overlay.SetValue(Panel.ZIndexProperty, 10000);
        if (_host is Grid hostGrid)
        {
            Grid.SetRowSpan(_overlay, Math.Max(1, hostGrid.RowDefinitions.Count));
            Grid.SetColumnSpan(_overlay, Math.Max(1, hostGrid.ColumnDefinitions.Count));
        }
        _overlay.Children.Add(_coachMark);
        _host.Children.Add(_overlay);

        _owner.Activated += OwnerActivated;
        _owner.Deactivated += OwnerDeactivated;
        _owner.SizeChanged += OwnerSizeChanged;
        _owner.Closed += OwnerClosed;
        GuidedTipProgressStore.ResetRequested += ProgressReset;
        GuidedTipProgressStore.EnabledChanged += TipsEnabledChanged;
    }

    internal async Task StartAsync(int delayMilliseconds = 350)
    {
        if (_disposed || _started) return;
        _started = true;
        if (delayMilliseconds > 0)
            await Task.Delay(delayMilliseconds);
        await Dispatcher.UIThread.InvokeAsync(ShowCurrent);
    }

    internal void Complete(string id)
    {
        GuidedTipProgressStore.Complete(id);
        if (_index < _steps.Count && string.Equals(_steps[_index].Id, id, StringComparison.Ordinal))
        {
            _index++;
            ShowCurrent();
        }
    }

    internal void AcknowledgeCurrent()
    {
        if (_index >= _steps.Count) return;
        GuidedTipProgressStore.Complete(_steps[_index].Id);
        _index++;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        _coachMark.IsVisible = false;
        if (_disposed || !_started || !_enabled || !_owner.IsActive || GuidedTipProgressStore.IsPostponedForSession())
            return;

        while (_index < _steps.Count)
        {
            var step = _steps[_index];
            if (GuidedTipProgressStore.IsComplete(step.Id))
            {
                _index++;
                continue;
            }
            if (step.IsSatisfied?.Invoke() == true)
            {
                GuidedTipProgressStore.Complete(step.Id);
                _index++;
                continue;
            }

            step.Prepare?.Invoke();
            if (step.Target() is not { IsVisible: true } target)
            {
                _index++;
                continue;
            }

            _title.Text = step.Title;
            _message.Text = step.Message;
            _progress.Text = $"{_index + 1} of {_steps.Count}";
            _topPointer.IsVisible = step.Placement == GuidedTipPlacement.Below;
            _bottomPointer.IsVisible = step.Placement == GuidedTipPlacement.Above;
            _coachMark.RowDefinitions[0].Height = new GridLength(_topPointer.IsVisible ? 12 : 0);
            _coachMark.RowDefinitions[2].Height = new GridLength(_bottomPointer.IsVisible ? 12 : 0);
            _currentTarget = target;
            _currentPlacement = step.Placement;
            _coachMark.IsVisible = true;
            QueuePositionCoachMark();
            return;
        }
    }

    private void Postpone()
    {
        GuidedTipProgressStore.PostponeForSession();
        _coachMark.IsVisible = false;
    }

    private void OwnerActivated(object? sender, EventArgs e) => ShowCurrent();
    private void OwnerDeactivated(object? sender, EventArgs e) => _coachMark.IsVisible = false;
    private void OwnerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_coachMark.IsVisible)
            QueuePositionCoachMark();
    }
    private void OwnerClosed(object? sender, EventArgs e) => Dispose();
    private void ProgressReset(object? sender, EventArgs e)
    {
        _index = 0;
        ShowCurrent();
    }
    private void TipsEnabledChanged(object? sender, bool enabled)
    {
        _enabled = enabled;
        if (!enabled) _coachMark.IsVisible = false;
        else ShowCurrent();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _coachMark.IsVisible = false;
        _owner.Activated -= OwnerActivated;
        _owner.Deactivated -= OwnerDeactivated;
        _owner.SizeChanged -= OwnerSizeChanged;
        _owner.Closed -= OwnerClosed;
        GuidedTipProgressStore.ResetRequested -= ProgressReset;
        GuidedTipProgressStore.EnabledChanged -= TipsEnabledChanged;
        _overlay.Children.Remove(_coachMark);
        _host.Children.Remove(_overlay);
    }

    private void QueuePositionCoachMark()
    {
        if (_disposed || _positionScheduled || !_coachMark.IsVisible)
            return;

        _positionScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _positionScheduled = false;
            if (!_disposed && _coachMark.IsVisible)
                PositionCoachMark();
        }, DispatcherPriority.Loaded);
    }

    private void PositionCoachMark()
    {
        if (_positioning || _currentTarget is null || _overlay.Bounds.Width <= 0 || _overlay.Bounds.Height <= 0)
            return;

        _positioning = true;
        try
        {
            _coachMark.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _coachMark.DesiredSize;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            double left;
            double top;
            if (_currentPlacement == GuidedTipPlacement.WindowTopRight)
            {
                left = _overlay.Bounds.Width - size.Width - 20;
                top = 58;
            }
            else
            {
                var targetOrigin = _currentTarget.TranslatePoint(default, _overlay);
                if (targetOrigin is null)
                    return;

                left = targetOrigin.Value.X + (_currentTarget.Bounds.Width - size.Width) / 2;
                top = _currentPlacement == GuidedTipPlacement.Above
                    ? targetOrigin.Value.Y - size.Height + 4
                    : targetOrigin.Value.Y + _currentTarget.Bounds.Height - 4;
            }

            left = Math.Clamp(left, 8, Math.Max(8, _overlay.Bounds.Width - size.Width - 8));
            top = Math.Clamp(top, 8, Math.Max(8, _overlay.Bounds.Height - size.Height - 8));
            left = Math.Round(left);
            top = Math.Round(top);

            // Render transforms do not participate in measure/arrange. Updating these
            // coordinates therefore cannot feed another position back into the window's
            // layout calculation (which previously caused Avalonia's infinite-layout crash).
            if (Math.Abs(_translation.X - left) >= 0.5)
                _translation.X = left;
            if (Math.Abs(_translation.Y - top) >= 0.5)
                _translation.Y = top;
        }
        finally
        {
            _positioning = false;
        }
    }

    private static Border CreatePointer(IBrush background, IBrush border, Thickness margin)
        => new()
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin,
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(2),
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new RotateTransform(45),
        };
}
