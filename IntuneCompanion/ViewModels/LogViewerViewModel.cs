using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IntuneCompanion.ViewModels;

public partial class LogViewerViewModel : ObservableObject
{
    private static readonly string LogsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "IntuneManagementExtension", "Logs");

    [ObservableProperty]
    private string? _selectedFile;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<string> LogFiles { get; } = [];
    public ObservableCollection<LogLine> LogLines { get; } = [];

    public LogViewerViewModel()
    {
        LoadFileList();
    }

    private void LoadFileList()
    {
        LogFiles.Clear();

        if (Directory.Exists(LogsFolder))
        {
            foreach (var file in Directory.GetFiles(LogsFolder, "*.log").OrderBy(f => f))
                LogFiles.Add(Path.GetFileName(file));
        }

        if (LogFiles.Count > 0)
        {
            SelectedFile = LogFiles.FirstOrDefault(f => f.StartsWith("IntuneManagementExtension", StringComparison.OrdinalIgnoreCase))
                ?? LogFiles[0];
        }
    }

    partial void OnSelectedFileChanged(string? value)
    {
        if (value is not null)
            LoadFileCommand.Execute(value);
    }

    [RelayCommand]
    private async Task LoadFileAsync(string fileName)
    {
        IsLoading = true;
        LogLines.Clear();

        var path = Path.Combine(LogsFolder, fileName);
        if (!File.Exists(path))
        {
            IsLoading = false;
            return;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var content = await reader.ReadToEndAsync();

        // CMTrace entries can span multiple lines. Split on the pattern that starts a new entry.
        // Each entry ends with `]LOG]!><...>` on a single line or across lines.
        // We reassemble by splitting on the closing metadata tag boundary.
        var entries = SplitEntries(content);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) 
                continue;

            var parsed = ParseLine(entry);
            LogLines.Add(parsed);
        }

        IsLoading = false;
    }

    private static List<string> SplitEntries(string content)
    {
        var entries = new List<string>();
        var remaining = content.AsSpan();

        while (true)
        {
            var start = remaining.IndexOf("<![LOG[".AsSpan());
            if (start < 0)
            {
                // No more CMTrace entries; treat remainder as plain lines
                foreach (var line in remaining.ToString().Split('\n'))
                {
                    var trimmed = line.TrimEnd('\r');

                    if (trimmed.Length > 0)
                        entries.Add(trimmed);
                }
                break;
            }

            // Any text before this entry (non-CMTrace lines)
            if (start > 0)
            {
                foreach (var line in remaining[..start].ToString().Split('\n'))
                {
                    var trimmed = line.TrimEnd('\r');

                    if (trimmed.Length > 0)
                        entries.Add(trimmed);
                }
            }

            remaining = remaining[start..];

            // Find the end: `]LOG]!><...>` ending with `>`
            var logEnd = remaining.IndexOf("]LOG]!>".AsSpan());

            if (logEnd < 0)
            {
                entries.Add(remaining.ToString().TrimEnd('\r', '\n'));
                break;
            }

            // Find the closing `>` of the metadata tag after `]LOG]!>`
            var metaStart = logEnd + 7; // after "]LOG]!>"
            var closingBracket = remaining[metaStart..].IndexOf('>');
            int entryEnd;

            if (closingBracket >= 0)
                entryEnd = metaStart + closingBracket + 1;
            else
                entryEnd = remaining.Length;

            entries.Add(remaining[..entryEnd].ToString().TrimEnd('\r', '\n'));
            remaining = remaining[entryEnd..].TrimStart("\r\n".AsSpan());
        }

        return entries;
    }

    private static LogLine ParseLine(string raw)
    {
        // CMTrace format: <![LOG[message]LOG]!><time="HH:mm:ss.fff" date="MM-DD-YYYY" ... type="N" ...>
        var msg = raw;
        DateTime? timestamp = null;
        int logType = 1; // 1=Info, 2=Warning, 3=Error

        var logStart = raw.IndexOf("<![LOG[", StringComparison.Ordinal);
        var logEnd = raw.IndexOf("]LOG]!>", StringComparison.Ordinal);

        if (logStart >= 0 && logEnd > logStart)
        {
            msg = raw[(logStart + 7)..logEnd];
            // Strip prefix like "2026-08-07 12:28:09 PM | thread[4] | "
            msg = MessagePrefixRegex().Replace(msg, "", 1);
        }

        var timeIdx = raw.IndexOf("time=\"", StringComparison.Ordinal);
        var dateIdx = raw.IndexOf("date=\"", StringComparison.Ordinal);

        if (timeIdx >= 0 && dateIdx >= 0)
        {
            var timeEnd = raw.IndexOf('"', timeIdx + 6);
            var dateEnd = raw.IndexOf('"', dateIdx + 6);

            if (timeEnd > 0 && dateEnd > 0)
            {
                var timePart = raw[(timeIdx + 6)..timeEnd];
                var datePart = raw[(dateIdx + 6)..dateEnd];

                // time may have offset like "HH:mm:ss.fff+000" or extra fractional digits
                var plusIdx = timePart.IndexOf('+');
                if (plusIdx > 0) timePart = timePart[..plusIdx];
                var minusIdx = timePart.IndexOf('-');
                if (minusIdx > 0) timePart = timePart[..minusIdx];
                if (timePart.Length > 12)
                    timePart = timePart[..12];

                if (DateTime.TryParseExact($"{datePart} {timePart}",
                    ["M-d-yyyy HH:mm:ss.fff", "MM-dd-yyyy HH:mm:ss.fff", "M-dd-yyyy HH:mm:ss.fff", "MM-d-yyyy HH:mm:ss.fff"],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                    timestamp = dt;
            }
        }

        var typeIdx = raw.IndexOf("type=\"", StringComparison.Ordinal);

        if (typeIdx >= 0)
        {
            var typeEnd = raw.IndexOf('"', typeIdx + 6);

            if (typeEnd > 0 && int.TryParse(raw[(typeIdx + 6)..typeEnd], out var t))
                logType = t;
        }

        return new LogLine(timestamp, msg, logType);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\s+\d{1,2}:\d{2}:\d{2}\s*(AM|PM)?\s*\|\s*thread\[\d+\]\s*\|\s*", RegexOptions.IgnoreCase)]
    private static partial Regex MessagePrefixRegex();
}

public record LogLine(DateTime? Timestamp, string Message, int LogType)
{
    public string DateText => Timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "";
    public string MessageSingleLine => Message.ReplaceLineEndings(" ");
}