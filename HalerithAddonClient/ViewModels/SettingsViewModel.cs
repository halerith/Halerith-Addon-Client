using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.ViewModels;

/// <summary>Working application settings. Managed access is displayed per Minecraft account by the window.</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public SettingsViewModel(AppConfig config)
    {
        Config = config;
        SaveCommand = new DelegateCommand(Save);
        CancelCommand = new DelegateCommand(() => RequestCancel?.Invoke());
    }

    public AppConfig Config { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public Action? SaveAdditionalSettings { get; set; }
    public event Action? RequestSave;
    public event Action? RequestCancel;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string ServiceTrustDiagnosticsText =>
        "Managed server access is checked for the selected Minecraft account by the Halerith Addon platform. " +
        "Local instances and public provider content are available without a Halerith subscription.";

    private void Save()
    {
        AppConfigStore.SaveAppConfig(Config);
        SaveAdditionalSettings?.Invoke();
        FileLogger.SetEnabled(Config.EnableEventLogging);
        LauncherLog.Configure(Config.EnableEventLogging);
        RequestSave?.Invoke();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
