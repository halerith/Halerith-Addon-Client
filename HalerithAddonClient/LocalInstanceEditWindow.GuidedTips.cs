using HalerithAddonClient.Core;
using HalerithAddonClient.GuidedTips;

namespace HalerithAddonClient;

public partial class LocalInstanceEditWindow
{
    private GuidedTipTour? _guidedTipTour;

    private void StartGuidedTips()
    {
        _guidedTipTour ??= new GuidedTipTour(this, AppConfigStore.LoadAppConfig(),
        [
            new(
                "instance-settings.sections.v1",
                "Instance settings",
                "Settings control how this local instance launches. The Build section shows readiness and prepares its Minecraft environment.",
                () => _tabControl),
            new(
                "instance-settings.build.v1",
                "Build the Minecraft environment",
                "Open Build to review what is ready, create or repair the local environment, and open its folder.",
                () => _buildEnvButton,
                Prepare: () => _tabControl.SelectedIndex = 1),
        ]);
        _ = _guidedTipTour.StartAsync();
    }
}
