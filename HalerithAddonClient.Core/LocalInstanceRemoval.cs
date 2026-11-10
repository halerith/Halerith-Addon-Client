namespace HalerithAddonClient.Core;

public static class LocalInstanceRemoval
{
    public static void Delete(LocalInstance instance)
    {
        if (BuiltInGameSessionStore.ReadVerifiedFresh(BuiltInGameSessionTarget.LocalInstance(instance.Id)) is not null)
            throw new InvalidOperationException("Close Minecraft before deleting this instance.");
        DeleteFiles(Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime", "instances"), instance.Id, () =>
        {
            BuiltInGameSessionStore.Clear(BuiltInGameSessionTarget.LocalInstance(instance.Id));
            using var db = AppStorageDatabase.OpenConnection();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM local_instances WHERE id = $id";
            command.Parameters.AddWithValue("$id", instance.Id);
            command.ExecuteNonQuery();
        });
    }

    internal static void DeleteFiles(string instancesRoot, string id, Action removeRecord)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new InvalidOperationException("Invalid local instance identifier.");
        var root = Path.GetFullPath(instancesRoot);
        var target = Path.GetFullPath(Path.Combine(root, id));
        if (!string.Equals(Path.GetDirectoryName(target), root.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid local instance directory.");
        for (string? path = target; path is not null; path = Path.GetDirectoryName(path)) CheckLink(path);
        if (Directory.Exists(target))
        {
            // Refuse links rather than following them into another instance or external data.
            var pending = new Stack<string>(); pending.Push(target);
            while (pending.TryPop(out var directory))
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    CheckLink(path);
                    if (Directory.Exists(path)) pending.Push(path);
                }
            var metadata = Path.Combine(target, ".halerith"); Directory.CreateDirectory(metadata);
            using var lease = new FileStream(Path.Combine(metadata, "local-pack.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
            Directory.Delete(target, recursive: true);
            if (Directory.Exists(target)) throw new IOException("The instance folder could not be removed. Close Minecraft and retry.");
        }
        // Keep the record on file-removal failure, so the user can retry.
        removeRecord();
    }

    private static void CheckLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("This instance contains a linked file or folder. Remove the link before deleting the instance.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
