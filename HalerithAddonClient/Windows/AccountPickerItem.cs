using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Windows;

internal sealed class AccountPickerItem : INotifyPropertyChanged
{
    private AccountPickerItem(MinecraftAccount? account, bool isAdd)
    {
        Account = account;
        IsAdd = isAdd;

        if (account is not null)
            ResolveHeadImage(account);
    }

    public MinecraftAccount? Account { get; }

    public bool IsAdd { get; }

    public bool IsAccount => Account is not null;

    public string DisplayName => Account is null ? "" : DemoMode.PlayerName(Account.Gamertag);

    public string Uuid => Account?.Uuid ?? "";

    public Bitmap? HeadImage { get; private set; }

    public bool HasHeadImage => HeadImage is not null;

    public static AccountPickerItem ForAccount(MinecraftAccount account) => new(account, isAdd: false);

    public static AccountPickerItem AddAccount() => new(null, isAdd: true);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void ResolveHeadImage(MinecraftAccount account)
    {
        var path = ImageCacheService.ResolveMinecraftHeadPath(
            account,
            downloadedPath => Dispatcher.UIThread.Post(() => LoadHeadImage(downloadedPath)));

        LoadHeadImage(path);
    }

    private void LoadHeadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            HeadImage = new Bitmap(stream);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeadImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHeadImage)));
        }
        catch
        {
            // Account heads are decorative; failed decode just leaves the text-only row.
        }
    }
}
