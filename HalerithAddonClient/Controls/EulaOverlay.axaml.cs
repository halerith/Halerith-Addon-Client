using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Controls;

/// <summary>
/// Full-window overlay that displays the EULA and blocks interaction until
/// the user accepts or declines.  Designed to be placed in a Grid on top of
/// the window's main content.
/// </summary>
public partial class EulaOverlay : UserControl
{
    private EulaDocument _document;
    private int _readRequest;
    /// <summary>Raised when the user clicks "I Accept".</summary>
    public event EventHandler? Accepted;

    /// <summary>Raised when the user clicks "Decline".</summary>
    public event EventHandler? Declined;

    /// <summary>Raised when the user clicks "Close" in read-only mode.</summary>
    public event EventHandler? Closed;

    public EulaOverlay()
        : this(EulaDocument.PackagedClient())
    {
    }

    public EulaOverlay(EulaDocument document)
    {
        _document = document;
        InitializeComponent();
        RemedyButton.IsVisible = document.MateriallyAdverse;
        VersionText.Text = $"EULA version {document.Version}";
        RenderEulaHtml(document.Html);
        if (!string.IsNullOrWhiteSpace(document.ChangeSummary)) EulaBody.Children.Insert(0, new TextBlock { Text = document.ChangeSummary, TextWrapping = TextWrapping.Wrap });

        // Enable Accept only after the user scrolls to the bottom.
        EulaScroller.ScrollChanged += OnScrollChanged;

        // If the EULA is short enough to not need scrolling, enable Accept
        // once the layout is complete and we can measure extents.
        EulaScroller.LayoutUpdated += OnScrollerLayoutUpdated;
    }

    /// <summary>
    /// Creates the overlay in read-only mode — shows a single "Close" button
    /// instead of Accept / Decline.  Use this when the EULA has already been
    /// accepted and the user just wants to review the text.
    /// </summary>
    public static EulaOverlay CreateReadOnly()
    {
        var overlay = new EulaOverlay(new EulaDocument("client", "", "", "End User License Agreement", "", "", false));
        overlay.AcceptDeclineFooter.IsVisible = false;
        overlay.CloseFooter.IsVisible = true;
        // No scroll-gating needed in read-only mode.
        overlay.EulaScroller.ScrollChanged -= overlay.OnScrollChanged;
        overlay.EulaScroller.LayoutUpdated -= overlay.OnScrollerLayoutUpdated;
        var history = EulaStore.GetAcceptanceHistory().Where(item => item.Product == "client").OrderByDescending(item => item.AcceptedAtUtc).ToList();
        overlay.VersionText.Text = "No verifiable acceptance history is available. A current agreement has not been substituted.";
        if (history.Count > 0)
        {
            overlay.AcceptedVersionSelector.IsVisible = true;
            overlay.AcceptedVersionSelector.ItemsSource = history.Select(item => $"Version {item.EulaVersion} — accepted {item.AcceptedAtUtc.LocalDateTime:g}").ToList();
            overlay.AcceptedVersionSelector.SelectionChanged += async (_, _) =>
            {
                var index = overlay.AcceptedVersionSelector.SelectedIndex;
                if (index >= 0 && index < history.Count) await overlay.LoadAcceptedDocumentAsync(history[index]);
            };
            overlay.AcceptedVersionSelector.SelectedIndex = 0;
        }
        return overlay;
    }

    private async Task LoadAcceptedDocumentAsync(EulaAcceptanceRecord acceptance)
    {
        var request = ++_readRequest;
        VersionText.Text = "Loading the accepted agreement...";
        EulaBody.Children.Clear();
        _document = _document with { Version = "", Html = "" };
        try
        {
            using var client = new HalerithEulaClient();
            var document = await client.ResolveAcceptedClientDocumentAsync(acceptance);
            if (request != _readRequest) return;
            if (document is null) throw new InvalidOperationException();
            _document = document;
            VersionText.Text = $"Accepted EULA {document.Version} · {acceptance.AcceptedAtUtc.LocalDateTime:g}";
            RenderEulaHtml(document.Html);
        }
        catch
        {
            if (request == _readRequest) VersionText.Text = "The exact accepted agreement is unavailable or could not be verified. No other version has been substituted. Please try again or contact support.";
        }
    }

