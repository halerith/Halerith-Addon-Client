using System.Diagnostics;
using System.Net;
using Avalonia.Threading;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    private void StartBuiltInGameSessionMonitor(
        BuiltInGameSessionTarget target,
        Process process,
        string processOutputLogPath)
    {
        process.EnableRaisingEvents = true;
        TrustBuiltInSession(target, process);

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => ready.TrySetResult(false);
        if (process.HasExited) ready.TrySetResult(false);

        _ = Task.Run(async () =>
        {
            try
            {
                var writerLock = new SemaphoreSlim(1, 1);
                await using var writer = new StreamWriter(processOutputLogPath, append: false);

                var stdoutTask = MonitorGameStreamAsync(process.StandardOutput, writer, writerLock, ready);
                var stderrTask = MonitorGameStreamAsync(process.StandardError, writer, writerLock, ready);

                // A timeout never becomes proof of readiness. Continue watching
                // output so a delayed startup can still be confirmed.
                var confirmed = await GameStartupMonitor.WaitForConfirmationAsync(ready.Task, TimeSpan.FromSeconds(45),
                    () => Dispatcher.UIThread.Post(() => _vm.AppendConsole("Minecraft is still starting. Waiting for startup confirmation."))).ConfigureAwait(false);
                if (confirmed && !process.HasExited)
                {
                    BuiltInGameSessionStore.MarkRunning(target);
                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateLaunchButtonState();
                        UpdateLocalLaunchButtonState();
                    });

                    Dispatcher.UIThread.Post(() => _vm.AppendConsole("Game startup detected."));
                }

                await process.WaitForExitAsync().ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => _vm.AppendConsole(!confirmed || process.ExitCode != 0
                    ? "Minecraft exited before startup completed or reported a failure. Check the game logs."
                    : "Minecraft closed."));

                try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
                catch { }
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _vm.AppendConsole($"Game process monitor failed: {ex.Message}"));
            }
            finally
            {
                var record = BuiltInGameSessionStore.Read(target);
                if (record?.Pid == process.Id)
                    BuiltInGameSessionStore.Clear(target);
                UntrustBuiltInSession(target);

                Dispatcher.UIThread.Post(() =>
                {
                    _vm.RefreshLaunchState();
                    UpdateLaunchButtonState();
                    UpdateLocalLaunchButtonState();
                });
            }
        });
    }

    private async Task MonitorGameStreamAsync(
        StreamReader reader,
        StreamWriter writer,
        SemaphoreSlim writerLock,
        TaskCompletionSource<bool> ready)
    {
        var consoleFormatter = new GameConsoleLineFormatter();

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            await writerLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                writerLock.Release();
            }

            var consoleLine = consoleFormatter.Format(line);
            if (!string.IsNullOrWhiteSpace(consoleLine))
                Dispatcher.UIThread.Post(() => _vm.AppendConsole(consoleLine));

            if (line.Contains("Sound engine started", StringComparison.OrdinalIgnoreCase))
                ready.TrySetResult(true);
        }
    }

    private sealed class GameConsoleLineFormatter
    {
        private string? _insideLog4jElement;
        private string? _level;
        private string? _source;

        public string? Format(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            var trimmed = line.Trim();

            if (trimmed.StartsWith("<log4j:Event", StringComparison.OrdinalIgnoreCase))
                return StartLog4jEvent(line);

            if (_insideLog4jElement is not null)
                return FormatLog4jElementContent(line, includePrefix: false);

            if (trimmed.StartsWith("<log4j:Message", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<log4j:Throwable", StringComparison.OrdinalIgnoreCase))
            {
                return StartLog4jElement(line, includePrefix: true);
            }

            if (trimmed.StartsWith("<log4j:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("</log4j:", StringComparison.OrdinalIgnoreCase))
                return null;

            // Defensive cleanup for malformed or resumed XML fragments.
            if (line.Contains("]]></log4j:Message>", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("</log4j:Message>", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("]]></log4j:Throwable>", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("</log4j:Throwable>", StringComparison.OrdinalIgnoreCase))
            {
                return FormatLog4jElementContent(line, includePrefix: false);
            }

            if (IsFabricDependencyTreeLine(trimmed))
                return null;

            return line;
        }

        private string? StartLog4jEvent(string line)
        {
            _level = ExtractXmlAttribute(line, "level");
            var thread = ExtractXmlAttribute(line, "thread");
            var logger = ExtractXmlAttribute(line, "logger");
            _source = !string.IsNullOrWhiteSpace(thread) && !string.IsNullOrWhiteSpace(logger)
                ? $"{thread}/{logger}"
                : thread ?? logger;

            var messageOpen = FindFirstLog4jContentElement(line);
            if (messageOpen.ElementName is null)
                return null;

            var contentStart = line.IndexOf('>', messageOpen.Index);
            if (contentStart < 0)
                return null;

            _insideLog4jElement = messageOpen.ElementName;
            return FormatLog4jElementContent(line[(contentStart + 1)..], includePrefix: true);
        }

        private string? StartLog4jElement(string line, bool includePrefix)
        {
            var open = FindFirstLog4jContentElement(line);
            if (open.ElementName is null)
                return null;

            var contentStart = line.IndexOf('>', open.Index);
            if (contentStart < 0)
                return null;

            _insideLog4jElement = open.ElementName;
            return FormatLog4jElementContent(line[(contentStart + 1)..], includePrefix);
        }

        private string? FormatLog4jElementContent(string line, bool includePrefix)
        {
            var value = line;
            if (value.StartsWith("<![CDATA[", StringComparison.Ordinal))
                value = value[9..];

            var element = _insideLog4jElement;
            var cdataClose = element is null
                ? IndexOfAny(value, "]]></log4j:Message>", "]]></log4j:Throwable>")
                : value.IndexOf($"]]></log4j:{element}>", StringComparison.OrdinalIgnoreCase);
            if (cdataClose >= 0)
            {
                value = value[..cdataClose];
                _insideLog4jElement = null;
            }
            else
            {
                var elementClose = element is null
                    ? IndexOfAny(value, "</log4j:Message>", "</log4j:Throwable>")
                    : value.IndexOf($"</log4j:{element}>", StringComparison.OrdinalIgnoreCase);
                if (elementClose >= 0)
                {
                    value = value[..elementClose];
                    _insideLog4jElement = null;
                }
            }

            value = value.Replace("</log4j:Event>", "", StringComparison.OrdinalIgnoreCase);
            value = WebUtility.HtmlDecode(value);
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (IsFabricDependencyTreeLine(value.Trim()))
                return null;

            if (!includePrefix || string.IsNullOrWhiteSpace(_level) || string.IsNullOrWhiteSpace(_source))
                return value;

            return $"[{_level}] [{_source}]: {value}";
        }

        private static (string? ElementName, int Index) FindFirstLog4jContentElement(string line)
        {
            var messageIndex = line.IndexOf("<log4j:Message", StringComparison.OrdinalIgnoreCase);
            var throwableIndex = line.IndexOf("<log4j:Throwable", StringComparison.OrdinalIgnoreCase);

            if (messageIndex < 0)
                return throwableIndex < 0 ? (null, -1) : ("Throwable", throwableIndex);

            if (throwableIndex < 0 || messageIndex < throwableIndex)
                return ("Message", messageIndex);

            return ("Throwable", throwableIndex);
        }

        private static int IndexOfAny(string value, params string[] needles)
        {
            var best = -1;
            foreach (var needle in needles)
            {
                var index = value.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (best < 0 || index < best))
                    best = index;
            }

            return best;
        }

        private static bool IsFabricDependencyTreeLine(string trimmed)
        {
            if (trimmed.Length < 3)
                return false;

            return IsSimpleArtifactVersionLine(trimmed) ||
                   trimmed.StartsWith("|-- ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("\\-- ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("|    ", StringComparison.Ordinal) ||
                   trimmed.StartsWith("|   ", StringComparison.Ordinal);
        }

        private static bool IsSimpleArtifactVersionLine(string trimmed)
        {
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
                return false;

            var text = trimmed[2..].Trim();
            if (text.Length == 0 ||
                text.Contains('\'') ||
                text.Contains(',') ||
                text.Contains('!') ||
                text.Contains(" should ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts is { Length: >= 2, Length: <= 4 } &&
                   parts[0].All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '[' or ']') &&
                   parts[1].Any(char.IsDigit);
        }
    }

    private static string? ExtractXmlAttribute(string text, string attributeName)
    {
        var marker = attributeName + "=\"";
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += marker.Length;
        var end = text.IndexOf('"', start);
        if (end <= start)
            return null;

        return WebUtility.HtmlDecode(text[start..end]);
    }
}
