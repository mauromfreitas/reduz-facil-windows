using System.Text;
using ReduzFacil.Core.Compress;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Media;

namespace ReduzFacil.Tests;

public static class Env
{
    public static string? EngineDir { get; set; }
    public static string Root { get; set; } = "";
    public static string In => Path.Combine(Root, "entrada");
    public static string Work => Path.Combine(Root, "trabalho");

    private static EngineTools? _tools;
    public static EngineTools Tools => _tools ??= EngineDir is null
        ? EngineTools.FromSystem(Work)
        : EngineTools.FromEngineDir(EngineDir, Work);

    public static CompressContext NewContext(HardwareCatalog? hw = null) => new() { Tools = Tools, Hardware = hw ?? SharedHw };

    /// <summary>Uma detecção por execução dos testes.</summary>
    public static HardwareCatalog SharedHw { get; } = new();

    /// <summary>Pasta de saída nova para cada teste.</summary>
    public static string NewOut(string name)
    {
        var d = Path.Combine(Root, "saida", name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    public static string F(string name) => Path.Combine(In, name);

    public static async Task<string> Run(Tool tool, params string[] args)
    {
        var r = await ProcessRunner.RunAsync(tool, args, Tools.Environment, captureStdOut: true, timeout: TimeSpan.FromMinutes(5));
        if (!r.Ok) throw new Exception($"{Path.GetFileName(tool.Exe)} {string.Join(' ', args)} falhou: {r.StdErrTail}");
        return r.StdOut;
    }

    public static Task<MediaInfo> Probe(string path) => MediaProbe.ProbeAsync(Tools, path, CancellationToken.None);

    public static async Task<string> Exif(string path, string tag) =>
        (await Run(Tools.MagickIdentify, "-quiet", "-format", $"%[EXIF:{tag}]", path)).Trim();
}

public static class Fixtures
{
    public const string Photo = "Foto da família São João.jpg";
    public const string Small = "pequena.jpg";
    public const string Alpha = "logo transparente.png";
    public const string Heic = "iphone.heic";
    public const string Video = "vídeo festa ação.mp4";
    public const string Rotated = "celular em pé.mp4";
    public const string Hdr = "hdr iphone.mov";
    public const string Wav = "gravação.wav";
    public const string Scanned = "documento escaneado.pdf";
    public const string TextPdf = "texto.pdf";
    public const string Locked = "com senha.pdf";
    public const string Broken = "quebrado.mp4";
    public const string Bmp = "desenho.bmp";
    public const string TinyPng = "icone.png";

    public static bool HasHeic { get; private set; }

    public static async Task CreateAsync()
    {
        Directory.CreateDirectory(Env.In);
        Directory.CreateDirectory(Env.Work);
        var t = Env.Tools;
        var m = t.MagickConvert;
        var ff = t.Ffmpeg;
        var tmp = Path.Combine(Env.Root, "tmp-fix");
        Directory.CreateDirectory(tmp);

        // Foto "de celular": 4000×3000, com ruído (comprime como foto real), qualidade 97, EXIF com GPS e câmera.
        var raw = Path.Combine(tmp, "raw.jpg");
        await Env.Run(m, "-size", "4000x3000", "gradient:#3a7bd5-#f6d365", "-attenuate", "0.35", "+noise", "Gaussian", "-quality", "97", raw);
        InjectExif(raw, Env.F(Photo));

        await Env.Run(m, "-size", "320x240", "gradient:#ddd-#999", "-quality", "60", Env.F(Small));
        await Env.Run(m, "-size", "800x600", "gradient:#123-#9ab", Env.F(Bmp));
        await Env.Run(m, "-size", "64x64", "xc:#2a6", "-define", "png:compression-level=9", Env.F(TinyPng));
        await Env.Run(m, "-size", "1600x1000", "xc:none", "-fill", "rgba(220,40,40,0.8)", "-draw", "circle 800,500 800,120",
            "-fill", "rgba(40,90,220,0.6)", "-draw", "rectangle 100,100 700,600", "-attenuate", "0.2", "+noise", "Uniform", Env.F(Alpha));

        // HEIC: no Linux pelo heif-enc; no Windows pelo próprio ImageMagick (se ele souber gravar).
        try
        {
            var mid = Path.Combine(tmp, "heic-src.jpg");
            await Env.Run(m, Env.F(Photo), "-resize", "2000x1500", mid);
            if (Env.EngineDir is null && File.Exists("/usr/bin/heif-enc"))
                await Env.Run(new Tool("/usr/bin/heif-enc"), "-q", "70", "-o", Env.F(Heic), mid);
            else
                await Env.Run(m, mid, "-quality", "70", Env.F(Heic));
            HasHeic = File.Exists(Env.F(Heic)) && new FileInfo(Env.F(Heic)).Length > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Sem HEIC de teste: " + ex.Message.Split('\n')[0]);
        }

        // Vídeo 1080p 30 fps, 12 s, com som, bitrate alto (como celular).
        await Env.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "12", "-c:v", "libx264", "-preset", "ultrafast",
            "-crf", "14", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest",
            "-metadata", "location=+23.5505-046.6333/", "-metadata", "creation_time=2026-01-02T10:00:00Z", Env.F(Video));

        // Vídeo gravado com o celular em pé (rotação na matriz de exibição).
        var land = Path.Combine(tmp, "land.mp4");
        await Env.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=330", "-t", "4", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", land);
        await Env.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-display_rotation", "90", "-i", land, "-c", "copy", Env.F(Rotated));

        // HDR (HLG 10 bits, como iPhone) em .mov com H.265.
        await Env.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-t", "3",
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-pix_fmt", "yuv420p10le",
            "-color_primaries", "bt2020", "-color_trc", "arib-std-b67", "-colorspace", "bt2020nc", "-tag:v", "hvc1", Env.F(Hdr));

