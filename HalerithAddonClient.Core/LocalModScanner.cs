namespace HalerithAddonClient.Core;
public static class LocalModScanner
{
    public static List<LocalModFile> Scan(string modsFolder)
    {
        // Guard against an unconfigured server. Without this, Directory.CreateDirectory
        // throws ArgumentException "Value cannot be an empty string. (Parameter 'path')"
        // which bubbles up to the UI footer unhelpfully. Raise a user-facing
        // ConfigurationIncompleteException instead so the caller can display a
        // message that tells the user exactly what to fix.
        if (string.IsNullOrWhiteSpace(modsFolder))
            throw new ConfigurationIncompleteException(
                "This server has no Mods folder set. Open its settings and choose a Game Data folder to populate the content paths.");

        // Only scan if the folder already exists — do NOT create it.
        // Instance folders for Default-mode servers are created at launch time,
        // not during manifest scanning.  Creating them eagerly would produce
        // empty instance directories for every server (including secondaries
        // and non-Default-mode servers).
        if (!Directory.Exists(modsFolder))
            return [];

        return Directory.EnumerateFiles(modsFolder, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(path => new LocalModFile { FileName = Path.GetFileName(path), FullPath = path, Stem = Path.GetFileNameWithoutExtension(path) })
            .OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
