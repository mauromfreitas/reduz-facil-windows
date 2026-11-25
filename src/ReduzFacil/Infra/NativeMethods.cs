using System.Runtime.InteropServices;

namespace ReduzFacil.Infra;

internal static partial class NativeMethods
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_ICONINFORMATION = 0x40;
    private const uint MB_SETFOREGROUND = 0x10000;
    private const uint MB_TOPMOST = 0x40000;
    public const int ASFW_ANY = -1;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDllDirectory(string path);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr AddDllDirectory(string path);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("shell32.dll")]
    public static partial void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>Avisa o Explorer que associações/menus mudaram.</summary>
    public static void NotifyShellAssocChanged()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { /* opcional */ }
    }

    /// <summary>Caixa de mensagem nativa: funciona antes do WPF estar carregado.</summary>
    public static void ShowMessage(string caption, string text, bool error)
    {
        var flags = MB_OK | MB_SETFOREGROUND | MB_TOPMOST | (error ? MB_ICONERROR : MB_ICONINFORMATION);
        MessageBox(IntPtr.Zero, text, caption, flags);
    }
}
