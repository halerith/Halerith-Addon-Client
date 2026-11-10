namespace HalerithAddonClient.Core;

public enum RuntimeVersionChangeKind
{
    None,
    MinecraftVersion,
    LoaderVersion,
    Loader,
    Mixed
}

public sealed record RuntimeVersionInfo(
    string MinecraftVersion,
    string Loader,
    string LoaderVersion)
{
    public static RuntimeVersionInfo From(ServerConfig server) => new(
        server.MinecraftVersion ?? "",
        server.Loader ?? "",
        server.LoaderVersion ?? "");

    public static RuntimeVersionInfo From(LocalInstance instance) => new(
        instance.MinecraftVersion ?? "",
        instance.Loader ?? "",
        instance.LoaderVersion ?? "");

    public static RuntimeVersionInfo From(ManifestServerInfo server) => new(
        server.MinecraftVersion ?? "",
        server.Loader ?? "",
        server.LoaderVersion ?? "");

    public string Display
    {
        get
        {
            if (string.IsNullOrWhiteSpace(MinecraftVersion)) return "";
            if (string.IsNullOrWhiteSpace(Loader))
                return $"{MinecraftVersion.Trim()} - Vanilla";

            var loader = Loader.Trim();
            var loaderName = char.ToUpperInvariant(loader[0]) + loader[1..];
            return string.IsNullOrWhiteSpace(LoaderVersion)
                ? $"{MinecraftVersion.Trim()} - {loaderName}"
                : $"{MinecraftVersion.Trim()} - {loaderName} {LoaderVersion.Trim()}";
        }
    }

    public RuntimeVersionInfo Normalized => new(
        MinecraftVersion.Trim(),
        Loader.Trim(),
        LoaderVersion.Trim());
}

public sealed record RuntimeVersionComparison(
    RuntimeVersionInfo Installed,
    RuntimeVersionInfo Target,
    RuntimeVersionChangeKind ChangeKind)
{
    public bool HasChange => ChangeKind != RuntimeVersionChangeKind.None;
    public string InstalledDisplay => Installed.Display;
    public string TargetDisplay => Target.Display;

    public static RuntimeVersionComparison None(RuntimeVersionInfo installed) =>
        new(installed, new RuntimeVersionInfo("", "", ""), RuntimeVersionChangeKind.None);

    public static RuntimeVersionComparison Compare(RuntimeVersionInfo installed, RuntimeVersionInfo? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.MinecraftVersion))
            return None(installed);

        var left = installed.Normalized;
        var right = target.Normalized;

        var mcChanged = !string.Equals(left.MinecraftVersion, right.MinecraftVersion, StringComparison.OrdinalIgnoreCase);
        var loaderChanged = !string.Equals(left.Loader, right.Loader, StringComparison.OrdinalIgnoreCase);
        var loaderVersionChanged = !string.Equals(left.LoaderVersion, right.LoaderVersion, StringComparison.OrdinalIgnoreCase);

        var changedCount = (mcChanged ? 1 : 0) + (loaderChanged ? 1 : 0) + (loaderVersionChanged ? 1 : 0);
        var kind = changedCount switch
        {
            0 => RuntimeVersionChangeKind.None,
            1 when mcChanged => RuntimeVersionChangeKind.MinecraftVersion,
            1 when loaderChanged => RuntimeVersionChangeKind.Loader,
            1 when loaderVersionChanged => RuntimeVersionChangeKind.LoaderVersion,
            _ => RuntimeVersionChangeKind.Mixed
        };

        return new(installed, target, kind);
    }
}

public sealed record RuntimeVersionPromotionResult(
    RuntimeVersionComparison Comparison,
    string ServerName)
{
    public bool HasChange => Comparison.HasChange;
    public string InstalledDisplay => Comparison.InstalledDisplay;
    public string TargetDisplay => Comparison.TargetDisplay;

    public string ConsoleMessage
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(ServerName) ? "Server" : ServerName.Trim();
            var target = string.IsNullOrWhiteSpace(TargetDisplay) ? "the new server version" : TargetDisplay;
            var fromTo = string.IsNullOrWhiteSpace(InstalledDisplay)
                ? target
                : $"{InstalledDisplay} -> {target}";

            return $"{name} version changed: {fromTo}. Runtime needs to be built before launch.";
        }
    }
}

public static class RuntimeVersionPromoter
{
    public static RuntimeVersionPromotionResult? ApplyManifestTarget(ServerConfig? server, ManifestServerInfo? target)
    {
        if (server is null || target is null || string.IsNullOrWhiteSpace(target.MinecraftVersion))
            return null;

        var comparison = RuntimeVersionComparison.Compare(
            RuntimeVersionInfo.From(server),
            RuntimeVersionInfo.From(target));

        if (!comparison.HasChange)
            return null;

        server.MinecraftVersion = target.MinecraftVersion.Trim();
        server.Loader = target.Loader.Trim();
        server.LoaderVersion = target.LoaderVersion.Trim();

        return new RuntimeVersionPromotionResult(
            comparison,
            string.IsNullOrWhiteSpace(server.ServerName) ? target.Name : server.ServerName);
    }
}
