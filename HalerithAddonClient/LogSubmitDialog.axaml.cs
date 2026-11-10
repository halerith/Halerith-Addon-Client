using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Diagnostics;
using System.IO;
using HalerithAddonClient.Core;

namespace HalerithAddonClient;

/// <summary>
/// Modal dialog that gathers optional notes and an optional attachment before
/// the user submits their logs. Call <see cref="ShowDialog"/> on an instance;
/// if the result is <c>true</c> read <see cref="Notes"/> and
/// <see cref="AttachmentPath"/> before uploading.
/// </summary>
public partial class LogSubmitDialog : Window
{
    private const long MaxAttachmentBytes = 1 * 1024 * 1024; // 1 MB

    /// <summary>Free-text notes the user typed (may be empty).</summary>
    public string Notes { get; private set; } = string.Empty;

    /// <summary>
    /// Fully-qualified path to the selected attachment, or <c>null</c> if
    /// none was chosen.
    /// </summary>
    public string? AttachmentPath { get; private set; }
    public string? GameLogDirectory { get; private set; }

    public LogSubmitDialog(IReadOnlyList<GameLogInstanceOption> instances, string? preferredInstanceId = null)
    {
        InitializeComponent();
        foreach (var instance in instances)
            _gameInstanceComboBox.Items.Add(instance);
        _gameInstanceComboBox.SelectedItem = instances.FirstOrDefault(i => i.Id == preferredInstanceId)
            ?? instances.FirstOrDefault();
        if (instances.Count == 0)
        {
            _includeGameLogsCheckBox.IsEnabled = false;
            _gameLogsNote.Text = "No installed Minecraft instances are available for log collection.";
        }
    }

    private void GameLogsSelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (_gameInstanceComboBox is not null)
            _gameInstanceComboBox.IsEnabled = _includeGameLogsCheckBox?.IsChecked == true;
    }

    private void PrivacyPolicyLink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "https://halerith.com/privacy/", UseShellExecute = true }); }
        catch { }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select attachment (max 1 MB)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }, FilePickerFileTypes.All }
        });

        if (files.Count == 0)
            return;

        try
        {
            var filePath = files[0].Path.LocalPath;
            var info = new FileInfo(filePath);

            if (info.Length > MaxAttachmentBytes)
            {
                _attachmentPathBox.Text = string.Empty;
                _attachmentErrorText.Text = $"File is too large ({info.Length / 1024.0:F0} KB). Maximum is 1 MB.";
                _attachmentErrorText.IsVisible = true;
                AttachmentPath = null;
                return;
            }

            _attachmentPathBox.Text = filePath;
            _attachmentErrorText.IsVisible = false;
            AttachmentPath = filePath;
        }
        catch (Exception ex)
        {
            _attachmentErrorText.Text = $"Could not read file: {ex.Message}";
            _attachmentErrorText.IsVisible = true;
        }
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        // Re-validate attachment size in case the file changed between selection and submit.
        if (AttachmentPath is not null)
        {
            try
            {
                var info = new FileInfo(AttachmentPath);
                if (info.Length > MaxAttachmentBytes)
                {
                    _attachmentErrorText.Text = $"File is too large ({info.Length / 1024.0:F0} KB). Maximum is 1 MB.";
                    _attachmentErrorText.IsVisible = true;
                    return;
                }
            }
            catch
            {
                _attachmentErrorText.Text = "Could not read the selected file. Please choose a different file.";
                _attachmentErrorText.IsVisible = true;
                return;
            }
        }

        Notes = _notesBox.Text?.Trim() ?? string.Empty;
        GameLogDirectory = _includeGameLogsCheckBox.IsChecked == true
            ? (_gameInstanceComboBox.SelectedItem as GameLogInstanceOption)?.Directory
            : null;
        Close(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close(false);
    }
}

public sealed record GameLogInstanceOption(string Id, string Name, string Directory)
{
    public override string ToString() => Name;
}
