using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Threading;

namespace HalerithAddonClient.ViewModels;

public sealed partial class MainViewModel
{
    // ══════════════════════════════════════════════════════════════════════
    //  Activity console
    // ══════════════════════════════════════════════════════════════════════

    private const int ConsoleMaxLines = 2000;
    private static readonly Regex LeadingTimestampRegex = new(@"^\[\d{2}:\d{2}:\d{2}\]\s*", RegexOptions.Compiled);

    /// <summary>
    /// Appends <paramref name="message"/> to <see cref="ConsoleLines"/>, capping
    /// the collection at <see cref="ConsoleMaxLines"/> by discarding the oldest entry.
    /// Always dispatches to the UI thread so the bound ItemsControl processes
    /// the CollectionChanged event safely — a plain lock is insufficient because
    /// Avalonia's container generator can race with cross-thread Add() calls.
    /// </summary>
    internal void AppendConsole(string message)
    {
        var timestamped = LeadingTimestampRegex.IsMatch(message)
            ? message
            : $"[{DateTime.Now:HH:mm:ss}] {message}";

        if (Dispatcher.UIThread.CheckAccess())
        {
            AppendConsoleDirect(timestamped);
        }
        else
        {
            Dispatcher.UIThread.Post(() => AppendConsoleDirect(timestamped));
        }
    }

    private void AppendConsoleDirect(string timestamped)
    {
        lock (_consoleLock)
        {
            if (ConsoleLines.Count >= ConsoleMaxLines)
                ConsoleLines.RemoveAt(0);
            ConsoleLines.Add(timestamped);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  INotifyPropertyChanged
    // ══════════════════════════════════════════════════════════════════════

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        if (string.Equals(name, nameof(IsActivityBarVisible), StringComparison.Ordinal))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActivityBarIndeterminate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivityBarProgress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIdleStatusSummaryVisible)));
        }
    }
}
