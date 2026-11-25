using System.Text.RegularExpressions;
using ReduzFacil.Core.Model;

namespace ReduzFacil.Core.Queue;

/// <summary>Transforma o que foi arrastado (arquivos e pastas) em arquivos suportados.</summary>
public static class FileCollector
{
    public sealed record Found(string Path, string? SourceRoot);

    /// <summary>Pastas que nunca são varridas (sistema, programas, lixeira, nossos dados).</summary>
    public static readonly string[] SkipDirNames =
    [
        "$Recycle.Bin", "System Volume Information", "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "AppData", "node_modules", ".git", "ReduzFacil-dados", "$WinREAgent", "Recovery", "OneDriveTemp",
    ];

    public static List<Found> Collect(IEnumerable<string> dropped, string? ownSuffix, CancellationToken ct = default)
    {
        var list = new List<Found>();
        foreach (var raw in dropped)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var p = raw.Trim().Trim('"');
            if (File.Exists(p))
            {
                // Arquivo escolhido de propósito entra mesmo se tiver o sufixo (o usuário pediu).
                if (FileTypes.KindOf(p) is not null) list.Add(new Found(Path.GetFullPath(p), null));
            }
            else if (Directory.Exists(p))
            {
                var root = Path.GetFullPath(p);
                foreach (var f in Walk(root, ct))
                    if (FileTypes.KindOf(f) is not null && !FileTypes.IsTemporary(f) && !IsOwnOutput(f, ownSuffix))
                        list.Add(new Found(f, root));
            }
        }
        return list;
    }

    /// <summary>Percorre a pasta pulando ocultos, do sistema, atalhos de pasta e pastas especiais.</summary>
    public static IEnumerable<string> Walk(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline,
        };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", opts).ToList();
                dirs = Directory.EnumerateDirectories(dir, "*", opts).ToList();
            }
            catch
            {
                continue;
            }
            foreach (var f in files) yield return f;
            foreach (var d in dirs.Reverse())
            {
                var name = Path.GetFileName(d);
                if (SkipDirNames.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                stack.Push(d);
            }
        }
    }

    /// <summary>"foto-reduzido.jpg", "foto-reduzido (2).jpg" e "video-reduzido-parte2.mp4" são saídas nossas: não entram de novo.</summary>
    public static bool IsOwnOutput(string path, string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix)) return false;
        var stem = Path.GetFileNameWithoutExtension(path);
        return Regex.IsMatch(stem, Regex.Escape(suffix.Trim()) + @"(-parte\d+)?( \(\d+\))?$", RegexOptions.IgnoreCase);
    }
}
