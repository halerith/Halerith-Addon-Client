using Avalonia.Interactivity;
using HalerithAddonClient.GuidedTips;

namespace HalerithAddonClient.Windows;

public partial class SettingsWindow
{
    private GuidedTipTour? _guidedTipTour;

    private void StartGuidedTips()
    {
        // Do not interrupt a direct Subscription/Help/About navigation. The
        // normal Settings entry point opens on Application and shows this tour.
        if (_tabControl.SelectedIndex != 0)
            return;

        _guidedTipTour ??= new GuidedTipTour(this, _vm.Config,
        [
            new(
                "client-settings.sections.v1",
                "Client settings",
                "Use these sections to choose launcher defaults, manage downloaded environments, review your subscription, find help, and view application information.",
                () => _tabControl,
                Placement: GuidedTipPlacement.WindowTopRight),
            new(
                "client-settings.guidance.v1",
                "Guided tips",
                "Turn contextual guidance on or off here. Restart guided tips whenever you want to see every tip again.",
                () => _guidedTipsPanel,
                Prepare: () => _tabControl.SelectedIndex = 0,
                Placement: GuidedTipPlacement.Above),
        ]);
        _ = _guidedTipTour.StartAsync();
    }

    private void RestartGuidedTips_Click(object? sender, RoutedEventArgs e)
    {
        GuidedTipProgressStore.Restart();
        _guidedTipsStatus.Text = "Guided tips restarted.";
    }
}
