using HalerithAddonClient.Core;
using HalerithAddonClient.GuidedTips;

namespace HalerithAddonClient.Windows;

public partial class ServerSettingsWindow
{
    private GuidedTipTour? _guidedTipTour;

    private void StartGuidedTips()
    {
        _guidedTipTour ??= new GuidedTipTour(this, AppConfigStore.LoadAppConfig(),
        [
            new(
                "instance-settings.sections.v1",
                "Instance settings",
                "Settings control how this server behaves in the Client. The Build section shows readiness and prepares the local Minecraft environment.",
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

public partial class AddLocalInstanceWindow
{
    private GuidedTipTour? _guidedTipTour;

    private void StartGuidedTips()
    {
        _guidedTipTour ??= new GuidedTipTour(this, AppConfigStore.LoadAppConfig(),
        [
            new(
                "add-instance.source.v1",
                "Choose how to start",
                "Create a blank instance, find a CurseForge or Modrinth modpack, or import a pack file you already have.",
                () => _mode),
            new(
                "add-instance.details.v1",
                "Choose the instance details",
                "Give the instance a name and choose its Minecraft and loader versions. A selected modpack locks these to the versions it requires.",
                () => _nameTextBox),
            new(
                "add-instance.save.v1",
                "Create the instance",
                "Save creates the local instance. You can then add content, build it, and launch Minecraft from the main window.",
                () => _saveButton,
                Placement: GuidedTipPlacement.Above),
        ]);
        _ = _guidedTipTour.StartAsync();
    }
}
