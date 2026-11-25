namespace ReduzFacil.Core.Output;

public enum OutputMode { NextToOriginal, Folder }

public sealed class OutputSettings
{
    public OutputMode Mode { get; set; } = OutputMode.NextToOriginal;
    public string? Folder { get; set; }
    public string Suffix { get; set; } = "-reduzido";
    /// <summary>O arquivo novo fica com a mesma data de modificação do original (fotos continuam em ordem).</summary>
    public bool KeepDates { get; set; } = true;

    public OutputSettings Clone() => (OutputSettings)MemberwiseClone();

    /// <summary>Sufixo efetivo: ao lado do original ele é obrigatório (senão o nome seria igual ao original).</summary>
    public string EffectiveSuffix(string? sourceDir, string destDir)
    {
        var s = (Suffix ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
        if (s.Length == 0 && (Mode == OutputMode.NextToOriginal || PathsEqual(sourceDir, destDir))) s = "-reduzido";
        return s;
    }

    internal static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), cmp);
    }
}

/// <summary>Decide onde e com que nome o arquivo reduzido é salvo. Nunca sobrescreve nada.</summary>
public static class OutputNamer
{
    /// <param name="sourceRoot">Pasta que o usuário arrastou (para recriar as subpastas no modo "pasta escolhida").</param>
    public static string Decide(OutputSettings s, string inputPath, string extension, string? sourceRoot = null)
    {
        var inputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
        string destDir;
        if (s.Mode == OutputMode.Folder && !string.IsNullOrWhiteSpace(s.Folder))
        {
            destDir = Path.GetFullPath(s.Folder);
            if (sourceRoot is not null)
            {
                var root = Path.GetFullPath(sourceRoot);
                var rel = Path.GetRelativePath(root, inputDir);
                if (rel != "." && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                    destDir = Path.Combine(destDir, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), rel);
                else if (rel == ".")
                    destDir = Path.Combine(destDir, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)));
            }
        }
        else destDir = inputDir;

        var suffix = s.EffectiveSuffix(inputDir, destDir);
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var ext = "." + extension.TrimStart('.');
        var candidate = Path.Combine(destDir, stem + suffix + ext);
        for (var n = 2; Exists(candidate) || OutputSettings.PathsEqual(candidate, inputPath); n++)
            candidate = Path.Combine(destDir, $"{stem}{suffix} ({n}){ext}");
        return candidate;
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
}
