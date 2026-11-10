using Avalonia.Controls;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    // ══════════════════════════════════════════════════════════════════════
    //  Launch workflows
    // ══════════════════════════════════════════════════════════════════════

    private async void LaunchButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var server = _vm.SelectedServer;
        if (server is null) return;

        if (await HandleServerBuildActionAsync(server))
            return;

        if (server.CachedPendingActions > 0)
        {
            _vm.AppendConsole("Launch blocked — update server content first.");
            UpdateLaunchButtonState();
            return;
        }

        if (server.CachedPlan is { } cachedPlan && ServerLaunchReadiness.HasRequiredManualFiles(cachedPlan))
        {
            _vm.AppendConsole("Launch blocked — install the required manual CurseForge files first.");
            UpdateLaunchButtonState();
            return;
        }

        var config = _vm.CurrentConfig;
        if (config is null) return;

        var previousAccountId = AccountStore.GetDefaultAccountId();
        var account = await EnsureLaunchAccountAsync(
            _accountComboBox,
            GetSelectedServerAccount,
            ApplyServerLaunchAccount,
            RefreshAccountSelector);
        if (account is null)
        {
            _vm.AppendConsole("Launch cancelled - Microsoft sign-in is required.");
            UpdateLaunchButtonState();
            return;
        }

        if (!string.Equals(MinecraftProfileClient.NormalizeUuid(previousAccountId),
                MinecraftProfileClient.NormalizeUuid(account.Uuid), StringComparison.OrdinalIgnoreCase))
        {
            await _vm.LoadServersAsync();
            server = _vm.Servers.FirstOrDefault(candidate =>
                string.Equals(candidate.HostId, server.HostId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ServerPublicId, server.ServerPublicId, StringComparison.OrdinalIgnoreCase));
            if (server is null)
            {
                _vm.AppendConsole("This server is not available to the selected Minecraft account.");
                return;
            }
            _vm.SelectedServer = server;
            config = _vm.CurrentConfig;
            if (config is null) return;
        }

        var launchGate = await ManagedServerAccessGate.EnsureAsync(
            config, server.ServerPublicId, account.Uuid);
        _vm.RefreshManagedAccessVisualState();

        if (!launchGate.Allowed)
        {
            _vm.AppendConsole(launchGate.Message);
            return;
        }

        await LaunchDefaultAsync(server, account);
    }

    private async Task LaunchDefaultAsync(ServerItemViewModel server, MinecraftAccount account)
    {
        var launchSessionStarted = false;
        _serverLaunchStarting = true;
        SetServerLaunchingButtonState();

        var effectiveServer = server;
        if (server.IsSecondary)
        {
            var primary = _vm.Servers.FirstOrDefault(s =>
                string.Equals(s.ServerPublicId, server.PrimaryServerPublicId, StringComparison.OrdinalIgnoreCase));
            if (primary is not null)
                effectiveServer = primary;
        }

        var mcVersion = effectiveServer.MinecraftVersion;
        var loader = effectiveServer.Loader;
        var loaderVersion = effectiveServer.LoaderVersion;

        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            _vm.AppendConsole("Server version unknown. Refresh to retrieve server info.");
            UpdateLaunchButtonState();
            return;
        }

        var username = account.Gamertag ?? server.Host.Username;

        _vm.AppendConsole("Preparing to launch...");

        try
        {
            if (!ReferenceEquals(server, _vm.SelectedServer))
                throw new InvalidOperationException("The selected server changed. Select the server and launch again.");
            if (_vm.CurrentConfig is not { } config)
                throw new InvalidOperationException("Client configuration is unavailable. Refresh before launching.");
            using var api = new ApiClient(config, effectiveServer.ServerPublicId);
            var manifest = await api.GetManifestAsync(new ManifestRequest(), CancellationToken.None);
            if (!manifest.Ok || !manifest.Authorized)
                throw new InvalidOperationException(manifest.Message ?? "Server content could not be verified. Refresh before launching.");
            ManagedServerAccessGate.RecordManifest(manifest, config.MinecraftUuid);
            _vm.RefreshManagedAccessVisualState();
            effectiveServer.CachedManifest = manifest;
            if (manifest is not null)
            {
                await Task.Run(async () =>
                {
                    var inventory = VerifiedServerContentScanner.Scan(effectiveServer.HostId, effectiveServer.ServerPublicId, config);
                    var plan = ServerContentPlanner.Build(manifest, inventory, config);
                    if (ServerLaunchReadiness.HasRequiredManualFiles(plan))
                        throw new InvalidOperationException("Required CurseForge files need manual installation before launching.");
                    if (ServerLaunchReadiness.HasPendingActions(plan))
                        throw new InvalidOperationException("Server content needs an update before launching.");
                    if (manifest.Modpack is not null)
                        await ModpackInstallationRequirements.AssertReadyAsync(manifest, plan, config, CancellationToken.None);
                });
            }
            var runtimeRoot = System.IO.Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
            using var db = new RuntimeDatabase(runtimeRoot);
            using var builder = new GameEnvironmentBuilder(database: db);
            var report = await builder.CheckReadinessAsync(mcVersion, loader, loaderVersion);
            if (!report.IsReady)
            {
                _vm.AppendConsole($"Game environment not ready — {report.MissingCount} item(s) missing. Build it in Settings → Instance tab.");
                UpdateLaunchButtonState();
                return;
            }

            await builder.RecordInstanceAsync(report);

            var env = await builder.ResolveEnvironmentAsync(mcVersion, loader, loaderVersion);
            var gameDir = System.IO.Path.Combine(
                LauncherPaths.GetAppDataDirectory(), "Runtime", "instances", effectiveServer.ServerPublicId);
            System.IO.Directory.CreateDirectory(gameDir);

            await PopulateServersDatAsync(server, gameDir);

            if (MinecraftLauncher.TryBuildDefaultLaunchPlan(env, gameDir, username, out var plan, out var message, account, effectiveServer.Server))
            {
                var logsDir = System.IO.Path.Combine(gameDir, "logs");
                System.IO.Directory.CreateDirectory(logsDir);
                var processOutputLogPath = System.IO.Path.Combine(logsDir, "launcher-output.log");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = plan.FileName,
                    Arguments = plan.Arguments,
                    WorkingDirectory = plan.WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null)
                {
                    var sessionTarget = BuiltInGameSessionTarget.Server(effectiveServer.HostId, effectiveServer.ServerPublicId);
                    BuiltInGameSessionStore.Save(new BuiltInGameSessionRecord(
                        sessionTarget,
                        proc.Id,
                        gameDir,
                        mcVersion,
                        loader,
                        loaderVersion,
                        BuiltInGameSessionState.Launching,
                        DateTime.UtcNow,
                        BuiltInGameSessionStore.TryGetProcessStartedAtUtc(proc)));
                    launchSessionStarted = true;
                    SetServerLaunchingButtonState();
                    StartBuiltInGameSessionMonitor(sessionTarget, proc, processOutputLogPath);
                }

                _vm.AppendConsole($"Launched {plan.Description}");
            }
            else
            {
                _vm.AppendConsole(message);
                UpdateLaunchButtonState();
            }
        }
        catch (Exception ex)
        {
            _vm.AppendConsole($"Launch failed: {ex.Message}");
            UpdateLaunchButtonState();
        }
        finally
        {
            _serverLaunchStarting = false;
            if (!launchSessionStarted)
                UpdateLaunchButtonState();
        }
    }
}
