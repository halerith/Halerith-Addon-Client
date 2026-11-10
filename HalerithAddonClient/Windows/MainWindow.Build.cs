using Avalonia.Controls;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    private string? _serverBuildButtonPollKey;
    private string? _localBuildButtonPollKey;
    private string? _serverBuildConsoleLabel;
    private string? _localBuildConsoleLabel;
    private bool _serverLaunchStarting;
    private bool _localLaunchStarting;
    private readonly Dictionary<string, System.Diagnostics.Process> _trustedBuiltInSessionProcesses = new(StringComparer.OrdinalIgnoreCase);

    private async Task<bool> HandleServerBuildActionAsync(ServerItemViewModel selectedServer)
    {
        if (!TryGetBuiltInRuntime(selectedServer, out var effectiveServer, out var mcVersion, out var loader, out var loaderVersion))
            return false;

        if (_serverLaunchStarting)
            return true;

        var target = BuiltInGameSessionTarget.Server(effectiveServer.HostId, effectiveServer.ServerPublicId);
        if (BuiltInGameSessionStore.Read(target) is not null)
        {
            KillBuiltInSession(target, effectiveServer.DisplayName);
            return true;
        }

        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            _vm.AppendConsole("Server version unknown. Refresh to retrieve server info.");
            return true;
        }

        var active = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
        if (active is not null && !active.BuildTask.IsCompleted)
        {
            StartServerBuildButtonPoll(RuntimeKey(mcVersion, loader, loaderVersion), effectiveServer, active);
            return true;
        }

        if (RuntimeExists(mcVersion, loader, loaderVersion))
            return false;

        if (!await EnsureServerBuildAccessAsync(effectiveServer))
            return true;

        var state = RuntimeBuildCoordinator.StartOrGetBuild(mcVersion, loader, loaderVersion);
        _vm.AppendConsole($"Building runtime for {effectiveServer.DisplayName} ({RuntimeDisplay(mcVersion, loader, loaderVersion)})...");
        StartServerBuildButtonPoll(RuntimeKey(mcVersion, loader, loaderVersion), effectiveServer, state);
        return true;
    }

    private async Task<bool> HandleLocalBuildActionAsync(LocalInstanceItemViewModel item)
    {
        var inst = item.Instance;
        var mcVersion = inst.MinecraftVersion;
        var loader = inst.Loader;
        var loaderVersion = inst.LoaderVersion;
        var target = BuiltInGameSessionTarget.LocalInstance(inst.Id);

        if (_localLaunchStarting)
            return true;

        if (BuiltInGameSessionStore.Read(target) is not null)
        {
            KillBuiltInSession(target, item.DisplayName);
            return true;
        }

        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            _vm.AppendConsole("MC version unknown.");
            return true;
        }

        var active = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
        if (active is not null && !active.BuildTask.IsCompleted)
        {
            item.BuildStatus = LocalInstanceBuildStatus.Building;
            StartLocalBuildButtonPoll(RuntimeKey(mcVersion, loader, loaderVersion), item, active);
            return true;
        }

        if (RuntimeExists(mcVersion, loader, loaderVersion))
            return false;

        item.BuildStatus = LocalInstanceBuildStatus.Building;
        var state = RuntimeBuildCoordinator.StartOrGetBuild(mcVersion, loader, loaderVersion);
        _vm.AppendConsole($"[{item.DisplayName}] Building runtime ({RuntimeDisplay(mcVersion, loader, loaderVersion)})...");
        StartLocalBuildButtonPoll(RuntimeKey(mcVersion, loader, loaderVersion), item, state);
        return true;
    }

    private async Task<bool> EnsureServerBuildAccessAsync(ServerItemViewModel server)
    {
        var config = _vm.CurrentConfig;
        if (config is null) return false;
        var gate = await ManagedServerAccessGate.EnsureAsync(
            config, server.ServerPublicId, ResolveSelectedManagedMinecraftUuid());
        if (gate.Allowed) return true;
        _vm.AppendConsole($"[{server.DisplayName}] {gate.Message}");
        return false;
    }

    private void KillBuiltInSession(BuiltInGameSessionTarget target, string label)
    {
        if (!TryKillTrustedBuiltInSession(target, out var message))
            BuiltInGameSessionStore.TryKillActiveOrStarting(target, out message);

        _vm.AppendConsole($"[{label}] {message}");
        UntrustBuiltInSession(target);

        _vm.RefreshLaunchState();
        UpdateLaunchButtonState();
        UpdateLocalLaunchButtonState();
    }

    private bool TryKillTrustedBuiltInSession(BuiltInGameSessionTarget target, out string message)
    {
        message = "";

        var record = BuiltInGameSessionStore.Read(target);
        if (record is null)
        {
            UntrustBuiltInSession(target);
            return false;
        }

        if (!_trustedBuiltInSessionProcesses.TryGetValue(SessionTargetKey(target), out var process))
        {
            return false;
        }

        try
        {
            if (process.Id != record.Pid
                || process.HasExited
                || !BuiltInGameSessionStore.IsJavaPid(process.Id))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            BuiltInGameSessionStore.Clear(target);
            message = "Game process killed.";
            return true;
        }
        catch (Exception ex)
        {
            BuiltInGameSessionStore.Clear(target);
            message = $"Unable to kill game process: {ex.Message}";
            return true;
        }
        finally
        {
            UntrustBuiltInSession(target);
        }
    }

    private void SetServerLaunchingButtonState()
    {
        if (_launchButton is null || _launchButtonText is null || _launchBuildProgressBar is null)
            return;

        SetSessionButtonState(_launchButton, _launchButtonText, _launchBuildProgressBar, BuiltInGameSessionState.Launching);
    }

    private void SetLocalLaunchingButtonState()
    {
        if (_localLaunchButton is null || _localLaunchButtonText is null || _localLaunchBuildProgressBar is null)
            return;

        SetSessionButtonState(_localLaunchButton, _localLaunchButtonText, _localLaunchBuildProgressBar, BuiltInGameSessionState.Launching);
    }

    private void UpdateLaunchButtonState()
    {
        if (_launchButton is null || _launchButtonText is null || _launchBuildProgressBar is null)
            return;

        var selected = _vm.SelectedServer;
        if (selected is null)
        {
            SetPrimaryButtonState(_launchButton, _launchButtonText, _launchBuildProgressBar,
                "▶  Launch", enabled: false, "success");
            return;
        }

        if (TryGetBuiltInRuntime(selected, out var effective, out var mcVersion, out var loader, out var loaderVersion))
        {
            UpdateServerRuntimeBuildState(effective);
            if (!ReferenceEquals(effective, selected))
                selected.RuntimeBuildStatus = effective.RuntimeBuildStatus;

            var target = BuiltInGameSessionTarget.Server(effective.HostId, effective.ServerPublicId);
            if (ReadBuiltInSessionForMainWindow(target) is { } session)
            {
                SetSessionButtonState(_launchButton, _launchButtonText, _launchBuildProgressBar, session.State);
                return;
            }

            if (string.IsNullOrWhiteSpace(mcVersion))
            {
                SetPrimaryButtonState(_launchButton, _launchButtonText, _launchBuildProgressBar,
                    "Build", enabled: false, "warning");
                return;
            }

            var key = RuntimeKey(mcVersion, loader, loaderVersion);
            var active = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
            if (active is not null && !active.BuildTask.IsCompleted)
            {
                effective.RuntimeBuildStatus = ServerRuntimeBuildStatus.Building;
                if (!ReferenceEquals(effective, selected))
                    selected.RuntimeBuildStatus = ServerRuntimeBuildStatus.Building;
                SetServerBuildActivity(active);
                SetBuildProgressButtonState(_launchButton, _launchButtonText, _launchBuildProgressBar, active);
                StartServerBuildButtonPoll(key, effective, active);
                return;
            }

            if (active is not null)
                ActiveBuildTracker.Remove(mcVersion, loader, loaderVersion);

            if (!RuntimeExists(mcVersion, loader, loaderVersion))
            {
                effective.RuntimeBuildStatus = ServerRuntimeBuildStatus.NotBuilt;
                if (!ReferenceEquals(effective, selected))
                    selected.RuntimeBuildStatus = ServerRuntimeBuildStatus.NotBuilt;
                SetPrimaryButtonState(_launchButton, _launchButtonText, _launchBuildProgressBar,
                    "Build", enabled: !(_vm.IsSyncing), "warning");
                return;
            }

            effective.RuntimeBuildStatus = ServerRuntimeBuildStatus.Ready;
            if (!ReferenceEquals(effective, selected))
                selected.RuntimeBuildStatus = ServerRuntimeBuildStatus.Ready;
            SetPrimaryButtonState(_launchButton, _launchButtonText, _launchBuildProgressBar,
                "▶  Launch", enabled: _vm.IsLaunchEnabled, "success");
            return;
        }

        selected.RuntimeBuildStatus = ServerRuntimeBuildStatus.Ready;
        SetPrimaryButtonState(_launchButton, _launchButtonText, _launchBuildProgressBar,
            "▶  Launch", enabled: _vm.IsLaunchEnabled, "success");
    }

    private void UpdateLocalLaunchButtonState()
    {
        if (_localLaunchButton is null || _localLaunchButtonText is null || _localLaunchBuildProgressBar is null)
            return;

        var item = _selectedLocalInstance;
        if (item is null)
        {
            SetPrimaryButtonState(_localLaunchButton, _localLaunchButtonText, _localLaunchBuildProgressBar,
                "▶  Launch", enabled: false, "success");
            return;
        }

        var inst = item.Instance;
        var target = BuiltInGameSessionTarget.LocalInstance(inst.Id);
        if (ReadBuiltInSessionForMainWindow(target) is { } session)
        {
            SetSessionButtonState(_localLaunchButton, _localLaunchButtonText, _localLaunchBuildProgressBar, session.State);
            return;
        }

        if (string.IsNullOrWhiteSpace(inst.MinecraftVersion))
        {
            SetPrimaryButtonState(_localLaunchButton, _localLaunchButtonText, _localLaunchBuildProgressBar,
                "Build", enabled: false, "warning");
            return;
        }

        var key = RuntimeKey(inst.MinecraftVersion, inst.Loader, inst.LoaderVersion);
        var active = ActiveBuildTracker.Get(inst.MinecraftVersion, inst.Loader, inst.LoaderVersion);
        if (active is not null && !active.BuildTask.IsCompleted)
        {
            item.BuildStatus = LocalInstanceBuildStatus.Building;
            SetLocalBuildActivity(active);
            SetBuildProgressButtonState(_localLaunchButton, _localLaunchButtonText, _localLaunchBuildProgressBar, active);
            StartLocalBuildButtonPoll(key, item, active);
            return;
        }

        if (active is not null)
            ActiveBuildTracker.Remove(inst.MinecraftVersion, inst.Loader, inst.LoaderVersion);

        if (!RuntimeExists(inst.MinecraftVersion, inst.Loader, inst.LoaderVersion))
        {
            item.BuildStatus = LocalInstanceBuildStatus.NotBuilt;
            SetPrimaryButtonState(_localLaunchButton, _localLaunchButtonText, _localLaunchBuildProgressBar,
                "Build", enabled: true, "warning");
            return;
        }

        item.BuildStatus = LocalInstanceBuildStatus.Ready;
        SetPrimaryButtonState(_localLaunchButton, _localLaunchButtonText, _localLaunchBuildProgressBar,
            "▶  Launch", enabled: true, "success");
    }

    private void StartServerBuildButtonPoll(string key, ServerItemViewModel effectiveServer, ActiveBuildTracker.BuildState state)
    {
        if (string.Equals(_serverBuildButtonPollKey, key, StringComparison.OrdinalIgnoreCase))
            return;

        _serverBuildButtonPollKey = key;
        _ = PollServerBuildButtonAsync(key, effectiveServer, state);
    }

    private async Task PollServerBuildButtonAsync(string key, ServerItemViewModel effectiveServer, ActiveBuildTracker.BuildState state)
    {
        try
        {
            while (!state.BuildTask.IsCompleted)
            {
                if (!IsSelectedServerRuntime(key))
                {
                    ClearServerBuildActivity();
                    return;
                }

                SetBuildProgressButtonState(_launchButton, _launchButtonText, _launchBuildProgressBar, state);
                SetServerBuildActivity(state);
                AppendServerBuildProgress(effectiveServer, state);
                await Task.Delay(250);
            }

            await state.BuildTask;

            if (!IsSelectedServerRuntime(key))
            {
                ClearServerBuildActivity();
                return;
            }

            if (state.Error is not null)
                _vm.AppendConsole($"Build failed for {effectiveServer.DisplayName}: {state.Error}");
            else
                _vm.AppendConsole($"Runtime ready for {effectiveServer.DisplayName}.");

            ActiveBuildTracker.Remove(state.MinecraftVersion, state.Loader, state.LoaderVersion);
            ClearServerBuildActivity();
            UpdateAllServerRuntimeBuildStates();
            _vm.RefreshLaunchState();
            UpdateLaunchButtonState();

            if (state.Error is null)
                await _vm.LoadContentAsync(silent: false);
        }
        finally
        {
            if (string.Equals(_serverBuildButtonPollKey, key, StringComparison.OrdinalIgnoreCase))
                _serverBuildButtonPollKey = null;
        }
    }

    private void StartLocalBuildButtonPoll(string key, LocalInstanceItemViewModel item, ActiveBuildTracker.BuildState state)
    {
        if (string.Equals(_localBuildButtonPollKey, key, StringComparison.OrdinalIgnoreCase))
            return;

        _localBuildButtonPollKey = key;
        _ = PollLocalBuildButtonAsync(key, item, state);
    }

    private async Task PollLocalBuildButtonAsync(string key, LocalInstanceItemViewModel item, ActiveBuildTracker.BuildState state)
    {
        try
        {
            while (!state.BuildTask.IsCompleted)
            {
                if (!IsSelectedLocalRuntime(key))
                {
                    ClearLocalBuildActivity();
                    return;
                }

                item.BuildStatus = LocalInstanceBuildStatus.Building;
                SetBuildProgressButtonState(_localLaunchButton, _localLaunchButtonText, _localLaunchBuildProgressBar, state);
                SetLocalBuildActivity(state);
                AppendLocalBuildProgress(item, state);
                await Task.Delay(250);
            }

            await state.BuildTask;

            if (!IsSelectedLocalRuntime(key))
            {
                ClearLocalBuildActivity();
                return;
            }

            if (state.Error is not null)
            {
                item.BuildStatus = LocalInstanceBuildStatus.NotBuilt;
                _vm.AppendConsole($"[{item.DisplayName}] Build failed: {state.Error}");
            }
            else
            {
                item.BuildStatus = LocalInstanceBuildStatus.Ready;
                _vm.AppendConsole($"[{item.DisplayName}] Runtime ready.");
            }

            ActiveBuildTracker.Remove(state.MinecraftVersion, state.Loader, state.LoaderVersion);
            ClearLocalBuildActivity();
            UpdateLocalLaunchButtonState();

            if (state.Error is null)
                await RefreshLocalInstanceContentAsync(item);
        }
        finally
        {
            if (string.Equals(_localBuildButtonPollKey, key, StringComparison.OrdinalIgnoreCase))
                _localBuildButtonPollKey = null;
        }
    }

    private bool TryGetBuiltInRuntime(
        ServerItemViewModel selected,
        out ServerItemViewModel effectiveServer,
        out string mcVersion,
        out string loader,
        out string loaderVersion)
    {
        effectiveServer = GetEffectiveServerForLaunch(selected);
        mcVersion = effectiveServer.MinecraftVersion;
        loader = effectiveServer.Loader;
        loaderVersion = effectiveServer.LoaderVersion;
        return true;
    }

    private void UpdateAllServerRuntimeBuildStates()
    {
        foreach (var server in _vm.Servers.Where(s => !s.IsSecondary).ToList())
            UpdateServerRuntimeBuildState(server);

        foreach (var secondary in _vm.Servers.Where(s => s.IsSecondary).ToList())
        {
            var primary = _vm.Servers.FirstOrDefault(s =>
                string.Equals(s.HostId, secondary.HostId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.ServerPublicId, secondary.PrimaryServerPublicId, StringComparison.OrdinalIgnoreCase));
            secondary.RuntimeBuildStatus = primary?.RuntimeBuildStatus ?? ServerRuntimeBuildStatus.Unknown;
        }
    }

    private void UpdateServerRuntimeBuildState(ServerItemViewModel server)
    {
        var mcVersion = server.MinecraftVersion;
        var loader = server.Loader;
        var loaderVersion = server.LoaderVersion;

        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            server.RuntimeBuildStatus = ServerRuntimeBuildStatus.Unknown;
            return;
        }

        var active = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
        if (active is not null && !active.BuildTask.IsCompleted)
        {
            server.RuntimeBuildStatus = ServerRuntimeBuildStatus.Building;
            return;
        }

        server.RuntimeBuildStatus = RuntimeExists(mcVersion, loader, loaderVersion)
            ? ServerRuntimeBuildStatus.Ready
            : ServerRuntimeBuildStatus.NotBuilt;
    }

    private ServerItemViewModel GetEffectiveServerForLaunch(ServerItemViewModel server)
    {
        if (!server.IsSecondary)
            return server;

        return _vm.Servers.FirstOrDefault(s =>
            string.Equals(s.ServerPublicId, server.PrimaryServerPublicId, StringComparison.OrdinalIgnoreCase)) ?? server;
    }

    private bool IsSelectedServerRuntime(string key)
    {
        var selected = _vm.SelectedServer;
        return selected is not null
            && TryGetBuiltInRuntime(selected, out _, out var mcVersion, out var loader, out var loaderVersion)
            && string.Equals(key, RuntimeKey(mcVersion, loader, loaderVersion), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSelectedLocalRuntime(string key)
    {
        var inst = _selectedLocalInstance?.Instance;
        return inst is not null
            && string.Equals(key, RuntimeKey(inst.MinecraftVersion, inst.Loader, inst.LoaderVersion), StringComparison.OrdinalIgnoreCase);
    }

    private static bool RuntimeExists(string mcVersion, string loader, string loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion))
            return false;

        try
        {
            var runtimeRoot = Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
            using var db = new RuntimeDatabase(runtimeRoot);
            return db.InstanceExists(mcVersion, loader, loaderVersion);
        }
        catch
        {
            return false;
        }
    }

    private static string RuntimeKey(string mcVersion, string loader, string loaderVersion)
        => $"{mcVersion.Trim()}|{loader.Trim()}|{loaderVersion.Trim()}".ToLowerInvariant();

    private static string RuntimeDisplay(string mcVersion, string loader, string loaderVersion)
    {
        var loaderDisplay = string.IsNullOrWhiteSpace(loader)
            ? "Vanilla"
            : string.IsNullOrWhiteSpace(loaderVersion)
                ? loader.Trim()
                : $"{loader.Trim()} {loaderVersion.Trim()}";
        return $"{mcVersion.Trim()} - {loaderDisplay}";
    }

    private BuiltInGameSessionRecord? ReadBuiltInSessionForMainWindow(BuiltInGameSessionTarget target)
    {
        var record = BuiltInGameSessionStore.Read(target);
        if (record is null)
        {
            UntrustBuiltInSession(target);
            return null;
        }

        var key = SessionTargetKey(target);
        if (_trustedBuiltInSessionProcesses.TryGetValue(key, out var process))
        {
            try
            {
                if (process.Id == record.Pid
                    && !process.HasExited
                    && BuiltInGameSessionStore.IsJavaPid(process.Id))
                {
                    return record;
                }
            }
            catch { }
        }

        _trustedBuiltInSessionProcesses.Remove(key);
        return BuiltInGameSessionStore.ReadVerifiedFresh(target);
    }

    private void TrustBuiltInSession(BuiltInGameSessionTarget target, System.Diagnostics.Process process)
    {
        _trustedBuiltInSessionProcesses[SessionTargetKey(target)] = process;
    }

    private void UntrustBuiltInSession(BuiltInGameSessionTarget target)
    {
        _trustedBuiltInSessionProcesses.Remove(SessionTargetKey(target));
    }

    private static string SessionTargetKey(BuiltInGameSessionTarget target)
        => $"{target.Kind}|{target.HostId}|{target.Id}";

    private void SetBuildProgressButtonState(
        Button button,
        TextBlock text,
        ProgressBar progress,
        ActiveBuildTracker.BuildState state)
    {
        SetPrimaryButtonState(button, text, progress, "Building...", enabled: false, "warning");
    }

    private static int BuildProgressPercent(ActiveBuildTracker.BuildState state)
    {
        var total = Math.Max(state.Total, 1);
        return Math.Clamp((int)Math.Round(state.Completed / (double)total * 100), 0, 100);
    }

    private void SetServerBuildActivity(ActiveBuildTracker.BuildState state)
    {
        _vm.SetBuildActivityProgress(BuildProgressPercent(state));
    }

    private void ClearServerBuildActivity()
    {
        _vm.SetBuildActivityProgress(null);
        _serverBuildConsoleLabel = null;
    }

    private void SetLocalBuildActivity(ActiveBuildTracker.BuildState state)
    {
        if (_localProgressBar is null)
            return;

        _localProgressBar.IsVisible = true;
        _localProgressBar.IsIndeterminate = false;
        _localProgressBar.Maximum = 100;
        _localProgressBar.Value = BuildProgressPercent(state);
    }

    private void ClearLocalBuildActivity()
    {
        if (_localProgressBar is null)
            return;

        _localProgressBar.IsVisible = true;
        _localProgressBar.IsIndeterminate = false;
        _localProgressBar.Value = 0;
        _localBuildConsoleLabel = null;
    }

    private void AppendServerBuildProgress(ServerItemViewModel server, ActiveBuildTracker.BuildState state)
    {
        var label = state.Label;
        if (string.IsNullOrWhiteSpace(label) || string.Equals(label, _serverBuildConsoleLabel, StringComparison.Ordinal))
            return;

        _serverBuildConsoleLabel = label;
        _vm.AppendConsole($"Building {server.DisplayName}: {label}");
    }

    private void AppendLocalBuildProgress(LocalInstanceItemViewModel item, ActiveBuildTracker.BuildState state)
    {
        var label = state.Label;
        if (string.IsNullOrWhiteSpace(label) || string.Equals(label, _localBuildConsoleLabel, StringComparison.Ordinal))
            return;

        _localBuildConsoleLabel = label;
        _vm.AppendConsole($"[{item.DisplayName}] Building: {label}");
    }

    private void SetPrimaryButtonState(
        Button button,
        TextBlock text,
        ProgressBar progress,
        string label,
        bool enabled,
        string styleClass,
        double? progressValue = null)
    {
        text.Text = label;
        SetBuildIconVisibility(button, label);
        button.IsEnabled = enabled;
        progress.IsVisible = progressValue.HasValue;
        progress.Value = progressValue.GetValueOrDefault();
        progress.IsIndeterminate = false;

        button.Classes.Set("success", string.Equals(styleClass, "success", StringComparison.OrdinalIgnoreCase));
        button.Classes.Set("curseforge", string.Equals(styleClass, "curseforge", StringComparison.OrdinalIgnoreCase));
        button.Classes.Set("warning", string.Equals(styleClass, "warning", StringComparison.OrdinalIgnoreCase));
        button.Classes.Set("accent", string.Equals(styleClass, "accent", StringComparison.OrdinalIgnoreCase));
        button.Classes.Set("danger", string.Equals(styleClass, "danger", StringComparison.OrdinalIgnoreCase));
    }

    private void SetSessionButtonState(
        Button button,
        TextBlock text,
        ProgressBar progress,
        BuiltInGameSessionState state)
    {
        if (state == BuiltInGameSessionState.Launching)
        {
            text.Text = "Launching...";
            SetBuildIconVisibility(button, text.Text);
            button.IsEnabled = true;
            progress.IsVisible = true;
            progress.IsIndeterminate = true;
            button.Classes.Set("success", true);
            button.Classes.Set("curseforge", false);
            button.Classes.Set("warning", false);
            button.Classes.Set("accent", false);
            button.Classes.Set("danger", false);
            return;
        }

        SetPrimaryButtonState(button, text, progress, "Kill", enabled: true, "danger");
    }

    private void SetBuildIconVisibility(Button button, string label)
    {
        var showBuildIcon = label.Contains("Build", StringComparison.OrdinalIgnoreCase);
        if (ReferenceEquals(button, _launchButton) && _launchBuildIcon is not null)
            _launchBuildIcon.IsVisible = showBuildIcon;
        if (ReferenceEquals(button, _localLaunchButton) && _localLaunchBuildIcon is not null)
            _localLaunchBuildIcon.IsVisible = showBuildIcon;
    }
}
