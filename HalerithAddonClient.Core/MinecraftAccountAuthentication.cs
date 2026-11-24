namespace HalerithAddonClient.Core;

public sealed class MinecraftSignInRequiredException : Exception
{
    public MinecraftSignInRequiredException() : base("Minecraft sign-in is required.") { }
}

public enum MinecraftAuthenticationResult
{
    Ready,
    SignInRequired,
    RetryLater,
}

/// <summary>Renews the selected Minecraft account before managed API requests.</summary>
public static class MinecraftAccountAuthentication
{
    public static async Task<MinecraftAuthenticationResult> EnsureReadyAsync(
        string uuid, CancellationToken cancellationToken = default)
    {
        var account = AccountStore.LoadAll().FirstOrDefault(a =>
            string.Equals(MinecraftProfileClient.NormalizeUuid(a.Uuid),
                MinecraftProfileClient.NormalizeUuid(uuid), StringComparison.OrdinalIgnoreCase));
        if (account is null || account.RequiresSignIn)
            return MinecraftAuthenticationResult.SignInRequired;

        if (!account.IsExpired && !string.IsNullOrWhiteSpace(account.McAccessToken))
            return MinecraftAuthenticationResult.Ready;

        if (string.IsNullOrWhiteSpace(account.MsRefreshToken))
        {
            AccountStore.SetSignInRequired(account.Uuid, true);
            return MinecraftAuthenticationResult.SignInRequired;
        }

        try
        {
            using var auth = new MicrosoftAuthService();
            var refreshed = await auth.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
            refreshed.RequiresSignIn = false;
            AccountStore.Save(refreshed);
            return MinecraftAuthenticationResult.Ready;
        }
        catch (MinecraftSignInRequiredException)
        {
            AccountStore.SetSignInRequired(account.Uuid, true);
            return MinecraftAuthenticationResult.SignInRequired;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A network or provider failure is not evidence that credentials were revoked.
            return MinecraftAuthenticationResult.RetryLater;
        }
    }
}
