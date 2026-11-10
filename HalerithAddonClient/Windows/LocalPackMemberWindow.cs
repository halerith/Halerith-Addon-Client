using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.ContentSearch;
using HalerithAddonClient.Core.LocalModpacks;

namespace HalerithAddonClient.Windows;

public sealed class LocalPackMemberWindow : Window
{
    public LocalPackMemberWindow(LocalInstance instance, LocalPackMember member)
    {
        var cancellation = new CancellationTokenSource();
        var busy = false;
        Closing += (_, e) => { if (busy) { cancellation.Cancel(); e.Cancel = true; } };
        Closed += (_, _) => { cancellation.Cancel(); cancellation.Dispose(); };
        Title = "Edit pack content"; Width = 600; Height = 360; MinWidth = 450; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = member.Project.Name, FontSize = 18, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Pack version: " + member.Version.VersionName, TextWrapping = TextWrapping.Wrap });
        var enabled = new CheckBox { Content = "Install this item", IsChecked = member.Enabled }; panel.Children.Add(enabled);
        var over = new CheckBox { Content = "Override pack version", IsChecked = member.Overridden && member.Enabled }; panel.Children.Add(over);
        var versions = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = over.IsChecked == true,
            ItemTemplate = new FuncDataTemplate<ContentVersionResult>((v, _) => new TextBlock { Text = v?.DisplayLabel ?? "" }) }; panel.Children.Add(versions);
        over.IsCheckedChanged += (_, _) => versions.IsEnabled = over.IsChecked == true;
        var status = new TextBlock { Text = "Pack upgrades reset individual overrides.", TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var save = new Button { Content = "Apply", IsDefault = true }; buttons.Children.Add(save); panel.Children.Add(buttons); Content = new ScrollViewer { Content = panel };
        Loaded += async (_, _) =>
        {
            try
            {
                if (member.Project.Id.Length == 0) { over.IsEnabled = false; over.IsChecked = false; return; }
                IContentSearchService service = member.Project.Source == "modrinth" ? new ModrinthSearchService() : new CurseForgeSearchService();
                versions.ItemsSource = await service.GetVersionsAsync(member.Project.Id, instance.MinecraftVersion, member.Project.ContentType == ContentTypes.Mod ? instance.Loader : null, cancellation.Token);
                versions.SelectedIndex = 0;
            }
            catch (Exception error) { status.Text = error.Message; }
        };
        save.Click += async (_, _) =>
        {
            if (over.IsChecked == true && enabled.IsChecked == true && versions.SelectedItem is not ContentVersionResult) { status.Text = "Select a compatible version."; return; }
            save.IsEnabled = cancel.IsEnabled = false;
            busy = true;
            try
            {
                await new LocalPackInstaller().ChangeMemberAsync(instance.Id, instance.GetInstanceDirectory(), member.Path,
                    over.IsChecked == true ? versions.SelectedItem as ContentVersionResult : null, enabled.IsChecked == true, cancellation.Token);
                busy = false;
                Close();
            }
            catch (Exception error) { busy = false; status.Text = error.Message; save.IsEnabled = cancel.IsEnabled = true; if (cancellation.IsCancellationRequested) Close(); }
        };
    }
}
