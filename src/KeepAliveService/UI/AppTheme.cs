using Microsoft.Win32;

namespace KeepAliveService.UI;

public enum ThemeMode
{
    Light,
    Dark,
}

public sealed class Palette
{
    public Color FormBack { get; init; }
    public Color FormFore { get; init; }
    public Color ControlBack { get; init; }
    public Color ControlFore { get; init; }
    public Color ConsoleBack { get; init; }
    public Color ConsoleFore { get; init; }
    public Color GroupBoxFore { get; init; }
    public Color BtnPrimaryBack { get; init; }
    public Color BtnPrimaryFore { get; init; }
    public Color BtnSecondaryBack { get; init; }
    public Color BtnSecondaryFore { get; init; }
    public Color BtnDestructiveBack { get; init; }
    public Color BtnDestructiveFore { get; init; }
    public Color LogFail { get; init; }
    public Color LogWarn { get; init; }
    public Color LogPass { get; init; }
    public Color LogInfo { get; init; }
    public Font MonoFont { get; init; } = new("Consolas", 10.5f);
}

public static class AppTheme
{
    public const int SpacingSmall = 8;
    public const int SpacingMedium = 12;
    public const int SpacingLarge = 16;
    public const int SpacingGap = 12;

    private static readonly Palette DarkPalette = new()
    {
        FormBack = Color.FromArgb(30, 33, 39),
        FormFore = Color.Gainsboro,
        ControlBack = Color.FromArgb(43, 47, 56),
        ControlFore = Color.Gainsboro,
        ConsoleBack = Color.FromArgb(28, 31, 36),
        ConsoleFore = Color.Gainsboro,
        GroupBoxFore = Color.FromArgb(160, 168, 184),
        BtnPrimaryBack = Color.FromArgb(52, 120, 246),
        BtnPrimaryFore = Color.White,
        BtnSecondaryBack = Color.FromArgb(58, 63, 75),
        BtnSecondaryFore = Color.FromArgb(224, 224, 224),
        BtnDestructiveBack = Color.FromArgb(139, 32, 32),
        BtnDestructiveFore = Color.White,
        LogFail = Color.Firebrick,
        LogWarn = Color.DarkGoldenrod,
        LogPass = Color.ForestGreen,
        LogInfo = Color.SteelBlue,
        MonoFont = new Font("Consolas", 10.5f),
    };

    private static readonly Palette LightPalette = new()
    {
        FormBack = SystemColors.Control,
        FormFore = SystemColors.ControlText,
        ControlBack = SystemColors.Window,
        ControlFore = SystemColors.WindowText,
        ConsoleBack = Color.FromArgb(245, 245, 245),
        ConsoleFore = Color.FromArgb(30, 30, 30),
        GroupBoxFore = SystemColors.ControlText,
        BtnPrimaryBack = Color.FromArgb(0, 102, 204),
        BtnPrimaryFore = Color.White,
        BtnSecondaryBack = SystemColors.Control,
        BtnSecondaryFore = SystemColors.ControlText,
        BtnDestructiveBack = Color.FromArgb(204, 51, 51),
        BtnDestructiveFore = Color.White,
        LogFail = Color.FromArgb(192, 0, 0),
        LogWarn = Color.FromArgb(153, 102, 0),
        LogPass = Color.FromArgb(0, 136, 0),
        LogInfo = Color.FromArgb(0, 85, 170),
        MonoFont = new Font("Consolas", 10.5f),
    };

    public static ThemeMode Detect()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i && i == 0)
            {
                return ThemeMode.Dark;
            }
        }
        catch
        {
            // Fall through to default.
        }

        return ThemeMode.Light;
    }

    public static Palette GetPalette(ThemeMode mode) =>
        mode == ThemeMode.Dark ? DarkPalette : LightPalette;

    public static void Apply(Control root, Palette p)
    {
        root.BackColor = p.FormBack;
        root.ForeColor = p.FormFore;
        ApplyRecursive(root, p);
    }

    private static void ApplyRecursive(Control parent, Palette p)
    {
        foreach (Control child in parent.Controls)
        {
            switch (child)
            {
                case RichTextBox rtb:
                    rtb.BackColor = p.ConsoleBack;
                    rtb.ForeColor = p.ConsoleFore;
                    rtb.Font = p.MonoFont;
                    break;

                case TextBox tb:
                    tb.BackColor = p.ControlBack;
                    tb.ForeColor = p.ControlFore;
                    break;

                case ComboBox cb:
                    cb.BackColor = p.ControlBack;
                    cb.ForeColor = p.ControlFore;
                    break;

                case Button:
                    // Buttons are excluded — MainForm handles hierarchy.
                    break;

                case GroupBox gb:
                    gb.BackColor = p.FormBack;
                    gb.ForeColor = p.GroupBoxFore;
                    ApplyRecursive(gb, p);
                    break;

                case TabControl tc:
                    tc.BackColor = p.FormBack;
                    tc.ForeColor = p.FormFore;
                    ApplyRecursive(tc, p);
                    break;

                case TabPage tp:
                    tp.BackColor = p.FormBack;
                    tp.ForeColor = p.FormFore;
                    ApplyRecursive(tp, p);
                    break;

                case StatusStrip ss:
                    ss.BackColor = p.FormBack;
                    ss.ForeColor = p.FormFore;
                    foreach (ToolStripItem item in ss.Items)
                    {
                        item.ForeColor = p.FormFore;
                    }
                    break;

                default:
                    child.BackColor = p.FormBack;
                    child.ForeColor = p.FormFore;
                    if (child.HasChildren)
                    {
                        ApplyRecursive(child, p);
                    }
                    break;
            }
        }
    }

    public static IDisposable OnThemeChanged(Form form, Action callback)
    {
        void Handler(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General)
            {
                return;
            }

            if (form.InvokeRequired)
            {
                form.BeginInvoke(callback);
            }
            else
            {
                callback();
            }
        }

        SystemEvents.UserPreferenceChanged += Handler;
        return new EventUnsubscriber(() => SystemEvents.UserPreferenceChanged -= Handler);
    }

    private sealed class EventUnsubscriber(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            _unsubscribe?.Invoke();
            _unsubscribe = null;
        }
    }
}
