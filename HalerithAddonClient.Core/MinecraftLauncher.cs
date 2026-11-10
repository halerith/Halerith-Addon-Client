using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HalerithAddonClient.Core;

/// <summary>
/// Builds and launches a Minecraft Java process from a resolved <see cref="GameEnvironment"/>.
/// Constructs the full JVM command line: classpath, natives, arguments, and main class.
/// </summary>
public static class MinecraftLauncher
{
    /// <summary>
    /// Placeholder auth values used when no Microsoft account is selected (offline mode).
    /// </summary>
    private const string OfflineAccessToken = "0";
    private const string OfflineUuid = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// Builds a <see cref="LaunchPlan"/> for the Default launcher mode using the resolved
    /// game environment.  When a <see cref="MinecraftAccount"/> is provided, real auth
    /// credentials are substituted; otherwise offline/placeholder values are used.
    /// </summary>
    public static bool TryBuildDefaultLaunchPlan(
        GameEnvironment env,
        string gameDirectory,
        string username,
        out LaunchPlan plan,
        out string message,
        MinecraftAccount? account = null,
        ServerConfig? serverConfig = null)
    {
        plan = new LaunchPlan();

        if (!File.Exists(env.JavaExecutable))
        {
            message = $"Java executable not found: {env.JavaExecutable}. Build the environment on the Instance tab first.";
            return false;
        }

        if (!File.Exists(env.ClientJarPath))
        {
            message = $"Client JAR not found: {env.ClientJarPath}. Build the environment on the Instance tab first.";
            return false;
        }

        // Build the classpath: all library JARs + client JAR
        var classpathEntries = new List<string>(env.LibraryPaths) { env.ClientJarPath };
        var classpath = string.Join(Path.PathSeparator, classpathEntries);

        // Build JVM arguments — honour per-server Java settings
        var javaMode = serverConfig?.JavaMode ?? "Auto";
        var jvmArgs = new List<string>();

        if (string.Equals(javaMode, JavaLaunchSettings.ModeCustom, StringComparison.OrdinalIgnoreCase))
        {
            // Custom mode: user supplies the full JVM args (between java.exe and main class).
            // We still need to resolve classpath and natives from the env.
            var customArgs = string.IsNullOrWhiteSpace(serverConfig?.JavaCustomArgs)
                ? JavaLaunchSettings.DefaultCustomArgs
                : serverConfig.JavaCustomArgs;

            foreach (var token in SplitArgs(customArgs))
                jvmArgs.Add(SubstituteTemplates(token, env, classpath, gameDirectory));

            EnsureUnlockExperimentalVmOptions(jvmArgs);

            // Ensure classpath and natives are present if the user didn't include them.
            if (!jvmArgs.Any(a => a.StartsWith("-cp") || a.StartsWith("-classpath") || a.StartsWith("--class-path")))
            {
                jvmArgs.Add("-cp");
                jvmArgs.Add(classpath);
            }
            if (!jvmArgs.Any(a => a.Contains("java.library.path")))
                jvmArgs.Add($"-Djava.library.path={env.NativesDirectory}");
        }
        else
        {
            if (string.Equals(javaMode, JavaLaunchSettings.ModeManual, StringComparison.OrdinalIgnoreCase) && serverConfig is not null)
            {
                var maxMemoryMb = serverConfig.JavaMaxMemoryMb > 0
                    ? serverConfig.JavaMaxMemoryMb
                    : JavaLaunchSettings.DefaultMaxMemoryGb * 1024;

                jvmArgs.Add($"-Xmx{maxMemoryMb}M");
                AppendGarbageCollectorArguments(
                    jvmArgs,
                    JavaLaunchSettings.NormalizeGarbageCollector(serverConfig.JavaGarbageCollector));
            }
            else
            {
                jvmArgs.Add($"-Xmx{JavaLaunchSettings.DefaultMaxMemoryGb}G");
                AppendGarbageCollectorArguments(jvmArgs, JavaLaunchSettings.GarbageCollectorG1);
            }

            ResolveJvmArguments(env, classpath, gameDirectory, jvmArgs);
        }

        AppendLauncherJvmArguments(env, jvmArgs);

        // Append extra JVM arguments from mod loader (e.g. Forge/NeoForge module path, --add-opens, etc.)
        if (env.ExtraJvmArgs.Count > 0)
        {
            foreach (var extraArg in env.ExtraJvmArgs)
            {
                jvmArgs.Add(SubstituteTemplates(extraArg, env, classpath, gameDirectory));
            }
        }

        // Build game arguments
        var gameArgs = new List<string>();
        ResolveGameArguments(env, gameDirectory, username, gameArgs, account);

        // Append extra game arguments from mod loader (e.g. Forge/NeoForge tweakers)
        if (env.ExtraGameArgs.Count > 0)
        {
            foreach (var extraArg in env.ExtraGameArgs)
            {
                gameArgs.Add(SubstituteGameTemplates(extraArg, env, gameDirectory, username, account));
            }
        }

        // Full argument string: JVM args + main class + game args
        var allArgs = new List<string>();
        allArgs.AddRange(jvmArgs);
        allArgs.Add(env.MainClass);
        allArgs.AddRange(gameArgs);

        plan = new LaunchPlan
        {
            Mode = LaunchMode.Direct,
            FileName = env.JavaExecutable,
            Arguments = string.Join(" ", allArgs.Select(QuoteIfNeeded)),
            WorkingDirectory = gameDirectory,
            Description = string.IsNullOrWhiteSpace(env.Loader)
                ? $"Minecraft {env.MinecraftVersion}"
                : $"Minecraft {env.MinecraftVersion} ({env.Loader})",
        };

        message = $"Launching {plan.Description}";
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Argument resolution
    // ══════════════════════════════════════════════════════════════════════

    private static void ResolveJvmArguments(
        GameEnvironment env, string classpath, string gameDirectory,
        List<string> args)
    {
        // Memory defaults — skip if the caller already added -Xmx/-Xms (Auto/Manual mode).
        if (!args.Any(a => a.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)))
            args.Add($"-Xmx{JavaLaunchSettings.DefaultMaxMemoryGb}G");

        // Try to use the version JSON's JVM argument templates
        if (env.VersionDetail.Arguments?.Jvm is { Count: > 0 } jvmTemplates)
        {
            foreach (var element in jvmTemplates)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = SubstituteTemplates(element.GetString() ?? "", env, classpath, gameDirectory);
                    args.Add(value);
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    // Conditional argument with rules
                    if (!EvaluateRules(element)) continue;

                    if (element.TryGetProperty("value", out var valueProp))
                    {
                        if (valueProp.ValueKind == JsonValueKind.String)
                        {
                            args.Add(SubstituteTemplates(valueProp.GetString() ?? "", env, classpath, gameDirectory));
                        }
                        else if (valueProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in valueProp.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String)
                                    args.Add(SubstituteTemplates(item.GetString() ?? "", env, classpath, gameDirectory));
                            }
                        }
                    }
                }
            }
        }
        else
        {
            // Fallback: minimal JVM args for older versions or when templates are missing
            args.Add($"-Djava.library.path={env.NativesDirectory}");
            args.Add("-cp");
            args.Add(classpath);
        }

        // Default to ZGC when the resolved Mojang Java runtime supports it, unless
        // the profile or user-supplied manual args already picked a collector.
        var javaMajor = env.VersionDetail.JavaVersion?.MajorVersion ?? 17;
        if (javaMajor >= 15 && !HasExplicitGarbageCollectorOption(args))
            args.Add("-XX:+UseZGC");

    }

    private static void AppendGarbageCollectorArguments(List<string> args, string garbageCollector)
    {
        EnsureUnlockExperimentalVmOptions(args);

        switch (garbageCollector)
        {
            case JavaLaunchSettings.GarbageCollectorG1:
                AddIfMissing(args, "-XX:+UseG1GC");
                AddIfMissing(args, "-XX:G1NewSizePercent=20");
                AddIfMissing(args, "-XX:G1ReservePercent=20");
                AddIfMissing(args, "-XX:MaxGCPauseMillis=150");
                if (!args.Any(a => a.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase)))
                    args.Add("-Xms256m");
                break;

            case JavaLaunchSettings.GarbageCollectorShenandoah:
                AddIfMissing(args, "-XX:+UseShenandoahGC");
                break;

            default:
                AddIfMissing(args, "-XX:+UseZGC");
                break;
        }
    }

    private static void EnsureUnlockExperimentalVmOptions(List<string> args)
        => AddIfMissing(args, "-XX:+UnlockExperimentalVMOptions");

    private static void AddIfMissing(List<string> args, string value)
    {
        if (!args.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)))
            args.Add(value);
    }

    private static void AppendLauncherJvmArguments(GameEnvironment env, List<string> args)
    {
        if (!args.Any(a => a.StartsWith("-DlibraryDirectory=", StringComparison.OrdinalIgnoreCase)))
            args.Add($"-DlibraryDirectory={Path.Combine(env.RuntimeRoot, "libraries")}");

        if (!string.IsNullOrWhiteSpace(env.Log4jConfigurationPath) &&
            File.Exists(env.Log4jConfigurationPath) &&
            !args.Any(a => a.StartsWith("-Dlog4j.configurationFile=", StringComparison.OrdinalIgnoreCase)))
        {
            args.Add($"-Dlog4j.configurationFile={env.Log4jConfigurationPath}");
        }
    }

    private static bool HasExplicitGarbageCollectorOption(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("-Xincgc", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!arg.StartsWith("-XX:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (arg.Contains("UseZGC", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("UseG1GC", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("UseSerialGC", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("UseParallelGC", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("UseConcMarkSweepGC", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("UseShenandoahGC", StringComparison.OrdinalIgnoreCase)
                || arg.Contains("UseEpsilonGC", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void ResolveGameArguments(
        GameEnvironment env, string gameDirectory, string username,
        List<string> args, MinecraftAccount? account = null)
    {
        if (env.VersionDetail.Arguments?.Game is { Count: > 0 } gameTemplates)
        {
            foreach (var element in gameTemplates)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = SubstituteGameTemplates(element.GetString() ?? "", env, gameDirectory, username, account);
                    args.Add(value);
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    // Conditional game arguments (e.g. demo mode, custom resolution)
                    // Skip conditional game args for now
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(env.VersionDetail.MinecraftArguments))
        {
            // Legacy pre-1.13 argument string
            var legacyArgs = env.VersionDetail.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var arg in legacyArgs)
                args.Add(SubstituteGameTemplates(arg, env, gameDirectory, username, account));
        }
        else
        {
            // Absolute fallback
            var accessToken = account?.McAccessToken ?? OfflineAccessToken;
            args.AddRange(["--username", username,
                "--version", env.MinecraftVersion,
                "--gameDir", gameDirectory,
                "--assetsDir", env.AssetsDirectory,
                "--assetIndex", env.AssetIndexId,
                "--accessToken", accessToken]);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Template substitution
    // ══════════════════════════════════════════════════════════════════════

    private static string SubstituteTemplates(string template, GameEnvironment env,
        string classpath, string gameDirectory)
    {
        return template
            .Replace("${natives_directory}", env.NativesDirectory)
            .Replace("${launcher_name}", "HalerithAddonClient")
            .Replace("${launcher_version}", "1.0")
            .Replace("${classpath}", classpath)
            .Replace("${classpath_separator}", Path.PathSeparator.ToString())
            .Replace("${library_directory}", Path.Combine(env.RuntimeRoot, "libraries"))
            .Replace("${version_name}", env.MinecraftVersion)
            .Replace("${game_directory}", gameDirectory);
    }

    private static string SubstituteGameTemplates(string template, GameEnvironment env,
        string gameDirectory, string username, MinecraftAccount? account = null)
    {
        var uuid        = account?.Uuid ?? OfflineUuid;
        var accessToken = account?.McAccessToken ?? OfflineAccessToken;
        var userType    = account is not null ? "msa" : "legacy";
        var clientId    = account is not null ? MicrosoftAuthService.ClientId : "";
        var xuid        = ResolveAccountXuid(account);

        return template
            .Replace("${auth_player_name}", username)
            .Replace("${version_name}", env.MinecraftVersion)
            .Replace("${game_directory}", gameDirectory)
            .Replace("${assets_root}", env.AssetsDirectory)
            .Replace("${assets_index_name}", env.AssetIndexId)
            .Replace("${auth_uuid}", uuid)
            .Replace("${auth_access_token}", accessToken)
            .Replace("${clientid}", clientId)
            .Replace("${auth_xuid}", xuid)
            .Replace("${user_type}", userType)
            .Replace("${version_type}", "release")
            .Replace("${user_properties}", "{}");
    }

    private static string ResolveAccountXuid(MinecraftAccount? account)
    {
        if (account is null)
            return "";

        if (!string.IsNullOrWhiteSpace(account.Xuid))
            return account.Xuid.Trim();

        return TryReadJwtStringClaim(account.McAccessToken, "xuid") ?? "";
    }

    private static string? TryReadJwtStringClaim(string token, string claimName)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
                return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);

            return doc.RootElement.TryGetProperty(claimName, out var claim) && claim.ValueKind == JsonValueKind.String
                ? claim.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool EvaluateRules(JsonElement element)
    {
        if (!element.TryGetProperty("rules", out var rulesEl)) return true;

        var osName = GetCurrentOsName();
        bool allowed = false;

        foreach (var rule in rulesEl.EnumerateArray())
        {
            var action = rule.TryGetProperty("action", out var actionProp)
                ? actionProp.GetString() ?? "" : "";

            if (rule.TryGetProperty("os", out var osProp))
            {
                if (osProp.TryGetProperty("name", out var osNameProp))
                {
                    if (string.Equals(osNameProp.GetString(), osName, StringComparison.OrdinalIgnoreCase))
                        allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
                }
                else if (osProp.TryGetProperty("arch", out var archProp))
                {
                    var arch = RuntimeInformation.OSArchitecture == Architecture.X64 ? "x86" : "x86"; // MC uses "x86" broadly
                    if (string.Equals(archProp.GetString(), arch, StringComparison.OrdinalIgnoreCase))
                        allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                // No OS condition — applies globally
                allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    private static string GetCurrentOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
        return "unknown";
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Contains(' ') || arg.Contains('"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }

    /// <summary>
    /// Splits a string of JVM/game arguments respecting double-quoted tokens.
    /// e.g. <c>-Xmx4G "-Dfoo=bar baz" -XX:+UseG1GC</c> → three tokens.
    /// </summary>
    private static List<string> SplitArgs(string input)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return tokens;

        var span = input.AsSpan().Trim();
        int i = 0;
        while (i < span.Length)
        {
            // Skip whitespace
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            if (i >= span.Length) break;

            if (span[i] == '"')
            {
                // Quoted token — find closing quote
                i++; // skip opening quote
                int start = i;
                while (i < span.Length && span[i] != '"') i++;
                tokens.Add(span[start..i].ToString());
                if (i < span.Length) i++; // skip closing quote
            }
            else
            {
                // Unquoted token — read until whitespace
                int start = i;
                while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
                tokens.Add(span[start..i].ToString());
            }
        }

        return tokens;
    }
}
