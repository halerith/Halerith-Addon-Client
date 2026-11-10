namespace HalerithAddonClient.Core;

/// <summary>
/// Tracks in-progress game environment builds so they survive the Settings window
/// being closed and reopened.  When the user returns to the Instance tab, the UI
/// can reconnect to the running build's progress instead of offering a second Build.
/// </summary>
public static class ActiveBuildTracker
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, BuildState> _builds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Snapshot of a running build's progress.</summary>
    public sealed class BuildState
    {
        public string MinecraftVersion { get; init; } = "";
        public string Loader { get; init; } = "";
        public string LoaderVersion { get; init; } = "";

        /// <summary>The <see cref="Task"/> representing the running build pipeline.</summary>
        public Task BuildTask { get; init; } = Task.CompletedTask;

        // ── Progress (updated by the build, read by the UI) ──────────
        public int  Completed { get; set; }
        public int  Total     { get; set; }
        public string Label   { get; set; } = "Building…";

        /// <summary>Non-null when the build failed.</summary>
        public string? Error  { get; set; }
    }

    public sealed record BuildSnapshot(
        string MinecraftVersion,
        string Loader,
        string LoaderVersion,
        bool IsCompleted,
        string? Error);

    /// <summary>Builds a compound key for an environment.</summary>
    private static string Key(string mcVersion, string loader, string loaderVersion)
        => $"{mcVersion.Trim()}|{loader.Trim()}|{loaderVersion.Trim()}".ToLowerInvariant();

    /// <summary>
    /// Returns the active build for the given environment, or null if none is running.
    /// Completed builds are cleaned up automatically.
    /// </summary>
    public static BuildState? Get(string mcVersion, string loader, string loaderVersion)
    {
        lock (_lock)
        {
            var key = Key(mcVersion, loader, loaderVersion);
            if (!_builds.TryGetValue(key, out var state)) return null;

            // Completed builds are kept briefly so the UI can show the final
            // result, but we return them so the caller can inspect Error/Completed.
            return state;
        }
    }

    /// <summary>
    /// Registers a new build.  Replaces any previous entry for the same environment
    /// (which should only happen if the previous build already completed).
    /// </summary>
    public static BuildState Register(string mcVersion, string loader, string loaderVersion, Task buildTask)
    {
        var state = new BuildState
        {
            MinecraftVersion = mcVersion.Trim(),
            Loader = loader.Trim(),
            LoaderVersion = loaderVersion.Trim(),
            BuildTask = buildTask
        };
        lock (_lock)
        {
            _builds[Key(mcVersion, loader, loaderVersion)] = state;
        }
        return state;
    }

    public static List<BuildSnapshot> Snapshot()
    {
        lock (_lock)
        {
            return _builds.Values
                .Select(s => new BuildSnapshot(
                    s.MinecraftVersion,
                    s.Loader,
                    s.LoaderVersion,
                    s.BuildTask.IsCompleted,
                    s.Error))
                .ToList();
        }
    }

    /// <summary>
    /// Removes the entry for a completed build.  Called by the UI after it has
    /// observed the final state and refreshed the panel.
    /// </summary>
    public static void Remove(string mcVersion, string loader, string loaderVersion)
    {
        lock (_lock)
        {
            _builds.Remove(Key(mcVersion, loader, loaderVersion));
        }
    }
}
