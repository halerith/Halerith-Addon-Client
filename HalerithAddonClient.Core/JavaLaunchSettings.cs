namespace HalerithAddonClient.Core;

public static class JavaLaunchSettings
{
    public const string ModeAuto = "Auto";
    public const string ModeManual = "Manual";
    public const string ModeCustom = "Custom";

    public const string GarbageCollectorZgc = "ZGC";
    public const string GarbageCollectorShenandoah = "ShenandoahGC";
    public const string GarbageCollectorG1 = "G1GC";

    public const int DefaultMaxMemoryGb = 4;
    public const int MinManualMaxMemoryGb = 2;
    public const int MaxManualMaxMemoryGb = 16;

    public const string DefaultCustomArgs = "-Xmx4G -XX:+UseZGC -XX:+UnlockExperimentalVMOptions";

    public static string NormalizeGarbageCollector(string? value)
        => value?.Trim() switch
        {
            GarbageCollectorShenandoah => GarbageCollectorShenandoah,
            GarbageCollectorG1 => GarbageCollectorG1,
            _ => GarbageCollectorZgc,
        };
}
