using ReduzFacil.Core.Compress;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Media;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Scan;

public sealed class ScanHit
{
    public required string Path { get; init; }
    public required FileKind Kind { get; init; }
    public long SizeBytes { get; init; }
    public long EstimatedBytes { get; init; }
    public long SavingBytes => Math.Max(0, SizeBytes - EstimatedBytes);
    public int SavingPercent => Format.SavingPercent(SizeBytes, EstimatedBytes);
    public string Detail { get; init; } = "";
    public DateTime Modified { get; init; }
}

public sealed record ScanProgress(int FilesSeen, int Hits, long PotentialBytes, string? Current);

/// <summary>
/// "Onde está o peso": varre uma pasta ou disco e estima quanto cada arquivo encolheria no nível escolhido.
/// Estimativa rápida (cabeçalhos; ffprobe só para vídeos) — o número real aparece depois de reduzir.
/// </summary>
public sealed class WeightScanner(EngineTools tools)
{
    /// <summary>Bytes por pixel de um JPG em cada nível (fotos reais ficam por aí).</summary>
    public static double ImageBytesPerPixel(Level l) => l switch { Level.Maxima => 0.32, Level.Equilibrado => 0.22, _ => 0.16 };

    /// <summary>Bitrate típico (bps) de vídeo H.264 por lado menor, no nível Equilibrado a 30 fps.</summary>
    public static double TypicalVideoBps(int shortSide) => shortSide switch
    {
        >= 2000 => 14_000_000,
        >= 1400 => 8_000_000,
        >= 1000 => 4_500_000,
        >= 700 => 2_300_000,
        >= 520 => 1_500_000,
        >= 460 => 1_100_000,
        _ => 700_000,
    };

    public async Task<List<ScanHit>> ScanAsync(string root, CompressOptions o, long minBytes,
        IProgress<ScanProgress>? progress, CancellationToken ct, string? ownSuffix = "-reduzido")
    {
        var hits = new List<ScanHit>();
        var seen = 0;
        long potential = 0;
        var lastReport = DateTime.MinValue;

        foreach (var path in FileCollector.Walk(Path.GetFullPath(root), ct))
        {
            ct.ThrowIfCancellationRequested();
            seen++;
            var kind = FileTypes.KindOf(path);
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new ScanProgress(seen, hits.Count, potential, path));
            }
            if (kind is null || FileTypes.IsTemporary(path) || FileCollector.IsOwnOutput(path, ownSuffix)) continue;
            FileInfo fi;
            try { fi = new FileInfo(path); } catch { continue; }
            if (fi.Length < minBytes) continue;

