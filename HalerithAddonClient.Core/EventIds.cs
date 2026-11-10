namespace HalerithAddonClient.Core;

/// <summary>
/// Diagnostic event ID constants. Each component owns a 100-ID range with XX98
/// reserved for general warnings and XX99 for general errors.
/// </summary>
public static class EventIds
{
    /// <summary>Historical event source name retained for installer compatibility.</summary>
    public const string EventSource = "HalerithAddonClient";

    /// <summary>Install / Uninstall events (1000–1099).</summary>
    public static class Installer
    {
        public const int Started               = 1000;
        public const int Completed             = 1001;
        public const int CopyingFiles          = 1002;
        public const int ShortcutCreated       = 1003;
        public const int ArpRegistered         = 1004;
        public const int UninstallStarted      = 1005;
        public const int UninstallCompleted    = 1006;
        public const int EventSourceCreated    = 1007;
        public const int EventSourceDeleted    = 1008;
        public const int UpdateStarted         = 1009;
        public const int UpdateCompleted       = 1010;
        public const int WaitingForParent      = 1011;
        public const int ReplacedFile          = 1012;
        public const int ArpVersionUpdated     = 1013;
        public const int LaunchedMigrate       = 1014;
        public const int RelaunchedApp         = 1015;
        public const int ConfigRemoved         = 1016;
        public const int Warning               = 1098;
        public const int Error                 = 1099;
    }

    /// <summary>Post-update migration events (2000–2099).</summary>
    public static class Migrate
    {
        public const int Started               = 2000;
        public const int Completed             = 2001;
        public const int TaskStarted           = 2002;
        public const int TaskCompleted         = 2003;
        public const int TaskFailed            = 2004;
        public const int WaitingForParent      = 2005;
        public const int RelaunchedApp         = 2006;
        public const int NoTasks               = 2007;
        public const int Warning               = 2098;
        public const int Error                 = 2099;
    }

    /// <summary>Main client application events (3000–3099).</summary>
    public static class Client
    {
        public const int Started               = 3000;
        public const int HealthCheck           = 3001;
        public const int ManifestReceived      = 3002;
        public const int SyncStarted           = 3003;
        public const int SyncCompleted         = 3004;
        public const int SelfUpdateStarted     = 3005;
        public const int SelfUpdateCompleted   = 3006;
        public const int ReportSent            = 3007;
        public const int ConfigLoaded          = 3008;
        public const int ServerLoaded          = 3009;
        public const int DiagnosticSubmitted   = 3010;
        public const int HostAdded             = 3011;
        public const int HostEdited            = 3012;
        public const int ApiTrace              = 3013;
        public const int Warning               = 3098;
        public const int Error                 = 3099;
    }

    /// <summary>Launcher (sync + game launch) events (4000–4099).</summary>
    public static class Launcher
    {
        public const int Started               = 4000;
        public const int SyncCompleted         = 4001;
        public const int GameLaunched          = 4002;
        public const int ServersDatUpdated     = 4003;
        public const int StepStarted           = 4004;
        public const int NothingToSync         = 4005;
        public const int ReportSent            = 4006;
        public const int Warning               = 4098;
        public const int Error                 = 4099;
    }
}
