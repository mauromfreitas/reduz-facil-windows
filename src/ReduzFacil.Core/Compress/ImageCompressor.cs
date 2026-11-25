using System.Globalization;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Compress;

public sealed record ImageInfo(int Width, int Height, bool HasAlpha, string Format);

public sealed class ImageCompressor : IKindCompressor
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static (int Quality, int MaxSide) LevelParams(Level level) => level switch
    {
        Level.Maxima => (88, 0),
        Level.Equilibrado => (80, 2560),
        _ => (68, 1600),
    };

    /// <summary>Extensão de saída (jpg, png ou webp).</summary>
    public static string OutputExtension(string inputPath, CompressOptions o, bool hasAlpha)
    {
        var native = FileTypes.Ext(inputPath) switch
        {
            "png" => "png",
            "webp" => "webp",
            _ => "jpg", // jpg/jpeg/jfif e também HEIC, BMP e AVIF (JPG abre em qualquer lugar)
        };
        if (o.ConvertOnly) return hasAlpha ? "png" : "jpg";
        return o.ImageFormat switch
        {
            ImageFormat.Jpg => "jpg",
            ImageFormat.WebP => "webp",
            ImageFormat.Manter => native,
            _ => hasAlpha ? (native == "png" ? "webp" : native == "webp" ? "webp" : "png") : "jpg",
        };
    }

    public static async Task<ImageInfo> IdentifyAsync(EngineTools tools, string path, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync(tools.MagickIdentify,
            ["-quiet", "-format", "%w|%h|%m|%[opaque]\\n", path + "[0]"],
            tools.Environment, captureStdOut: true, timeout: TimeSpan.FromMinutes(3), ct: ct).ConfigureAwait(false);
        var line = res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        var parts = line?.Split('|');
        if (!res.Ok && parts is not { Length: 4 } || parts is not { Length: 4 }
            || !int.TryParse(parts[0], NumberStyles.Integer, Inv, out var w) || !int.TryParse(parts[1], NumberStyles.Integer, Inv, out var h) || w <= 0 || h <= 0)
        {
            Log.Warn($"identify falhou ({res.ExitCode}): {res.StdErrTail}");
            throw new UserFacingException("Não consegui abrir esta imagem. Ela pode estar corrompida ou num formato não suportado.");
        }
        var alpha = parts[3].Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        return new ImageInfo(w, h, alpha, parts[2]);
    }

    public async Task<EngineOutput> CompressAsync(CompressContext ctx, JobScope job, CompressOptions o)
    {
        var info = await IdentifyAsync(ctx.Tools, job.InputPath, job.Token).ConfigureAwait(false);
        job.Report(0.15);
        var warnings = new List<string>();
        var ext = OutputExtension(job.InputPath, o, info.HasAlpha);
        if (ext == "jpg" && info.HasAlpha) warnings.Add("JPG não tem transparência: as partes transparentes ficaram brancas.");

        var level = o.ConvertOnly ? Level.Maxima : o.Level;
        var (quality, levelMax) = LevelParams(level);
        if (o.ConvertOnly) quality = 92;
        var originalSize = new FileInfo(job.InputPath).Length;
        long? target = o.ConvertOnly ? null : o.TargetBytes;
        // Já cabe na meta: comprime só pelo nível.
        if (target is { } tt && originalSize <= tt) target = null;

        var maxSide = o.ImageMaxSide > 0 ? o.ImageMaxSide : (target is null && !o.ConvertOnly ? levelMax : 0);
        var longSide = Math.Max(info.Width, info.Height);
        var side = maxSide > 0 ? Math.Min(maxSide, longSide) : longSide;
        var output = job.Temp("saida." + ext);

        await RunConvertAsync(ctx, job, o, ext, quality, side, longSide, target, output).ConfigureAwait(false);
        job.Report(0.5);

        if (target is { } tb)
        {
            // Procura caber na meta sem destruir a foto: se a qualidade cair demais, reduz as dimensões.
            for (var round = 0; round < 10; round++)
            {
                var size = new FileInfo(output).Length;
                var q = ext == "jpg" ? await JpegQualityAsync(ctx, output, job.Token).ConfigureAwait(false) : 100;
                var fits = size <= tb;
                if (fits && (q >= 55 || side <= 640)) break;
                if (side <= 320)
                {
                    warnings.Add("Não deu para chegar exatamente na meta de tamanho sem estragar a imagem.");
                    break;
                }
                // Quanto reduzir: pela área que falta (com folga), ou 15% quando a qualidade caiu demais.
                var ratio = fits ? 0.85 : Math.Clamp(Math.Sqrt((double)tb / size) * 0.95, 0.5, 0.9);
                side = Math.Max(320, (int)(side * ratio));
                await RunConvertAsync(ctx, job, o, ext, quality, side, longSide, tb, output).ConfigureAwait(false);
                job.Report(0.5 + 0.045 * (round + 1));
            }
            if (side < longSide) warnings.Add($"Para caber na meta, a imagem foi reduzida para {side} px no lado maior.");
        }

        var explicitFormat = o.ConvertOnly || o.ImageFormat is ImageFormat.Jpg or ImageFormat.WebP;
        return new EngineOutput
        {
            TempPath = output,
            Extension = ext,
            Warnings = warnings,
            EngineNote = $"{ext.ToUpperInvariant()}, {Dim(info, side, longSide)}",
            KeepEvenIfBigger = explicitFormat && FileTypes.Ext(job.InputPath) is var src && !SameFormat(src, ext),
        };
    }

    private static bool SameFormat(string srcExt, string outExt) =>
        srcExt == outExt || (outExt == "jpg" && srcExt is "jpeg" or "jfif");

    private static string Dim(ImageInfo info, int side, int longSide)
    {
        if (side >= longSide) return $"{info.Width}×{info.Height}";
        var f = (double)side / longSide;
        return $"{Math.Max(1, (int)Math.Round(info.Width * f))}×{Math.Max(1, (int)Math.Round(info.Height * f))}";
    }

    private static async Task RunConvertAsync(CompressContext ctx, JobScope job, CompressOptions o, string ext,
        int quality, int side, int longSide, long? target, string output)
    {
        var a = new List<string> { "-quiet", job.InputPath + "[0]", "-auto-orient" };
        // Privacidade: tira EXIF (GPS, câmera), XMP e IPTC; mantém o perfil de cor para as cores não mudarem.
        if (o.RemoveMetadata) a.AddRange(["+profile", "!icc,*", "+set", "comment"]);
        if (side < longSide) a.AddRange(["-filter", "Lanczos", "-resize", $"{side}x{side}>"]);
        switch (ext)
        {
            case "jpg":
                a.AddRange(["-background", "white", "-alpha", "remove", "-alpha", "off",
                            "-sampling-factor", "4:2:0", "-interlace", "JPEG", "-quality", quality.ToString(Inv)]);
                if (target is { } t) a.AddRange(["-define", $"jpeg:extent={Math.Max(8, (long)(t * 0.98 / 1024))}KB"]);
                break;
            case "webp":
                a.AddRange(["-quality", quality.ToString(Inv), "-define", "webp:method=6", "-define", "webp:alpha-quality=90"]);
                if (target is { } tw) a.AddRange(["-define", $"webp:target-size={(long)(tw * 0.97)}"]);
                break;
            default: // png sem perdas
                a.AddRange(["-define", "png:compression-level=9", "-define", "png:compression-strategy=1", "-define", "png:exclude-chunks=date,time"]);
                break;
        }
        a.Add(ext + ":" + output);

        await ctx.Pause.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
        var res = await ProcessRunner.RunAsync(ctx.Tools.MagickConvert, a, ctx.Tools.Environment, pause: ctx.Pause,
            timeout: TimeSpan.FromMinutes(10), workingDir: job.Dir, ct: job.Token).ConfigureAwait(false);
        if (!res.Ok || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            Log.Warn($"ImageMagick saiu com {res.ExitCode}: {res.StdErrTail}");
            throw new UserFacingException(res.StdErrTail.Contains("memory", StringComparison.OrdinalIgnoreCase)
                ? "Esta imagem é grande demais para a memória disponível."
                : "Não consegui compactar esta imagem. Ela pode estar corrompida.");
        }
    }

    private static async Task<int> JpegQualityAsync(CompressContext ctx, string path, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync(ctx.Tools.MagickIdentify, ["-quiet", "-format", "%Q", path],
            ctx.Tools.Environment, captureStdOut: true, timeout: TimeSpan.FromMinutes(1), ct: ct).ConfigureAwait(false);
        return int.TryParse(res.StdOut.Trim(), NumberStyles.Integer, Inv, out var q) ? q : 100;
    }
}