            ScanHit? hit = null;
            try
            {
                hit = kind switch
                {
                    FileKind.Image => EstimateImage(fi, o),
                    FileKind.Video => await EstimateVideoAsync(fi, o, ct).ConfigureAwait(false),
                    FileKind.Audio => EstimateAudio(fi, o),
                    FileKind.Pdf => EstimatePdf(fi, o),
                    _ => null,
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Estimativa falhou em {path}: {ex.Message}");
            }
            // Só vale a pena mostrar o que economiza de verdade: 20% ou mais e pelo menos 300 KB.
            if (hit is not null && hit.SavingPercent >= 20 && hit.SavingBytes >= 300 * 1024)
            {
                hits.Add(hit);
                potential += hit.SavingBytes;
            }
        }
        progress?.Report(new ScanProgress(seen, hits.Count, potential, null));
        return hits.OrderByDescending(h => h.SavingBytes).ToList();
    }

    public static ScanHit? EstimateImage(FileInfo fi, CompressOptions o)
    {
        var dims = HeaderReader.ImageSize(fi.FullName);
        var ext = FileTypes.Ext(fi.FullName);
        int w, h;
        var alpha = false;
        if (dims is { Width: > 0, Height: > 0 })
        {
            (w, h, alpha) = (dims.Width, dims.Height, dims.HasAlpha);
        }
        else if (ext is "heic" or "heif" or "avif")
        {
            // HEIC já é bem compacto; foto típica de iPhone é 12 MP.
            (w, h) = (4032, 3024);
        }
        else return null;

        var (_, maxSide) = ImageCompressor.LevelParams(o.Level);
        if (o.ImageMaxSide > 0) maxSide = o.ImageMaxSide;
        var longSide = Math.Max(w, h);
        double scale = maxSide > 0 && longSide > maxSide ? (double)maxSide / longSide : 1;
        var pixels = w * scale * (h * scale);
        var bpp = ImageBytesPerPixel(o.Level) * (alpha ? 0.8 : 1);
        var est = (long)Math.Min(fi.Length, pixels * bpp + 20_000);
        return new ScanHit
        {
            Path = fi.FullName, Kind = FileKind.Image, SizeBytes = fi.Length, EstimatedBytes = est, Modified = fi.LastWriteTime,
            Detail = dims is null ? "foto" : $"{w}×{h}",
        };
    }

    private async Task<ScanHit?> EstimateVideoAsync(FileInfo fi, CompressOptions o, CancellationToken ct)
    {
        MediaInfo info;
        try
        {
            info = await MediaProbe.ProbeAsync(tools, fi.FullName, ct).ConfigureAwait(false);
        }
        catch (UserFacingException)
        {
            return null;
        }
        if (!info.HasVideo || info.DurationSec <= 0) return null;
        var maxShort = o.VideoMaxShortSide > 0 ? o.VideoMaxShortSide : VideoPlanner.LevelShortSide(o.Level);
        var shortSide = Math.Min(info.Width, info.Height);
        if (maxShort > 0) shortSide = Math.Min(shortSide, maxShort);
        var levelFactor = o.Level switch { Level.Maxima => 1.5, Level.Equilibrado => 1.0, _ => 0.65 };
        var codecFactor = o.VideoCodec == VideoCodec.H265 ? 0.6 : 1.0;
        var fpsFactor = Math.Sqrt(Math.Clamp(info.Fps, 15, 120) / 30.0);
        var vbps = TypicalVideoBps(shortSide) * levelFactor * codecFactor * fpsFactor;
        var abps = info.HasAudio ? VideoPlanner.AudioKbps(o.Level) * 1000.0 : 0;
        var est = (long)Math.Min(fi.Length, info.DurationSec * (vbps + abps) / 8 * 1.02);
        return new ScanHit
        {
            Path = fi.FullName, Kind = FileKind.Video, SizeBytes = fi.Length, EstimatedBytes = est, Modified = fi.LastWriteTime,
            Detail = $"{info.DisplayWidth}×{info.DisplayHeight}, {Format.Duration(TimeSpan.FromSeconds(info.DurationSec))}",
        };
    }

    public static ScanHit? EstimateAudio(FileInfo fi, CompressOptions o)
    {
        var ext = FileTypes.Ext(fi.FullName);
        // MP3, M4A, OGG... já são comprimidos: só WAV, FLAC e AIFF valem a pena.
        if (ext is not ("wav" or "flac" or "aiff" or "aif")) return null;
        var dur = HeaderReader.AudioDuration(fi.FullName);
        long est;
        if (dur is { } d && d > 0) est = (long)(d * AudioCompressor.LevelKbps(o.Level) * 1000 / 8);
        else if (ext is "aiff" or "aif") est = (long)(fi.Length * AudioCompressor.LevelKbps(o.Level) / 1411.0);
        else return null;
        return new ScanHit
        {
            Path = fi.FullName, Kind = FileKind.Audio, SizeBytes = fi.Length, EstimatedBytes = Math.Min(fi.Length, est), Modified = fi.LastWriteTime,
            Detail = dur is { } dd ? Format.Duration(TimeSpan.FromSeconds(dd)) : ext.ToUpperInvariant(),
        };
    }

    public static ScanHit? EstimatePdf(FileInfo fi, CompressOptions o)
    {
        var pages = HeaderReader.PdfPages(fi.FullName);
        var perPage = o.Level switch { Level.Maxima => 220_000, Level.Equilibrado => 120_000, _ => 70_000 };
        long est;
        if (pages is { } p && p > 0)
        {
            var avg = fi.Length / (double)p;
            // Páginas leves (texto) quase não encolhem.
            est = avg <= perPage * 1.3 ? (long)(fi.Length * 0.9) : (long)(p * perPage + 30_000);
        }
        else est = (long)(fi.Length * (fi.Length > 5 * 1024 * 1024 ? 0.5 : 0.85));
        return new ScanHit
        {
            Path = fi.FullName, Kind = FileKind.Pdf, SizeBytes = fi.Length, EstimatedBytes = Math.Min(fi.Length, est), Modified = fi.LastWriteTime,
            Detail = pages is { } pp ? $"{pp} página{(pp == 1 ? "" : "s")}" : "PDF",
        };
    }
}
