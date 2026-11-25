namespace ReduzFacil.Core.Engines;

/// <summary>Um programa de linha de comando e os argumentos que sempre vão antes (ex.: "magick identify").</summary>
public sealed record Tool(string Exe, IReadOnlyList<string> Prefix)
{
    public Tool(string exe) : this(exe, Array.Empty<string>()) { }
}

/// <summary>
/// Onde estão os motores. No app Windows: ReduzFacil-dados\engine\{ffmpeg,magick,gs}.
/// Nos testes no Linux: programas do sistema (ffmpeg, convert/identify do ImageMagick 6, gs).
/// </summary>
public sealed class EngineTools
{
    public required Tool Ffmpeg { get; init; }
    public required Tool Ffprobe { get; init; }
    public required Tool MagickConvert { get; init; }
    public required Tool MagickIdentify { get; init; }
    public required Tool Ghostscript { get; init; }

    /// <summary>Variáveis de ambiente para os motores (ImageMagick portátil precisa de MAGICK_HOME).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Pasta para arquivos temporários de trabalho (nunca a pasta temporária do Windows no app).</summary>
    public required string WorkDir { get; init; }

    /// <summary>Motores embutidos do app Windows.</summary>
    public static EngineTools FromEngineDir(string engineDir, string workDir)
    {
        var ff = Path.Combine(engineDir, "ffmpeg");
        var mg = Path.Combine(engineDir, "magick");
        var gs = Path.Combine(engineDir, "gs");
        var magick = Path.Combine(mg, "magick.exe");
        var tmp = Path.Combine(workDir, "magick");
        Directory.CreateDirectory(tmp);
        return new EngineTools
        {
            Ffmpeg = new Tool(Path.Combine(ff, "ffmpeg.exe")),
            Ffprobe = new Tool(Path.Combine(ff, "ffprobe.exe")),
            MagickConvert = new Tool(magick),
            MagickIdentify = new Tool(magick, ["identify"]),
            Ghostscript = new Tool(Path.Combine(gs, "gswin64c.exe")),
            WorkDir = workDir,
            Environment = new Dictionary<string, string>
            {
                ["MAGICK_HOME"] = mg,
                ["MAGICK_CONFIGURE_PATH"] = mg,
                ["MAGICK_TEMPORARY_PATH"] = tmp,
                // Fotos muito grandes não podem esgotar a memória do PC.
                ["MAGICK_MEMORY_LIMIT"] = "2GiB",
                ["MAGICK_DISK_LIMIT"] = "16GiB",
            },
        };
    }

    /// <summary>Programas instalados no sistema (testes no Linux ou máquina de desenvolvimento).</summary>
    public static EngineTools FromSystem(string workDir)
    {
        var magick7 = FindOnPath("magick");
        return new EngineTools
        {
            Ffmpeg = new Tool(FindOnPath("ffmpeg") ?? "ffmpeg"),
            Ffprobe = new Tool(FindOnPath("ffprobe") ?? "ffprobe"),
            MagickConvert = magick7 is not null ? new Tool(magick7) : new Tool(FindOnPath("convert") ?? "convert"),
            MagickIdentify = magick7 is not null ? new Tool(magick7, ["identify"]) : new Tool(FindOnPath("identify") ?? "identify"),
            Ghostscript = new Tool(FindOnPath(OperatingSystem.IsWindows() ? "gswin64c" : "gs") ?? "gs"),
            WorkDir = workDir,
        };
    }

    public string[] Missing()
    {
        var list = new List<string>();
        foreach (var (name, tool) in new[] { ("FFmpeg", Ffmpeg), ("FFprobe", Ffprobe), ("ImageMagick", MagickConvert), ("Ghostscript", Ghostscript) })
            if (Path.IsPathRooted(tool.Exe) && !File.Exists(tool.Exe)) list.Add(name);
        return list.ToArray();
    }

    private static string? FindOnPath(string name)
    {
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" };
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                var p = Path.Combine(dir, name + ext);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>Saída "descartável" do FFmpeg (1ª passada do 2-pass).</summary>
    public static string NullOutput => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
}
