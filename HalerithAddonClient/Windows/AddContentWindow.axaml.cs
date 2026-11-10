using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.ContentSearch;

namespace HalerithAddonClient.Windows;

/// <summary>
/// Window for searching Modrinth / CurseForge and adding content (mods, resource packs, shaders)
/// to a local instance.
/// </summary>
public partial class AddContentWindow : Window
{
    // ── Static palette (mirrors DarkTheme.axaml) ──────────────────────────
    private static readonly ISolidColorBrush AccentBlueBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly ISolidColorBrush SurfaceBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x27));
    private static readonly ISolidColorBrush SurfaceHoverBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x35));
    private static readonly ISolidColorBrush CardBorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x31, 0x49));
    private static readonly ISolidColorBrush TextPrimaryBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly ISolidColorBrush TextSecondaryBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly ISolidColorBrush TextMutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly ISolidColorBrush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly ISolidColorBrush SuccessBgBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x3D, 0x2E));
    private static readonly HttpClient IconPreviewHttp = CreateIconPreviewHttpClient();

    // ── Instance context ──────────────────────────────────────────────────
    private readonly string _instanceId;
    private readonly string _instanceDirectory;
    private readonly string _mcVersion;
    private readonly string _loader;       // lowercase: "fabric", "forge", etc. or ""

    // ── Services ──────────────────────────────────────────────────────────
    private readonly ModrinthSearchService _modrinthService = new();
    private readonly CurseForgeSearchService _curseForgeService = new();
    private IContentSearchService _activeService;

    // ── State ─────────────────────────────────────────────────────────────
    private string _activeSource = "modrinth";
    private string _activeContentType = ContentTypes.Mod;
    private HashSet<string> _installedKeys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _debounceTimer;

    public AddContentWindow()
    {
        // Design-time only
        InitializeComponent();
        _instanceId = "";
        _instanceDirectory = "";
        _mcVersion = "";
        _loader = "";
        _activeService = _modrinthService;
    }

    public AddContentWindow(string instanceId, string instanceDirectory, string mcVersion, string loader)
    {
        InitializeComponent();

        _instanceId = instanceId;
        _instanceDirectory = instanceDirectory;
        _mcVersion = mcVersion;
        _loader = loader;
        _activeService = _modrinthService;

        // Set badges
        _mcVersionBadge.Text = $"MC {_mcVersion}";

        if (!string.IsNullOrWhiteSpace(_loader))
        {
            _loaderBadge.IsVisible = true;
            _loaderBadgeText.Text = char.ToUpperInvariant(_loader[0]) + _loader[1..];
        }

        // Set initial tab state
        _curseForgeTabButton.IsVisible = _curseForgeService.IsAvailable;
        SetActiveTab("modrinth");

        // Wire up search debounce
        _searchTextBox.TextChanged += SearchTextBox_TextChanged;

        // Load installed items on open
        Opened += async (_, _) => await LoadInstalledKeysAsync();
    }

    // ── Tab switching ─────────────────────────────────────────────────────

    private void ModrinthTabButton_Click(object? sender, RoutedEventArgs e)
        => SetActiveTab("modrinth");

    private void CurseForgeTabButton_Click(object? sender, RoutedEventArgs e)
        => SetActiveTab("curseforge");

    private void SetActiveTab(string source)
    {
        _activeSource = source;
        _activeService = source == "modrinth"
            ? _modrinthService
            : _curseForgeService;

        // Update button styles
        _modrinthTabButton.Classes.Clear();
        _curseForgeTabButton.Classes.Clear();
        _modrinthTabButton.Classes.Add(source == "modrinth" ? "source-active" : "source-inactive");
        _curseForgeTabButton.Classes.Add(source == "curseforge" ? "source-active" : "source-inactive");

        if (!_activeService.IsAvailable)
        {
            _statusText.Text = $"{_activeService.SourceName} is not configured (API key missing)";
            _resultsPanel.Children.Clear();
            return;
        }

        _statusText.Text = "";
        TriggerSearch();
    }

    // ── Content type change ───────────────────────────────────────────────

    private void ContentTypeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Guard: fires during InitializeComponent before fields are initialized
        if (_activeService is null || _contentTypeComboBox is null) return;

        if (_contentTypeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _activeContentType = tag;
            TriggerSearch();
        }
    }

    // ── Search ────────────────────────────────────────────────────────────

    private void SearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        // Debounce: restart timer on each keystroke
        _debounceTimer?.Stop();
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            TriggerSearch();
        };
        _debounceTimer.Start();
    }

    private void TriggerSearch()
    {
        var query = _searchTextBox?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            _resultsPanel.Children.Clear();
            _statusText.Text = "";
            return;
        }

        _ = PerformSearchAsync(query);
    }

    private async Task PerformSearchAsync(string query)
    {
        // Cancel any in-flight search
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        _statusText.Text = "Searching…";

        try
        {
            // Only apply loader filter for mods
            var loader = _activeContentType == ContentTypes.Mod ? _loader : null;

            var results = await _activeService.SearchAsync(
                query,
                _activeContentType,
                _mcVersion,
                loader,
                cts.Token);

            if (cts.Token.IsCancellationRequested) return;

            _statusText.Text = results.Count == 0 ? "No results" : $"{results.Count} results";

            // Build card controls
            _resultsPanel.Children.Clear();
            foreach (var r in results)
            {
                var card = BuildResultCard(r);
                card.Width = 280;
                card.Margin = new Thickness(0, 0, 12, 12);
                _resultsPanel.Children.Add(card);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a new search supersedes this one
        }
        catch (Exception ex)
        {
            if (!cts.Token.IsCancellationRequested)
                _statusText.Text = $"Error: {ex.Message}";
        }
    }

    // ── Installed keys ────────────────────────────────────────────────────

    private async Task LoadInstalledKeysAsync()
    {
        try
        {
            _installedKeys = LocalInstanceContentStore.GetInstalledKeys(_instanceId);
            await Task.CompletedTask;
        }
        catch
        {
            _installedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private bool IsInstalled(ContentSearchResult result)
        => _installedKeys.Contains($"{result.Source}:{result.Id}");

    // ── Card building ─────────────────────────────────────────────────────

    private Control BuildResultCard(ContentSearchResult result)
    {
        var isInstalled = IsInstalled(result);

        // Card container
        var card = new Border
        {
            Background = SurfaceBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
        };

        var grid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("*,Auto"),
        };

        // ── Top: icon + info ──
        var topGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
        };

        // Icon
        var iconBorder = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x17)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (!string.IsNullOrWhiteSpace(result.IconUrl))
        {
            var image = new Image
            {
                Width = 40,
                Height = 40,
                Stretch = Stretch.UniformToFill,
            };
            _ = LoadIconAsync(image, result.IconUrl);
            iconBorder.Child = image;
        }

        Grid.SetColumn(iconBorder, 0);
        topGrid.Children.Add(iconBorder);

        // Info stack
        var infoStack = new StackPanel { Spacing = 2 };

        // Name with link icon
        var namePanel = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
        };

        var nameText = new TextBlock
        {
            Text = result.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimaryBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
        };
        Grid.SetColumn(nameText, 0);
        namePanel.Children.Add(nameText);

        // External link button
        if (!string.IsNullOrWhiteSpace(result.SourceUrl))
        {
            var linkButton = new Button
            {
                Content = "↗",
                FontSize = 10,
                Padding = new Thickness(2),
                Background = Brushes.Transparent,
                Foreground = TextMutedBrush,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            linkButton.Click += (_, _) => OpenUrl(result.SourceUrl);
            Grid.SetColumn(linkButton, 1);
            namePanel.Children.Add(linkButton);
        }
        infoStack.Children.Add(namePanel);

        // Description (truncated)
        var desc = result.Description;
        if (desc.Length > 80) desc = desc[..77] + "...";
        infoStack.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 11,
            Foreground = TextSecondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 32,
        });

        // Download count
        infoStack.Children.Add(new TextBlock
        {
            Text = FormatDownloads(result.Downloads) + " downloads",
            FontSize = 11,
            Foreground = TextMutedBrush,
            Margin = new Thickness(0, 2, 0, 0),
        });

        Grid.SetColumn(infoStack, 1);
        topGrid.Children.Add(infoStack);

        Grid.SetRow(topGrid, 0);
        grid.Children.Add(topGrid);

        // ── Bottom: Add button or Installed badge ──
        if (isInstalled)
        {
            var installedBorder = new Border
            {
                Background = SuccessBgBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 10, 0, 0),
            };
            installedBorder.Child = new TextBlock
            {
                Text = "✓ Installed",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = SuccessBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetRow(installedBorder, 1);
            grid.Children.Add(installedBorder);
        }
        else
        {
            var addButton = new Button
            {
                Content = "Add",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Height = 33,
                Background = AccentBlueBrush,
                Foreground = Brushes.White,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 10, 0, 0),
            };
            addButton.Click += async (_, _) => await OnAddClickedAsync(result, addButton);
            Grid.SetRow(addButton, 1);
            grid.Children.Add(addButton);
        }

        card.Child = grid;
        return card;
    }

    // ── Add button handler → version picker ───────────────────────────────

    private async Task OnAddClickedAsync(ContentSearchResult result, Button addButton)
    {
        addButton.IsEnabled = false;
        addButton.Content = "Loading…";

        try
        {
            // Only apply loader filter for mods
            var loader = _activeContentType == ContentTypes.Mod ? _loader : null;

            // Fetch versions
            var versions = await _activeService.GetVersionsAsync(
                result.Id,
                _mcVersion,
                loader);

            if (versions.Count == 0)
            {
                addButton.Content = "No versions";
                await Task.Delay(1500);
                addButton.Content = "Add";
                addButton.IsEnabled = true;
                return;
            }

            // Show version picker dialog
            var selectedVersion = await ShowVersionPickerAsync(result.Name, versions);

            if (selectedVersion == null)
            {
                // User cancelled
                addButton.Content = "Add";
                addButton.IsEnabled = true;
                return;
            }

            // Download
            addButton.Content = "Downloading…";
            await ContentDownloader.DownloadAndTrackAsync(
                _instanceId,
                _instanceDirectory,
                result,
                selectedVersion);

            // Mark as installed
            _installedKeys.Add($"{result.Source}:{result.Id}");

            // Replace the Add button with Installed badge
            if (addButton.Parent is Grid parentGrid)
            {
                var row = Grid.GetRow(addButton);
                parentGrid.Children.Remove(addButton);

                var installedBorder = new Border
                {
                    Background = SuccessBgBrush,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(0, 6),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 10, 0, 0),
                };
                installedBorder.Child = new TextBlock
                {
                    Text = "✓ Installed",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = SuccessBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                Grid.SetRow(installedBorder, row);
                parentGrid.Children.Add(installedBorder);
            }
        }
        catch (Exception ex)
        {
            addButton.Content = "Error";
            _statusText.Text = $"Failed: {ex.Message}";
            await Task.Delay(2000);
            addButton.Content = "Add";
            addButton.IsEnabled = true;
        }
    }

    // ── Version picker dialog ─────────────────────────────────────────────

    private async Task<ContentVersionResult?> ShowVersionPickerAsync(
        string projectName,
        List<ContentVersionResult> versions)
    {
        ContentVersionResult? selected = null;

        var dialog = new Window
        {
            Title = "Choose Version",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            MinWidth = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SystemDecorations = SystemDecorations.BorderOnly,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x27)),
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Spacing = 14,
        };

        // Title
        stack.Children.Add(new TextBlock
        {
            Text = "Choose Version",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimaryBrush,
        });

        // Project name
        stack.Children.Add(new TextBlock
        {
            Text = projectName,
            FontSize = 13,
            Foreground = TextSecondaryBrush,
        });

        // Version ComboBox
        var versionCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 13,
        };

        foreach (var v in versions)
        {
            versionCombo.Items.Add(new ComboBoxItem
            {
                Content = v.DisplayLabel,
                Tag = v,
            });
        }

        if (versionCombo.Items.Count > 0)
            versionCombo.SelectedIndex = 0;

        stack.Children.Add(versionCombo);

        // Buttons
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            MinWidth = 81,
            Height = 33,
            FontSize = 13,
            Background = Brushes.Transparent,
            Foreground = TextSecondaryBrush,
        };
        cancelBtn.Click += (_, _) =>
        {
            selected = null;
            dialog.Close();
        };

        var addBtn = new Button
        {
            Content = "Add",
            MinWidth = 81,
            Height = 33,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Background = AccentBlueBrush,
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(6),
        };
        addBtn.Click += (_, _) =>
        {
            if (versionCombo.SelectedItem is ComboBoxItem item && item.Tag is ContentVersionResult v)
                selected = v;
            dialog.Close();
        };

        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(addBtn);
        stack.Children.Add(buttonPanel);

        dialog.Content = stack;
        await dialog.ShowDialog(this);

        return selected;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static HttpClient CreateIconPreviewHttpClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.UserAgent.TryParseAdd("HalerithAddonClient/1.0");
        return http;
    }

    private static async Task LoadIconAsync(Image image, string url)
    {
        const long maxIconBytes = 100L * 1024L;

        try
        {
            var uri = HttpsUrlPolicy.RequireHttpsAbsoluteUri(url, "Image URL");
            using var response = await IconPreviewHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > maxIconBytes)
                return;

            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var memory = new MemoryStream();
            var buffer = new byte[81920];
            long totalBytes = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    break;

                totalBytes += read;
                if (totalBytes > maxIconBytes)
                    return;

                await memory.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }

            var data = memory.ToArray();
            Dispatcher.UIThread.Post(() => LoadIconBytes(image, data));
        }
        catch
        {
            // Icon load failure is non-critical.
        }
    }

    private static void LoadIconBytes(Image image, byte[] data)
    {
        if (data.Length == 0)
            return;

        try
        {
            using var stream = new MemoryStream(data);
            image.Source = new Bitmap(stream);
        }
        catch
        {
            // Icon load failure is non-critical.
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            var uri = HttpsUrlPolicy.RequireHttpOrHttpsAbsoluteUri(url, "Project URL");
            var psi = new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* Non-critical */ }
    }

    private static string FormatDownloads(long count)
    {
        return count switch
        {
            >= 1_000_000_000 => $"{count / 1_000_000_000.0:F1}B",
            >= 1_000_000 => $"{count / 1_000_000.0:F1}M",
            >= 1_000 => $"{count / 1_000.0:F0}K",
            _ => count.ToString("N0")
        };
    }
}
