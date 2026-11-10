namespace HalerithAddonClient.Core;

/// <summary>A cancelled or closed prompt must never hand off to the installer.</summary>
public static class ClientUpgradeCountdown
{
    public static async Task<bool> RunAsync(
        Action<int> showSeconds,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        try
        {
            for (var seconds = 3; seconds > 0; seconds--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                showSeconds(seconds);
                await delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
