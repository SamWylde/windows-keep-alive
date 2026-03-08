using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using KeepAliveService.Update;

namespace KeepAliveService.UI;

public sealed class WpfOutputWriter : TextWriter
{
    private const long MaxLogBytes = 5L * 1024L * 1024L;
    private const long RetainedLogBytes = MaxLogBytes / 2;
    private const int RotationCheckIntervalLines = 50;

    private readonly RichTextBox _outputBox;
    private readonly Paragraph _paragraph;
    private readonly StringBuilder _buffer = new();
    private readonly object _sync = new();
    private readonly string _logFilePath;
    private int _linesSinceRotationCheck;

    private static readonly SolidColorBrush FailBrush = new(Color.FromRgb(192, 0, 0));
    private static readonly SolidColorBrush WarnBrush = new(Color.FromRgb(153, 102, 0));
    private static readonly SolidColorBrush PassBrush = new(Color.FromRgb(0, 136, 0));
    private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0, 85, 170));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(30, 30, 30));

    static WpfOutputWriter()
    {
        FailBrush.Freeze();
        WarnBrush.Freeze();
        PassBrush.Freeze();
        InfoBrush.Freeze();
        DefaultBrush.Freeze();
    }

    public WpfOutputWriter(RichTextBox outputBox, Paragraph paragraph)
    {
        _outputBox = outputBox;
        _paragraph = paragraph;
        AppSettings.EnsureDirectories();
        _logFilePath = AppSettings.LogPath;
        RotateLogIfNeeded();
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\r')
            return;

        lock (_sync)
        {
            if (value == '\n')
            {
                FlushBufferedLine();
                return;
            }

            _buffer.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        foreach (var ch in value)
            Write(ch);
    }

    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            Write(value);
        Write('\n');
    }

    public override void Flush()
    {
        lock (_sync)
        {
            if (_buffer.Length > 0)
                FlushBufferedLine();
        }
    }

    private void FlushBufferedLine()
    {
        var line = _buffer.ToString();
        _buffer.Clear();
        AppendLine(line);
    }

    private void AppendLine(string line)
    {
        AppendToLogFile(line);

        var dispatcher = _outputBox.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        if (dispatcher.CheckAccess())
        {
            AppendLineToUi(line);
        }
        else
        {
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, () => AppendLineToUi(line));
            }
            catch (TaskCanceledException)
            {
                // Dispatcher shut down between check and enqueue.
            }
        }
    }

    private void AppendLineToUi(string line)
    {
        try
        {
            var brush = ResolveColor(line);
            var run = new Run(line + Environment.NewLine) { Foreground = brush };
            _paragraph.Inlines.Add(run);
            _outputBox.ScrollToEnd();
        }
        catch
        {
            // Best effort.
        }
    }

    private void AppendToLogFile(string line)
    {
        try
        {
            _linesSinceRotationCheck++;
            if (_linesSinceRotationCheck >= RotationCheckIntervalLines)
            {
                _linesSinceRotationCheck = 0;
                RotateLogIfNeeded();
            }

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.AppendAllText(_logFilePath, $"{timestamp} {line}{Environment.NewLine}");
        }
        catch
        {
            // Best effort.
        }
    }

    private void RotateLogIfNeeded()
    {
        try
        {
            var info = new FileInfo(_logFilePath);
            if (!info.Exists || info.Length <= MaxLogBytes)
                return;

            using var source = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, source.Length - RetainedLogBytes);
            source.Seek(start, SeekOrigin.Begin);

            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var bytes = memory.ToArray();

            var offset = 0;
            if (start > 0)
            {
                var lineBreakIndex = Array.IndexOf(bytes, (byte)'\n');
                if (lineBreakIndex >= 0 && lineBreakIndex + 1 < bytes.Length)
                    offset = lineBreakIndex + 1;
            }

            var trimmedLength = bytes.Length - offset;
            if (trimmedLength <= 0)
                return;

            using var target = new FileStream(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            target.Write(bytes, offset, trimmedLength);
        }
        catch
        {
            // Best effort.
        }
    }

    private static SolidColorBrush ResolveColor(string line)
    {
        if (line.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase))
            return FailBrush;
        if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
            return WarnBrush;
        if (line.Contains("[PASS]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[OK]", StringComparison.OrdinalIgnoreCase))
            return PassBrush;
        if (line.Contains("[INFO]", StringComparison.OrdinalIgnoreCase))
            return InfoBrush;
        return DefaultBrush;
    }
}
