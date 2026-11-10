namespace HalerithAddonClient.Core;

public sealed class ImageCacheRecord
{
    public string CacheKey { get; set; } = "";
    public string ImageKind { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Url { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string LastSeenUtc { get; set; } = "";
    public string LastAccessedUtc { get; set; } = "";
    public string DownloadedAtUtc { get; set; } = "";
    public string ImageHash { get; set; } = "";
    public string FailedAtUtc { get; set; } = "";
    public int FailureCount { get; set; }
}
