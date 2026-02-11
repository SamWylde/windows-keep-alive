using System.Text;
using KeepAliveService.Update;

namespace KeepAliveService.UI;

public sealed class RichTextBoxWriter : TextWriter
{
    private const long MaxLogBytes = 1_048_576; // 1 MB
    private const long RetainedLogBytes = 786_432; // 768 KB
    private const int RotationCheckIntervalLines = 50;

    private readonly RichTextBox _outputBox;
    private readonly object _sync = new();
    private readonly StringBuilder _buffer = new();
    private readonly string _logFilePath;
    private int _linesSinceRotationCheck;

    public RichTextBoxWriter(RichTextBox outputBox)
    {
        _outputBox = outputBox;
        AppSettings.EnsureDirectories();
        _logFilePath = AppSettings.LogPath;
        RotateLogIfNeeded();
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\r')
        {
            return;
        }

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
        {
            return;
        }

        foreach (var ch in value)
        {
            Write(ch);
        }
    }

    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Write(value);
        }

        Write('\n');
    }

    public override void Flush()
    {
        lock (_sync)
        {
            if (_buffer.Length > 0)
            {
                FlushBufferedLine();
            }
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

        if (_outputBox.IsDisposed)
        {
            return;
        }

        void AppendAction()
        {
            var color = ResolveColor(line);
            _outputBox.SelectionStart = _outputBox.TextLength;
            _outputBox.SelectionLength = 0;
            _outputBox.SelectionColor = color;
            _outputBox.AppendText(line + Environment.NewLine);
            _outputBox.SelectionColor = _outputBox.ForeColor;
            _outputBox.ScrollToCaret();
        }

        if (_outputBox.InvokeRequired)
        {
            _outputBox.BeginInvoke((MethodInvoker)AppendAction);
        }
        else
        {
            AppendAction();
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
            // Best effort only.
        }
    }

    private void RotateLogIfNeeded()
    {
        try
        {
            var info = new FileInfo(_logFilePath);
            if (!info.Exists || info.Length <= MaxLogBytes)
            {
                return;
            }

            using var source = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, source.Length - RetainedLogBytes);
            source.Seek(start, SeekOrigin.Begin);

            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var bytes = memory.ToArray();

            // Try to start on a line boundary after truncation.
            var offset = 0;
            if (start > 0)
            {
                var lineBreakIndex = Array.IndexOf(bytes, (byte)'\n');
                if (lineBreakIndex >= 0 && lineBreakIndex + 1 < bytes.Length)
                {
                    offset = lineBreakIndex + 1;
                }
            }

            var trimmedLength = bytes.Length - offset;
            if (trimmedLength <= 0)
            {
                return;
            }

            using var target = new FileStream(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            target.Write(bytes, offset, trimmedLength);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static Color ResolveColor(string line)
    {
        if (line.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase))
        {
            return Color.Firebrick;
        }

        if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
        {
            return Color.DarkGoldenrod;
        }

        if (line.Contains("[PASS]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[OK]", StringComparison.OrdinalIgnoreCase))
        {
            return Color.ForestGreen;
        }

        if (line.Contains("[INFO]", StringComparison.OrdinalIgnoreCase))
        {
            return Color.SteelBlue;
        }

        return Color.Gainsboro;
    }
}
