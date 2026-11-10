using System.Net.Http;
using System.Text.Json;

namespace HalerithAddonClient.Core;

public static class SyncFailureCodes
{
    public const string AccessDenied = "file_access_denied";
    public const string FileIoFailed = "file_io_failed";
    public const string InvalidData = "invalid_data";
    public const string NetworkFailed = "network_failed";
    public const string TimedOut = "timed_out";
    public const string UnexpectedFailure = "unexpected_failure";

    public static string FromException(Exception exception) => exception switch
    {
        UnauthorizedAccessException => AccessDenied,
        TimeoutException or TaskCanceledException => TimedOut,
        HttpRequestException => NetworkFailed,
        IOException => FileIoFailed,
        InvalidOperationException or ArgumentException or JsonException => InvalidData,
        _ => UnexpectedFailure,
    };
}
