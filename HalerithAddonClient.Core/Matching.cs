using System.Text.RegularExpressions;

namespace HalerithAddonClient.Core;

public static class Matching
{
    public static string NormalizeStem(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value).ToLowerInvariant();
        stem = Regex.Replace(stem, "[^a-z0-9]+", "-");
        return stem.Trim('-');
    }

    public static string[] Tokenize(string value) =>
        NormalizeStem(value).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool TokensMatchContiguous(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
            return false;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (!string.Equals(haystack[i + j], needle[j], StringComparison.OrdinalIgnoreCase))
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
                return true;
        }

        return false;
    }

    public static bool TokensMatchAtStart(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
            return false;

        for (var i = 0; i < needle.Length; i++)
        {
            if (!string.Equals(haystack[i], needle[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public static int Score(ManifestMod mod, LocalModFile file)
    {
        if (string.Equals(mod.DownloadFilename, file.FileName, StringComparison.OrdinalIgnoreCase))
            return 1000;

        if (!string.IsNullOrWhiteSpace(file.SourceId) &&
            !string.IsNullOrWhiteSpace(mod.SourceId) &&
            string.Equals(file.SourceId, mod.SourceId, StringComparison.OrdinalIgnoreCase))
            return 990;

        if (!string.IsNullOrWhiteSpace(file.Slug) &&
            !string.IsNullOrWhiteSpace(mod.Slug) &&
            string.Equals(file.Slug, mod.Slug, StringComparison.OrdinalIgnoreCase))
            return 985;

        // Alternate-version filenames are known previous releases of this exact item.
        // Match these before aliases — an exact historical filename is more reliable
        // than any pattern-based alias heuristic.
        foreach (var alt in mod.AlternateVersions)
        {
            if (!string.IsNullOrWhiteSpace(alt.Filename) &&
                string.Equals(alt.Filename, file.FileName, StringComparison.OrdinalIgnoreCase))
                return 975;
        }

        var fileTokens = Tokenize(file.Stem);

        foreach (var alias in mod.Aliases)
        {
            var aliasType = alias.Type?.Trim() ?? "";
            var aliasValue = alias.Value?.Trim() ?? "";
            if (aliasValue.Length == 0)
                continue;

            if (aliasType.Equals("filename", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(aliasValue, file.FileName, StringComparison.OrdinalIgnoreCase))
                return 950;

            if (aliasType.Equals("regex", StringComparison.OrdinalIgnoreCase))
            {
                // Bare pattern stored without delimiters (same convention as the PHP server).
                // Match against the full filename (including extension) case-insensitively.
                try
                {
                    if (Regex.IsMatch(file.FileName, aliasValue, RegexOptions.IgnoreCase))
                        return 925;
                }
                catch (RegexParseException)
                {
                    // Invalid pattern — skip rather than crash.
                }
            }

            if (aliasType.Equals("filename_prefix", StringComparison.OrdinalIgnoreCase) &&
                file.FileName.StartsWith(aliasValue, StringComparison.OrdinalIgnoreCase))
                return 900;

            // For slug/display/manual/project aliases, use tokenized contiguous matching.
            // Do NOT use raw substring matching because short aliases like "sodium"
            // incorrectly match unrelated mods such as sodium-extra.
            if (aliasType.Equals("project_slug", StringComparison.OrdinalIgnoreCase) ||
                aliasType.Equals("display", StringComparison.OrdinalIgnoreCase) ||
                aliasType.Equals("manual", StringComparison.OrdinalIgnoreCase))
            {
                if (TokensMatchContiguous(fileTokens, Tokenize(aliasValue)))
                    return 800;
            }
        }

        if (!string.IsNullOrWhiteSpace(mod.Slug) &&
            TokensMatchContiguous(fileTokens, Tokenize(mod.Slug)))
            return 700;

        if (TokensMatchContiguous(fileTokens, Tokenize(mod.Name)))
            return 600;

        return 0;
    }
}