        await Env.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=44100",
            "-f", "lavfi", "-i", "anoisesrc=color=pink:amplitude=0.2", "-filter_complex", "[0][1]amix=inputs=2,aformat=channel_layouts=stereo",
            "-t", "40", "-c:a", "pcm_s16le", Env.F(Wav));

        // PDF escaneado: 3 páginas A4 em 200 dpi, JPEG qualidade alta.
        var pages = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var p = Path.Combine(tmp, $"pagina{i}.jpg");
            await Env.Run(m, "-size", "1654x2339", "xc:white", "-attenuate", "0.25", "+noise", "Gaussian",
                "-fill", "black", "-draw", $"rectangle 150,{200 * i} 1500,{200 * i + 60}", "-quality", "95", p);
            pages.Add(p);
        }
        await Env.Run(m, [.. pages, "-density", "200", "-units", "PixelsPerInch", "-quality", "95", "-compress", "JPEG", Env.F(Scanned)]);

        await Env.Run(t.Ghostscript, "-q", "-dNOPAUSE", "-dBATCH", "-sDEVICE=pdfwrite", "-sOutputFile=" + Env.F(TextPdf),
            "-c", "/Helvetica findfont 24 scalefont setfont 72 720 moveto (Reduz Facil texto) show showpage");
        await Env.Run(t.Ghostscript, "-q", "-dNOPAUSE", "-dBATCH", "-sDEVICE=pdfwrite", "-sOwnerPassword=dono", "-sUserPassword=usuario",
            "-sOutputFile=" + Env.F(Locked), Env.F(TextPdf));

        await File.WriteAllBytesAsync(Env.F(Broken), Encoding.ASCII.GetBytes(new string('x', 50_000)));

        foreach (var f in Directory.GetFiles(Env.In))
            Console.WriteLine($"  amostra: {Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} KB)");
    }

    /// <summary>Coloca um bloco EXIF (câmera "TesteCam" e latitude GPS) logo depois do início do JPEG.</summary>
    public static void InjectExif(string src, string dst)
    {
        var jpg = File.ReadAllBytes(src);
        var tiff = new List<byte>();
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(long v) { for (var i = 0; i < 4; i++) tiff.Add((byte)(v >> (8 * i))); }
        // Cabeçalho TIFF (little-endian), IFD0 em 8
        tiff.AddRange("II"u8.ToArray()); U16(42); U32(8);
        // IFD0: Make (0x010F) e GPSInfo (0x8825) → 2 + 24 + 4 = 30 bytes; GPS IFD em 38
        U16(2);
        U16(0x010F); U16(2); U32(9); U32(92);
        U16(0x8825); U16(4); U32(1); U32(38);
        U32(0);
        // GPS IFD: LatitudeRef "S" e Latitude (3 racionais em 68) → 30 bytes
        U16(2);
        U16(0x0001); U16(2); U32(2); tiff.AddRange([(byte)'S', 0, 0, 0]);
        U16(0x0002); U16(5); U32(3); U32(68);
        U32(0);
        // 23/1 33/1 0/1
        U32(23); U32(1); U32(33); U32(1); U32(0); U32(1);
        tiff.AddRange(Encoding.ASCII.GetBytes("TesteCam\0"));

        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var len = payload.Length + 2;
        using var o = File.Create(dst);
        o.Write(jpg, 0, 2); // FFD8
        o.Write([0xFF, 0xE1, (byte)(len >> 8), (byte)len]);
        o.Write(payload);
        o.Write(jpg, 2, jpg.Length - 2);
    }
}
