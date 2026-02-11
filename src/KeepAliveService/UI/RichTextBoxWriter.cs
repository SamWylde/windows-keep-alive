using System.Text;
using KeepAliveService.Update;

namespace KeepAliveService.UI;

public sealed class RichTextBoxWriter : TextWriter
{
    private readonly RichTextBox _outputBox;
    private readonly object _sync = new();
    private readonly StringBuilder _buffer = new();
    private readonly string _logFilePath;

    public RichTextBoxWriter(RichTextBox outputBox)
    {
        _outputBox = outputBox;
        _logFilePath = AppSettings.LogPath;
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
            AppSettings.EnsureDirectories();
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.AppendAllText(_logFilePath, $"{timestamp} {line}{Environment.NewLine}");
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
