namespace HalerithAddonClient.Core;

public static class GameStartupMonitor
{
    public static async Task<bool> WaitForConfirmationAsync(Task<bool> confirmation, TimeSpan noticeAfter, Action onSlowStartup)
    {
        if (await Task.WhenAny(confirmation, Task.Delay(noticeAfter)).ConfigureAwait(false) != confirmation)
            onSlowStartup();
        return await confirmation.ConfigureAwait(false);
    }
}
