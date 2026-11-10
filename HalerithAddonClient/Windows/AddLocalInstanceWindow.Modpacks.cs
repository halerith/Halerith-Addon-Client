using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.ContentSearch;
using HalerithAddonClient.Core.LocalModpacks;
using System.IO;

namespace HalerithAddonClient.Windows;

public partial class AddLocalInstanceWindow
{
    private LocalModpack? _selectedPack;
    private readonly LocalInstance? _existingInstance;
    private readonly string? _requestedVersionId;
    private string? _currentPackVersionId;
    private bool _packBusy, _installing;
    private CancellationTokenSource _packCancellation = new();
    private readonly List<string> _temporaryPacks = [];
    private static readonly string[] Policies = [ModpackFilePolicies.Preserve, ModpackFilePolicies.Review, ModpackFilePolicies.Replace, ModpackFilePolicies.Ignore];
    private async void PackIconDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Image image) return;
        image.Source = null;
        if (image.DataContext is not ContentSearchResult project) return;
        var icon = await HalerithAddonClient.Services.ContentIconLoader.LoadAsync(project.Source, project.Id, project.Slug, ContentTypes.Modpack, project.IconUrl);
        // Virtualized rows can be reused while their previous icon is still loading.
        if (ReferenceEquals(image.DataContext, project)) image.Source = icon;
    }
    private void PackWebsiteClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var uri = HttpsUrlPolicy.RequireHttpOrHttpsAbsoluteUri(url, "Pack website");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch { _packStatus.Text = "The pack website could not be opened. Please try again."; }
    }
    private async Task LoadExistingPackAsync()
    {
        var pack = LocalModpackStore.Load(_existingInstance!.GetInstanceDirectory());
        if (pack is null) return;
        Title = "Manage local modpack";
        _heading.Text = "Manage Local Modpack";
        _currentPackVersionId = pack.VersionId;
        _mode.SelectedIndex = pack.ProjectId.Length > 0 ? 1 : 2; _mode.IsEnabled = false;
        _searchPanel.IsVisible = false;
        _nameTextBox.Text = _existingInstance.Name; _serverAddressTextBox.Text = _existingInstance.ServerAddress;
        _nameTextBox.IsReadOnly = _serverAddressTextBox.IsReadOnly = true;
        _filePolicy.SelectedIndex = Array.IndexOf(Policies, pack.Policy);
        ShowInspected(pack); _saveButton.Content = "Review & apply";
        if (pack.ProjectId.Length > 0)
        {
            var project = new ContentSearchResult { Id = pack.ProjectId, Source = pack.Provider, Name = pack.Name, ContentType = ContentTypes.Modpack };
            _results.ItemsSource = new[] { project }; _results.SelectedItem = project;
            // Selection handler owns the cancellable release lookup.
        }
        else _packStatus.Text = "Imported pack: choose a newer export to upgrade, or apply this release again.";
        await Task.CompletedTask;
        UpdateSaveEnabled();
    }
    private void InitializePackSelection()
    {
        _filePolicy.ItemsSource = ModpackFilePolicies.Labels; _filePolicy.SelectedIndex = 0;
        if (!CurseForgeApiKeyStore.HasEffectiveKey)
        {
            _bothPackSourcesOption.IsVisible = false;
            _curseForgePackSourceOption.IsVisible = false;
            _source.SelectedIndex = 1;
        }
        Closing += (_, e) => { if (_installing) { e.Cancel = true; _packCancellation.Cancel(); } };
        Closed += (_, _) =>
        {
            _packCancellation.Cancel();
            foreach (var file in _temporaryPacks) { try { File.Delete(file); } catch (IOException) { } }
        };
    }
    private void ModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_blankRuntime is null) return;
        _packCancellation.Cancel(); _packCancellation = new(); _selectedPack = null; _packBusy = false;
        _blankRuntime.IsVisible = _mode.SelectedIndex == 0;
        _searchPanel.IsVisible = _results.IsVisible = _mode.SelectedIndex == 1;
        _importButton.IsVisible = _mode.SelectedIndex == 2;
        _packDetails.IsVisible = _mode.SelectedIndex > 0;
        _saveButton.Content = _mode.SelectedIndex == 0 ? "Create instance" : "Create & install";
        UpdateSaveEnabled();
    }
    private async void SearchClicked(object? sender, RoutedEventArgs e)
    {
        await PackWork(async ct =>
        {
            _selectedPack = null;
            var tasks = new List<Task<List<ContentSearchResult>>>();
            if (_source.SelectedIndex != 2) tasks.Add(new ModrinthSearchService().SearchAsync(_query.Text ?? "", ContentTypes.Modpack, ct: ct));
            if (_source.SelectedIndex != 1 && CurseForgeApiKeyStore.HasEffectiveKey)
                tasks.Add(new CurseForgeSearchService().SearchAsync(_query.Text ?? "", ContentTypes.Modpack, ct: ct));
            var results = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();
            ct.ThrowIfCancellationRequested(); _results.ItemsSource = results;
            _packStatus.Text = results.Count == 0 ? "No packs returned. Try another search or check provider availability." : $"{results.Count} packs. Select one to choose a release.";
        });
    }
    private async void PackSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_results.SelectedItem is not ContentSearchResult project) return;
        await PackWork(async ct =>
        {
            _selectedPack = null;
            IContentSearchService service = project.Source == "modrinth" ? new ModrinthSearchService() : new CurseForgeSearchService();
            var versions = await service.GetVersionsAsync(project.Id, ct: ct); ct.ThrowIfCancellationRequested();
            var releases = versions.Where(v => !v.IsServerPack).ToList();
            _packRelease.ItemsSource = releases; _packStatus.Text = "Choose a release. Its archive will be inspected before you can create the instance.";
            if (_existingInstance is null) _nameTextBox.Text = project.Name;
            _packRelease.SelectedItem = releases.FirstOrDefault(v => v.SourceVersionId == (_requestedVersionId ?? _currentPackVersionId)) ?? releases.FirstOrDefault();
        });
    }
    private async void ReleaseSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_packRelease.SelectedItem is not ContentVersionResult release || _results.SelectedItem is not ContentSearchResult project) return;
        await PackWork(async ct =>
        {
            _selectedPack = null;
            var temporary = Path.Combine(Path.GetTempPath(), "halerith-pack-" + Guid.NewGuid().ToString("N") + ".zip");
            _temporaryPacks.Add(temporary);
            await LocalPackDownloads.DownloadAsync(release.DownloadUrl, temporary, release.FileHash, release.FileSizeBytes, ct);
            var pack = await Task.Run(() => new LocalPackInspector().InspectAsync(temporary, project, release, ct), ct);
            ct.ThrowIfCancellationRequested(); ShowInspected(pack);
        });
    }
    private async void ImportClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import client modpack", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Client modpacks") { Patterns = ["*.mrpack", "*.zip"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
        await PackWork(async ct =>
        {
            _selectedPack = null;
            var pack = await Task.Run(() => new LocalPackInspector().InspectAsync(path, ct: ct), ct);
            ct.ThrowIfCancellationRequested(); ShowInspected(pack); _nameTextBox.Text = pack.Name;
        });
    }
    private void ShowInspected(LocalModpack pack)
    {
        _selectedPack = pack;
        _packRuntime.Text = $"{pack.Name} — {pack.Version}\nMinecraft {pack.MinecraftVersion} · {pack.Loader} {pack.LoaderVersion}\nRuntime versions are set by this pack release.";
        _optionalFiles.ItemsSource = pack.Members.Where(m => m.Optional).ToList();
        _packStatus.Text = $"{pack.Members.Count} referenced items · {pack.BundledFiles.Count} bundled files.\n" + string.Join("\n", pack.Warnings.Take(4));
    }
    private async Task PackWork(Func<CancellationToken, Task> work)
    {
        if (_installing) return;
        _packCancellation.Cancel(); _packCancellation = new(); var current = _packCancellation;
        _packBusy = true; _packStatus.Text = "Loading pack information…"; UpdateSaveEnabled();
        try { await work(current.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (ReferenceEquals(current, _packCancellation)) _packStatus.Text = error.Message; }
        finally { if (ReferenceEquals(current, _packCancellation)) { _packBusy = false; UpdateSaveEnabled(); } }
    }
    private async Task CreatePackInstanceAsync()
    {
        if (_selectedPack is not { } pack || _packBusy) return;
        _installing = _packBusy = true; _formPanel.IsEnabled = false; UpdateSaveEnabled();
        var instance = _existingInstance ?? new LocalInstance { Id = Guid.NewGuid().ToString("N")[..12], Name = _nameTextBox.Text!.Trim(),
            MinecraftVersion = pack.MinecraftVersion, Loader = pack.Loader, LoaderVersion = pack.LoaderVersion,
            ServerAddress = _serverAddressTextBox.Text?.Trim() ?? "", CreatedAtUtc = DateTime.UtcNow };
        pack.Policy = Policies[Math.Max(0, _filePolicy.SelectedIndex)];
        bool started = false;
        try
        {
            var installer = new LocalPackInstaller(message => Avalonia.Threading.Dispatcher.UIThread.Post(() => _packStatus.Text = message));
            if (_existingInstance is not null && !await new MessageBoxWindow("Apply pack release", "This applies the selected pack release and its exact runtime. Individual pack-member overrides will be reset. Independently added content and worlds are retained. Review the file changes next.", MessageBoxButton.YesNo, MessageBoxImage.Warning).ShowDialog(this)) return;
            using var prepared = await Task.Run(() => installer.PrepareAsync(instance.GetInstanceDirectory(), pack, resetOverrides: _existingInstance is not null, ct: _packCancellation.Token, instanceId: _existingInstance?.Id));
            if (!await ModpackFileReviewDialog.ShowReviewAsync(this, prepared.Review, _packCancellation.Token)) return;
            // Once application starts, retain a visible, repairable instance even if interrupted.
            instance.MinecraftVersion = pack.MinecraftVersion; instance.Loader = pack.Loader; instance.LoaderVersion = pack.LoaderVersion;
            // Content inventory rows reference this parent. Commit it before applying files
            // and recording addons, rather than waiting for the dialog to close.
            AppStorageDatabase.SaveLocalInstance(instance);
            started = true;
            await Task.Run(() => installer.ApplyAsync(instance.Id, prepared, _packCancellation.Token));
            var build = HalerithAddonClient.Services.RuntimeBuildCoordinator.StartOrGetBuild(instance.MinecraftVersion, instance.Loader, instance.LoaderVersion);
            var progress = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            progress.Tick += (_, _) => _packStatus.Text = "Building runtime: " + build.Label;
            progress.Start();
            try
            {
                await build.BuildTask.WaitAsync(_packCancellation.Token);
                if (!string.IsNullOrWhiteSpace(build.Error)) throw new InvalidOperationException(build.Error);
            }
            finally { progress.Stop(); }
        }
        catch (Exception error)
        {
            _packStatus.Text = error is OperationCanceledException ? "Installation cancelled. You can reapply the pack to finish." : error.Message;
            if (started)
                await new MessageBoxWindow("Local modpack", _packStatus.Text + "\nThe instance has been retained so you can retry or review it.", MessageBoxButton.OK, MessageBoxImage.Warning).ShowDialog(this);
        }
        finally
        {
            _installing = _packBusy = false; _formPanel.IsEnabled = true; UpdateSaveEnabled();
            if (started)
            {
                Close(instance);
            }
            else if (_existingInstance is null)
            {
                try { LocalPackInstaller.DiscardUnappliedCache(instance.GetInstanceDirectory()); }
                catch (System.IO.IOException) { _packStatus.Text = "Cancelled. Some temporary pack files could not be removed."; }
            }
        }
    }
}
