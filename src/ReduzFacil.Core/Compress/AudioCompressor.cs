using ReduzFacil.Core.Media;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Compress;

public sealed class AudioCompressor : IKindCompressor
{
    public static int LevelKbps(Level level) => level switch
    {
        Level.Maxima => 192,
        Level.Equilibrado => 128,
        _ => 96,
    };

    /// <summary>Formato de saída: MP3 ou M4A (AAC).</summary>
    public static string OutputExtension(string inputPath, CompressOptions o, bool fromVideo)
    {
        if (o.ConvertOnly) return "mp3";
        return o.AudioFormat switch
        {
            AudioFormat.Mp3 => "mp3",
            AudioFormat.M4a => "m4a",
            _ => fromVideo ? "mp3" : FileTypes.Ext(inputPath) is "m4a" or "aac" ? "m4a" : "mp3",
        };
    }

    /// <summary>kbps para caber na meta (entre 32 e 320).</summary>
    public static int KbpsForTarget(long targetBytes, double durationSec)
    {
        if (durationSec <= 0) throw new UserFacingException("Não consegui ler a duração do áudio para calcular a meta.");
        var kbps = (int)Math.Floor(targetBytes * 8 * 0.97 / durationSec / 1000);
        if (kbps < 32)
        {
            var minMb = 32_000 * durationSec / 8 / 0.97 / (1024 * 1024);
            throw new UserFacingException($"A meta é pequena demais para {Format.Duration(TimeSpan.FromSeconds(durationSec))} de áudio. O mínimo é cerca de {Format.Number(Math.Ceiling(minMb * 10) / 10)} MB.");
        }
        return Math.Min(kbps, 320);
    }

    public async Task<EngineOutput> CompressAsync(CompressContext ctx, JobScope job, CompressOptions o)
    {
        var info = await MediaProbe.ProbeAsync(ctx.Tools, job.InputPath, job.Token).ConfigureAwait(false);
        if (!info.HasAudio) throw new UserFacingException("Não encontrei som neste arquivo.");
        return await CompressProbedAsync(ctx, job, o, info, fromVideo: false).ConfigureAwait(false);
    }

    internal async Task<EngineOutput> CompressProbedAsync(CompressContext ctx, JobScope job, CompressOptions o, MediaInfo info, bool fromVideo)
    {
        var ext = OutputExtension(job.InputPath, o, fromVideo);
        var (start, dur) = VideoPlanner.TrimWindow(info, o);
        var level = o.ConvertOnly ? Level.Maxima : o.Level;
        var kbps = o.TargetBytes is { } t && !o.ConvertOnly ? KbpsForTarget(t, dur) : LevelKbps(level);
        // Não aumenta a taxa de um áudio que já é mais leve (só gastaria espaço).
        var srcKbps = (int)((info.AudioBitrate > 0 ? info.AudioBitrate : info.Bitrate) / 1000);
        if (srcKbps > 0 && !fromVideo && kbps > srcKbps && o.TargetBytes is null) kbps = Math.Max(64, srcKbps);

        var output = job.Temp("saida." + ext);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        if (start > 0) args.AddRange(["-ss", start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", job.InputPath]);
        if (o.TrimEnd is not null || start > 0) args.AddRange(["-t", dur.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
        args.AddRange(["-map", "0:a:0"]);
        // Capa do álbum (MP3) continua no arquivo.
        var keepCover = ext == "mp3" && info.HasCoverArt && !fromVideo;
        if (keepCover) args.AddRange(["-map", "0:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        else args.Add("-vn");
        args.AddRange(ext == "mp3"
            ? ["-c:a", "libmp3lame", "-b:a", $"{kbps}k"]
            : ["-c:a", "aac", "-b:a", $"{kbps}k", "-movflags", "+faststart"]);
        if (info.AudioChannels > 2) args.AddRange(["-ac", "2"]);
        args.AddRange(o.RemoveMetadata && fromVideo ? ["-map_metadata", "-1"] : ["-map_metadata", "0"]);
        if (ext == "mp3") args.AddRange(["-id3v2_version", "3"]);
        args.Add(output);

        try
        {
            await FfmpegRun.RunPassesAsync(ctx, job, [new FfmpegPass(args, 1)], dur, 0.03, 0.99).ConfigureAwait(false);
        }
        catch (FfmpegFailedException) when (keepCover)
        {
            // Capa num formato que o MP3 não aceita: tenta sem a capa.
            var noCover = args.ToList();
            var i = noCover.IndexOf("0:v:0");
            noCover.RemoveRange(i - 1, 6);
            noCover.Insert(i - 1, "-vn");
            await FfmpegRun.RunPassesAsync(ctx, job, [new FfmpegPass(noCover, 1)], dur, 0.03, 0.99).ConfigureAwait(false);
        }

        var warnings = new List<string>();
        var size = new FileInfo(output).Length;
        if (o.TargetBytes is { } tb && size > tb) warnings.Add($"Ficou um pouco acima da meta ({Format.Bytes(size)}).");
        return new EngineOutput
        {
            TempPath = output,
            Extension = ext,
            Warnings = warnings,
            EngineNote = $"{ext.ToUpperInvariant()} {kbps} kbps",
            KeepEvenIfBigger = fromVideo || o.ConvertOnly || o.TrimStart is not null || o.TrimEnd is not null,
        };
    }
}
