using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.Platform;
using HalerithAddonClient.Services;
using HalerithAddonClient.ViewModels;
using HalerithAddonClient.Windows;

namespace HalerithAddonClient;

public partial class App : Application
{
    private AboutWindow? _aboutWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        LoadThemeResources();
    }

    /// <summary>
    /// Dynamically loads theme resource dictionaries and style sheets using the
    /// actual runtime assembly name, so avares:// URIs resolve correctly even
    /// when AssemblyName is overridden per build configuration.
    /// </summary>
    private void LoadThemeResources()
    {
        var asmName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
        var baseUri = new Uri($"avares://{asmName}");

        // Colour palette resource dictionaries
        var rd = Resources as Avalonia.Controls.ResourceDictionary
              ?? new Avalonia.Controls.ResourceDictionary();
        if (Resources != rd) Resources = rd;

        rd.MergedDictionaries.Insert(0,
            new Avalonia.Markup.Xaml.Styling.ResourceInclude(baseUri)
            { Source = new Uri($"avares://{asmName}/Themes/Dark.axaml") });
        rd.MergedDictionaries.Add(
            new Avalonia.Markup.Xaml.Styling.ResourceInclude(baseUri)
            { Source = new Uri($"avares://{asmName}/Themes/DarkTheme.axaml") });

        // Control style sheets
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri)
            { Source = new Uri($"avares://{asmName}/Themes/Shared.axaml") });
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri)
            { Source = new Uri($"avares://{asmName}/Themes/Controls.axaml") });
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            FileLogger.Error($"Fatal unhandled exception: {ex.ExceptionObject}");
        };

        try
        {
            await RunStartup(Environment.GetCommandLineArgs().Skip(1).ToArray());
        }
        catch (SecretStorageUnavailableException ex)
        {
            FileLogger.Error("Linux secure storage is unavailable", ex);
            await ShowSecureStorageBlockedMessageAsync();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(3);
        }
        catch (Exception ex)
        {
            FileLogger.Error("Startup failed", ex);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(1);
        }
    }

    private async Task RunStartup(string[] args)
    {
        var version = GetAppVersion();
        AppConfigStore.EnsureRootDefaults();
        var appConfig = AppConfigStore.LoadAppConfig();

        // Initialise file logger as early as possible after config is loaded.
        FileLogger.Initialise(appConfig.EnableEventLogging);
        FileLogger.Info("=== Startup begin ===");
        FileLogger.Info($"Version={version}");
        AppConfigStore.MaintainStorage(FileLogger.Info);
        bool launchMode = args.Any(a => string.Equals(a, "-launch", StringComparison.OrdinalIgnoreCase));

        var databaseVersion = ClientDatabaseVersionGuard.CheckAndUpdate(version);
        if (!databaseVersion.IsCompatible)
        {
            FileLogger.Warn(
                $"Startup blocked because local database version {databaseVersion.StoredVersion} is newer than client version {databaseVersion.RunningVersion}.");
            await ShowStartupBlockedMessageAsync(databaseVersion);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime blockedDesktop)
                blockedDesktop.Shutdown(2);
            return;
        }

#if HALERITH_DEBUG_BUILD
        FileLogger.Info("HALERITH_DEBUG_BUILD enabled");
#endif
#if MANIFEST_DEBUG_DUMP
        ApiClient.ManifestDebugDumpEnabled = true;
        FileLogger.Info($"MANIFEST_DEBUG_DUMP enabled; manifest dumps will be written to {Path.Combine(LauncherPaths.GetAppDataDirectory(), "Logs")}");
#endif

        AppTheme.SetMode();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            desktopLifetime.ShutdownRequested += (_, _) =>
            {
                AppConfigStore.MaintainStorage(FileLogger.Info);
            };
        }

        FileLogger.Info("Creating window...");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (launchMode && CanStartLauncher(appConfig, out _))
            {
                LauncherWindow.ConfigureLogging(appConfig.EnableEventLogging);
                desktop.MainWindow = new LauncherWindow(appConfig);
                desktop.MainWindow.Show();
                FileLogger.Info("LauncherWindow created and shown");
            }
            else
            {
                if (launchMode)
                {
                    CanStartLauncher(appConfig, out var fallbackReason);
                    FileLogger.Info($"Launcher mode redirected to MainWindow: {fallbackReason}");
                }

                desktop.MainWindow = new HalerithAddonClient.Windows.MainWindow(version);
                desktop.MainWindow.Show();
                FileLogger.Info("MainWindow created and shown");
            }
        }

        FileLogger.Info("=== Startup complete ===");
    }

    private static bool CanStartLauncher(AppConfig appConfig, out string reason)
    {
        var accounts = AccountStore.LoadAll();
        var defaultAccountId = MinecraftProfileClient.NormalizeUuid(AccountStore.GetDefaultAccountId());
        if (string.IsNullOrWhiteSpace(defaultAccountId)
            || !accounts.Any(account => string.Equals(
                MinecraftProfileClient.NormalizeUuid(account.Uuid),
                defaultAccountId,
                StringComparison.OrdinalIgnoreCase)))
        {
            reason = "no usable default Minecraft account is configured";
            return false;
        }

        var host = appConfig.EnsureManagerScope();
        if (DemoMode.Enabled)
        {
            reason = DemoMode.VisibleServers(host.Servers).Count == 0 ? "no visible demo server is configured" : "";
            return reason.Length == 0;
        }
        if (host.VisibleServersFor(defaultAccountId).Count == 0)
        {
            reason = "no managed server is visible for the selected Minecraft account";
            return false;
        }

        reason = "";
        return true;
    }

    private void AboutApplicationMenuItem_OnClick(object? sender, EventArgs e)
    {
        ShowAboutWindow(GetAppVersion());
    }

    private void ShowAboutWindow(string version)
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow(version);
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            _ = _aboutWindow.ShowDialog(owner);
            return;
        }

        _aboutWindow.Show();
    }

    private async Task ShowStartupBlockedMessageAsync(ClientDatabaseVersionCheck databaseVersion)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var message =
            "This Halerith Addon Client is older than the local database.\n\n" +
            $"Client version: {databaseVersion.RunningVersion}\n" +
            $"Database version: {databaseVersion.StoredVersion}\n\n" +
            "Please install the latest client version and try again.";

        var dialog = new MessageBoxWindow(
            "Client Update Required",
            message,
            MessageBoxButton.OK,
            MessageBoxImage.Error)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        var closed = new TaskCompletionSource<object?>();
        dialog.Closed += (_, _) => closed.TrySetResult(null);
        desktop.MainWindow = dialog;
        dialog.Show();
        await closed.Task.ConfigureAwait(true);
    }

    private async Task ShowSecureStorageBlockedMessageAsync()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var dialog = new MessageBoxWindow(
            "Secure Storage Required",
            "Halerith could not access your Linux desktop keyring. Install or unlock the desktop Secret Service and try again.\n\nYour saved accounts and credentials were not changed.",
            MessageBoxButton.OK,
            MessageBoxImage.Error)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        var closed = new TaskCompletionSource<object?>();
        dialog.Closed += (_, _) => closed.TrySetResult(null);
        desktop.MainWindow = dialog;
        dialog.Show();
        await closed.Task.ConfigureAwait(true);
    }

    private static string GetAppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion)) return fileVersion;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion)) return informationalVersion;
        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

}
