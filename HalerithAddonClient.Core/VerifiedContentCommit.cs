namespace HalerithAddonClient.Core;

// Small filesystem seam for deterministic commit/rollback fault tests.
internal class ContentFileOperations
{
    internal virtual bool Exists(string path) => File.Exists(path);
    internal virtual void Move(string source, string target) => File.Move(source, target);
    internal virtual void Copy(string source, string target) => File.Copy(source, target, overwrite: false);
    internal virtual void Delete(string path) => File.Delete(path);
    internal virtual void Replace(string source, string target, string? backup) => File.Replace(source, target, backup, ignoreMetadataErrors: true);
}

internal static class VerifiedContentCommit
{
    internal static void Commit(string staged, string target, string? previous, string? backup,
        ContentFileOperations? operations = null, Action<string>? log = null)
    {
        var files = operations ?? new ContentFileOperations();
        target = Path.GetFullPath(target);
        staged = Path.GetFullPath(staged);
        previous = previous is null ? target : Path.GetFullPath(previous);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(staged), Path.GetDirectoryName(target), comparison)
            || !string.Equals(Path.GetDirectoryName(previous), Path.GetDirectoryName(target), comparison)
            || string.Equals(staged, target, comparison))
            throw new IOException("Content must be staged within its destination folder.");

        if (string.Equals(previous, target, comparison) || !files.Exists(previous))
        {
            // File.Replace keeps the destination intact on a failed atomic replacement.
            if (files.Exists(target)) files.Replace(staged, target, backup);
            else files.Move(staged, target);
            return;
        }

        // A version can also change filename. Keep the old version until the new
        // file is committed, and retain any pre-existing target for rollback.
        if (backup is not null) files.Copy(previous, backup);
        string? rollback = files.Exists(target) ? target + "." + Guid.NewGuid().ToString("N") + ".rollback" : null;
        bool installed = false;
        try
        {
            if (rollback is not null) files.Replace(staged, target, rollback);
            else files.Move(staged, target);
            installed = true;
            files.Delete(previous);
        }
        catch
        {
            if (installed)
            {
                try
                {
                    if (rollback is not null) files.Replace(rollback, target, null);
                    else files.Delete(target);
                }
                catch (Exception recoveryError)
                {
                    throw new IOException("Content replacement failed. The previous version and any rollback copy were retained; close Minecraft and retry.", recoveryError);
                }
            }
            throw;
        }
        if (rollback is not null)
        {
            try { files.Delete(rollback); }
            catch { log?.Invoke("The update succeeded, but a temporary rollback copy could not be removed."); }
        }
    }
}
