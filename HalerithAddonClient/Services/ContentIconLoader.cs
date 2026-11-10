using Avalonia.Media.Imaging;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Services;

/// <summary>Shares icon work without retaining every decoded image for the process lifetime.</summary>
public static class ContentIconLoader
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Slots = new(4);
    private static readonly Dictionary<string, WeakReference<Bitmap>> Images = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task<Bitmap?>> Pending = new(StringComparer.Ordinal);

    public static Task<Bitmap?> LoadAsync(string? source, string? sourceId, string? slug, string? type, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<Bitmap?>(null);
        var key = ImageCacheService.BuildContentIconCacheKey(source, sourceId, slug, type, url) + "\n" + url;
        return LoadAsync(key, () => ImageCacheService.ResolveContentIconPathAsync(source, sourceId, slug, type, url));
    }

    internal static Task<Bitmap?> LoadAsync(string key, Func<Task<string?>> resolvePath)
    {
        lock (Gate)
        {
            if (Images.TryGetValue(key, out var image) && image.TryGetTarget(out var bitmap))
                return Task.FromResult<Bitmap?>(bitmap);
            if (Pending.TryGetValue(key, out var pending)) return pending;
            // Task.Run is deliberate: even a cache hit performs synchronous SQLite and disk work.
            var task = Task.Run(async () =>
            {
                await Slots.WaitAsync().ConfigureAwait(false);
                try
                {
                    var path = await resolvePath().ConfigureAwait(false);
                    if (path is null) return null;
                    using var stream = File.OpenRead(path);
                    var loaded = Bitmap.DecodeToWidth(stream, 64);
                    lock (Gate)
                    {
                        if (Images.Count >= 512) Images.Remove(Images.Keys.First());
                        Images[key] = new WeakReference<Bitmap>(loaded);
                    }
                    return loaded;
                }
                catch { return null; } // An unavailable icon must not fail a content refresh.
                finally
                {
                    Slots.Release();
                    lock (Gate) Pending.Remove(key);
                }
            });
            Pending[key] = task;
            return task;
        }
    }
}
