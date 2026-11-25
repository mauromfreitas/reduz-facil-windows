using System.Runtime.InteropServices;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

/// <summary>Manda arquivos para a Lixeira do Windows (dá para restaurar depois).</summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Devolve quantos foram para a Lixeira.</summary>
    public static int Send(IEnumerable<string> paths)
    {
        var n = 0;
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = Path.GetFullPath(p) + "\0\0",
                // Se o disco não tiver Lixeira (pen drive), o Windows pergunta antes de apagar de vez.
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING,
            };
            var r = SHFileOperation(ref op);
            if (r == 0 && !op.fAnyOperationsAborted && !File.Exists(p)) n++;
            else Log.Warn($"Lixeira recusou {p} (código {r})");
        }
        return n;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint flags);

    /// <summary>Tamanho e quantidade de itens na Lixeira (todos os discos).</summary>
    public static (long Bytes, long Items) Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Esvazia a Lixeira de todos os discos, sem perguntar (o app já perguntou).</summary>
    public static void Empty() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
}
