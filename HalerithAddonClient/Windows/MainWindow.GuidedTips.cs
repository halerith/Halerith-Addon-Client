using HalerithAddonClient.GuidedTips;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    private GuidedTipTour? _guidedTipTour;

    private void StartGuidedTips()
    {
        _guidedTipTour ??= new GuidedTipTour(this, AppConfigStore.LoadAppConfig(),
        [
            new(
                "main.minecraft-account.v1",
                "Connect your Minecraft account",
                "Sign in to identify your Minecraft player and see the Manager servers you can access.",
                () => _accountComboBox,
                () => AccountStore.LoadAll().Count > 0),
            new(
                "main.servers.v1",
                "Your Manager servers",
                "Servers your Minecraft account can access appear here. Use a server's settings button to control its Client setup. If this area is empty, ask the server owner to add your Minecraft account.",
                () => _serversHeader),
            new(
                "main.local-instances.v1",
                "Your local instances",
                "Local instances live only on this computer. Use the add button to create one, then add and manage its content here.",
                () => _localInstancesHeader),
            new(
                "main.actions.v1",
                "Keep your instance ready",
                "Refresh checks for changes, Update All applies available content changes, and Build or Launch prepares and starts Minecraft.",
                () => _localInstancePanel.IsVisible ? _localUpdateButton : _applyButton),
        ]);
        _ = _guidedTipTour.StartAsync(650);
    }

    private void CompleteMinecraftAccountTip()
        => _guidedTipTour?.Complete("main.minecraft-account.v1");
}