    private void OnOpenCopyClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_document.Version)) OpenSupportUrl("https://halerith.com/products/addon/client/eula.php?version=" + Uri.EscapeDataString(_document.Version));
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        CheckScrolledToBottom();
    }

    private void OnScrollerLayoutUpdated(object? sender, EventArgs e)
    {
        // Only need to check once after first layout.
        CheckScrolledToBottom();
    }

    private void CheckScrolledToBottom()
    {
        var sv = EulaScroller;
        // Content fits without scrolling, or user scrolled within ~20px of the bottom.
        bool atBottom = sv.Extent.Height <= sv.Viewport.Height
                     || sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 20;

        if (atBottom && !AcceptButton.IsEnabled)
        {
            AcceptButton.IsEnabled = true;
            ScrollHintText.IsVisible = false;
            // Unhook — no need to keep checking once enabled.
            sv.ScrollChanged  -= OnScrollChanged;
            sv.LayoutUpdated  -= OnScrollerLayoutUpdated;
        }
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        Accepted?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeclineClick(object? sender, RoutedEventArgs e)
    {
        Declined?.Invoke(this, EventArgs.Empty);
    }

    private async void OnRemedyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner) await new Windows.AgreementRemedyWindow(_document).ShowDialog(owner);
    }

    private void OpenSupportUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { VersionText.Text = "Please open halerith.com in your browser for billing or privacy help."; }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Parses the simple HTML from <see cref="EulaContent.Html"/> and renders
    /// it as Avalonia controls (TextBlocks, bullet lists, etc.).
    /// Only supports: h2, h3, p, strong, em, ul/li.
    /// </summary>
    private void RenderEulaHtml(string html)
    {
        var body = EulaBody;
        body.Children.Clear();

        // Resolve theme brushes (fall back to hard-coded colours).
        var textPrimary   = FindBrush("TextPrimaryBrush",   "#E1E4ED");
        var textSecondary = FindBrush("TextSecondaryBrush", "#8B90A0");
        var accentBrush   = FindBrush("AccentBrush",        "#6366F1");

        // Strip outer whitespace and split into block-level elements.
        html = html.Trim();

        // Process block elements in order.
        var blockPattern = new Regex(
            @"<(h[23]|p|ul)>(.*?)</\1>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match block in blockPattern.Matches(html))
        {
            string tag     = block.Groups[1].Value.ToLowerInvariant();
            string content = block.Groups[2].Value.Trim();

            switch (tag)
            {
                case "h2":
                    body.Children.Add(CreateTextBlock(
                        StripInlineTags(content), 20, FontWeight.Bold, textPrimary, 16, 8));
                    break;

                case "h3":
                    body.Children.Add(CreateTextBlock(
                        StripInlineTags(content), 15, FontWeight.SemiBold, textPrimary, 14, 4));
                    break;

                case "p":
                    body.Children.Add(CreateRichTextBlock(content, 13, textPrimary));
                    break;

                case "ul":
                    RenderBulletList(body, content, textPrimary);
                    break;
            }
        }
    }

    /// <summary>Creates a simple single-style TextBlock.</summary>
    private static TextBlock CreateTextBlock(
        string text, double fontSize, FontWeight weight,
        IBrush foreground, double marginTop = 0, double marginBottom = 0)
    {
        return new TextBlock
        {
            Text         = DecodeEntities(text),
            FontSize     = fontSize,
            FontWeight   = weight,
            Foreground   = foreground,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, marginTop, 0, marginBottom),
        };
    }

    /// <summary>
    /// Creates a TextBlock with inline runs for &lt;strong&gt; and &lt;em&gt; tags.
    /// </summary>
    private static TextBlock CreateRichTextBlock(string html, double fontSize, IBrush foreground)
    {
        var tb = new TextBlock
        {
            FontSize     = fontSize,
            Foreground   = foreground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = fontSize * 1.6,
            Margin       = new Thickness(0, 0, 0, 4),
        };

        // Split by <strong> and <em> tags, preserving the tags as delimiters.
        var parts = Regex.Split(html,
            @"(<a\b[^>]*>.*?</a>|</?(?:strong|em|b|i)>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        bool bold   = false;
        bool italic = false;

        foreach (var part in parts)
        {
            var link = Regex.Match(part, "^<a\\b[^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (link.Success)
            {
                var url = System.Net.WebUtility.HtmlDecode(link.Groups[1].Value);
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    ((uri.Scheme == "https" && uri.Host == "halerith.com") || uri.Scheme == "mailto"))
                {
                    var button = new Button
                    {
                        Content = DecodeEntities(StripInlineTags(link.Groups[2].Value)),
                        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                        Padding = new Thickness(0), Foreground = foreground, FontSize = fontSize,
                    };
                    ToolTip.SetTip(button, url);
                    button.Click += (_, _) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                        catch { button.Content = url; }
                    };
                    tb.Inlines!.Add(new InlineUIContainer { Child = button });
                    continue;
                }
            }
            var lower = part.ToLowerInvariant();
            switch (lower)
            {
                case "<strong>": case "<b>":  bold   = true;  continue;
                case "</strong>": case "</b>": bold   = false; continue;
                case "<em>": case "<i>":      italic = true;  continue;
                case "</em>": case "</i>":     italic = false; continue;
            }

            if (string.IsNullOrEmpty(part)) continue;

            var run = new Run(DecodeEntities(StripInlineTags(part)))
            {
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle  = italic ? FontStyle.Italic : FontStyle.Normal,
            };
            tb.Inlines!.Add(run);
        }

        return tb;
    }

    /// <summary>Renders &lt;li&gt; items as indented bullet paragraphs.</summary>
    private static void RenderBulletList(StackPanel parent, string ulContent, IBrush foreground)
    {
        var liPattern = new Regex(@"<li>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match li in liPattern.Matches(ulContent))
        {
            // Use a DockPanel so the bullet is fixed-width and the text
            // fills the remaining space and wraps correctly.
            var panel = new DockPanel
            {
                Margin = new Thickness(16, 2, 0, 2),
            };

            var bullet = new TextBlock
            {
                Text       = "\u2022",
                FontSize   = 13,
                Foreground = foreground,
                Margin     = new Thickness(0, 0, 8, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            };
            DockPanel.SetDock(bullet, Avalonia.Controls.Dock.Left);
            panel.Children.Add(bullet);

            var textBlock = CreateRichTextBlock(li.Groups[1].Value.Trim(), 13, foreground);
            panel.Children.Add(textBlock);

            parent.Children.Add(panel);
        }
    }

    /// <summary>Strips HTML tags, keeping only text content.</summary>
    private static string StripInlineTags(string html) =>
        Regex.Replace(html, @"<[^>]+>", "");

    /// <summary>Decodes common HTML entities.</summary>
    private static string DecodeEntities(string text) =>
        text.Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&ldquo;", "\u201C")
            .Replace("&rdquo;", "\u201D")
            .Replace("&mdash;", "-")
            .Replace("&lsquo;", "\u2018")
            .Replace("&rsquo;", "\u2019")
            .Replace("&nbsp;", " ")
            .Replace("&#x2019;", "\u2019")
            .Replace("&#x201C;", "\u201C")
            .Replace("&#x201D;", "\u201D");

    /// <summary>Tries to resolve a named brush from the app's resources.</summary>
    private IBrush FindBrush(string resourceKey, string fallbackHex)
    {
        if (this.TryFindResource(resourceKey, out var resource) && resource is IBrush brush)
            return brush;
        return SolidColorBrush.Parse(fallbackHex);
    }
}
