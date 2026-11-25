using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

/// <summary>Tema claro, escuro ou igual ao Windows. Troca os pincéis do app na hora (as telas usam DynamicResource).</summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Accent"] = "#10B981", ["AccentDark"] = "#34D399", ["AccentSoft"] = "#064E3B",
        ["Bg"] = "#0F172A", ["Card"] = "#1E293B", ["Text"] = "#F1F5F9", ["Muted"] = "#94A3B8",
        ["Line"] = "#334155", ["Field"] = "#0F172A", ["FieldBorder"] = "#475569", ["Hover"] = "#334155",
        ["Danger"] = "#F87171", ["Warn"] = "#FBBF24", ["Toast"] = "#F1F5F9", ["ToastText"] = "#0F172A",
        ["Viewer"] = "#020617", ["DropStroke"] = "#64748B",
        ["TintImage"] = "#1E3A5F", ["TintVideo"] = "#4A1D3A", ["TintAudio"] = "#4A3B12", ["TintPdf"] = "#4A1F1F",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["Accent"] = "#0E9F6E", ["AccentDark"] = "#057A55", ["AccentSoft"] = "#DEF7EC",
        ["Bg"] = "#F4F6F8", ["Card"] = "#FFFFFF", ["Text"] = "#1F2937", ["Muted"] = "#6B7280",
        ["Line"] = "#E5E7EB", ["Field"] = "#FFFFFF", ["FieldBorder"] = "#D1D5DB", ["Hover"] = "#F3F4F6",
        ["Danger"] = "#C81E1E", ["Warn"] = "#B45309", ["Toast"] = "#1F2937", ["ToastText"] = "#FFFFFF",
        ["Viewer"] = "#E5E7EB", ["DropStroke"] = "#9CA3AF",
        ["TintImage"] = "#DBEAFE", ["TintVideo"] = "#FCE7F3", ["TintAudio"] = "#FEF3C7", ["TintPdf"] = "#FDE8E8",
    };

    private static string _mode = "sistema";
    private static bool _listening;

    public static bool IsDark { get; private set; }

    /// <summary>"claro", "escuro" ou "sistema".</summary>
    public static void Apply(string mode)
    {
        _mode = mode;
        var dark = mode switch
        {
            "escuro" => true,
            "claro" => false,
            _ => WindowsUsesDark(),
        };
        IsDark = dark;
        var res = Application.Current.Resources;
        foreach (var (key, hex) in dark ? Dark : Light)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            res[key] = b;
        }
        foreach (Window w in Application.Current.Windows) TitleBar(w);
        if (!_listening)
        {
            _listening = true;
            // O usuário trocou o tema do Windows com o app aberto: acompanha (só no modo "sistema").
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (_mode == "sistema" && e.Category == UserPreferenceCategory.General)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply("sistema"));
            };
        }
        Log.Info($"Tema: {mode} ({(dark ? "escuro" : "claro")})");
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Barra de título escura no tema escuro (Windows 10 2004+ e 11).</summary>
    public static void TitleBar(Window w)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero)
            {
                w.SourceInitialized -= OnSourceInitialized;
                w.SourceInitialized += OnSourceInitialized;
                return;
            }
            var on = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        }
        catch
        {
            // Windows antigo: barra continua clara.
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window w) TitleBar(w);
    }

    public static bool WindowsUsesDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
