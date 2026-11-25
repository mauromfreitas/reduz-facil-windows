using System.Globalization;
using ReduzFacil.Core.Media;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Compress;

/// <summary>Uma execução do FFmpeg (a 1ª passada do 2-pass é uma, a final é outra).</summary>
public sealed record FfmpegPass(IReadOnlyList<string> Args, double ProgressWeight);

public sealed class VideoPlan
{
    public required IReadOnlyList<FfmpegPass> Passes { get; init; }
    public required double OutputDurationSec { get; init; }
    public long VideoBps { get; init; }
    public long AudioBps { get; init; }
    public int OutWidth { get; init; }
    public int OutHeight { get; init; }
    public bool TwoPass { get; init; }
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Monta os comandos do FFmpeg para um vídeo. Função pura (sem rodar nada): fácil de testar.
/// </summary>
public static class VideoPlanner
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly int[] ShortSideSteps = [2160, 1440, 1080, 720, 540, 480, 360];

    /// <summary>Lado menor máximo de cada nível (0 = mantém).</summary>
    public static int LevelShortSide(Level level) => level switch
    {
        Level.Maxima => 0,
        Level.Equilibrado => 1080,
        _ => 720,
    };

    public static int AudioKbps(Level level) => level switch
    {
        Level.Maxima => 160,
        Level.Equilibrado => 128,
        _ => 96,
    };

    /// <summary>Qualidade constante por codificador e nível (número menor = melhor).</summary>
    public static int QualityValue(VideoEncoder enc, Level level)
    {
        var i = (int)level; // 0 máxima, 1 equilibrado, 2 menor
        return (enc.Family, enc.Codec) switch
        {
            (EncoderFamily.Cpu, VideoCodec.H264) => new[] { 21, 25, 29 }[i],
            (EncoderFamily.Cpu, VideoCodec.H265) => new[] { 23, 27, 31 }[i],
            (EncoderFamily.Nvidia, VideoCodec.H264) => new[] { 22, 26, 30 }[i],
            (EncoderFamily.Nvidia, VideoCodec.H265) => new[] { 24, 28, 32 }[i],
            (EncoderFamily.Intel, _) => new[] { 22, 26, 30 }[i],
            (EncoderFamily.Amd, _) => new[] { 21, 25, 29 }[i],
            _ => 25,
        };
    }

    /// <summary>Mínimo de "bits por pixel" para a imagem não virar borrão; abaixo disso a resolução cai.</summary>
    public static double MinBitsPerPixel(VideoCodec codec) => codec == VideoCodec.H265 ? 0.035 : 0.055;

    public static (double Start, double Duration) TrimWindow(MediaInfo info, CompressOptions o)
    {
        var total = info.DurationSec;
        var start = Math.Max(0, o.TrimStart?.TotalSeconds ?? 0);
        var end = o.TrimEnd?.TotalSeconds ?? total;
        if (total > 0) end = Math.Min(end, total);
        if (total > 0 && start >= total)
            throw new UserFacingException($"O início do corte ({Format.Duration(TimeSpan.FromSeconds(start))}) passa do fim do vídeo ({Format.Duration(TimeSpan.FromSeconds(total))}).");
        var dur = end - start;
        if (dur < 0.2)
            throw new UserFacingException("O fim do corte precisa ser depois do início.");
        return (start, dur);
    }

    /// <summary>
    /// Bits por segundo para caber na meta. Reserva ~4% para o "envelope" do MP4 e para a variação do codificador.
    /// </summary>
    public static (long VideoBps, long AudioBps) BitrateForTarget(long targetBytes, double durationSec, bool hasAudio)
    {
        if (durationSec <= 0) throw new UserFacingException("Não consegui ler a duração do vídeo para calcular a meta. Use \"Por qualidade\".");
        var totalBps = targetBytes * 8 * 0.96 / durationSec;
        long audio = !hasAudio ? 0 : totalBps > 2_000_000 ? 128_000 : totalBps > 600_000 ? 96_000 : 64_000;
        var video = (long)(totalBps - audio);
        const long minVideo = 60_000;
        if (video < minVideo)
        {
            var minBytes = (minVideo + audio) * durationSec / 8 / 0.96;
            var mb = Math.Ceiling(minBytes / (1024.0 * 1024.0) * 10) / 10;
            throw new UserFacingException(
                $"A meta é pequena demais para {Format.Duration(TimeSpan.FromSeconds(durationSec))} de vídeo. " +
                $"O mínimo para ficar assistível é cerca de {Format.Number(mb)} MB — ou corte um trecho menor.");
        }
        return (video, audio);
    }

    /// <summary>Tamanho de saída para um lado menor máximo (0 = mantém), sempre par.</summary>
    public static (int W, int H) ScaledSize(int w, int h, int maxShort)
    {
        if (w <= 0 || h <= 0) return (w, h);
        var shortSide = Math.Min(w, h);
        if (maxShort > 0 && shortSide > maxShort)
        {
            var f = (double)maxShort / shortSide;
            w = (int)Math.Round(w * f);
            h = (int)Math.Round(h * f);
        }
        return (w / 2 * 2, h / 2 * 2);
    }

    public static VideoPlan Build(
        MediaInfo info, CompressOptions o, VideoEncoder enc,
        string input, string output, string passLogName, long? videoBpsOverride = null)
    {
        var warnings = new List<string>();
        var (start, dur) = TrimWindow(info, o);
        var keepAudio = info.HasAudio && !o.RemoveAudio;
        var level = o.ConvertOnly ? Level.Maxima : o.Level;

        // Tamanho na tela, depois do giro pedido
        var dispW = info.DisplayWidth;
        var dispH = info.DisplayHeight;
        if (o.Rotate is 90 or 270) (dispW, dispH) = (dispH, dispW);

        var maxShort = o.VideoMaxShortSide > 0 ? o.VideoMaxShortSide : (o.ConvertOnly ? 0 : LevelShortSide(level));
        var fps = info.Fps;
        var limitFps = false;
        long vbps = 0, abps = keepAudio ? AudioKbps(level) * 1000L : 0;
        var target = o.TargetBytes is > 0 && !o.ConvertOnly;

        if (target)
        {
            (vbps, abps) = BitrateForTarget(o.TargetBytes!.Value, dur, keepAudio);
            if (videoBpsOverride is > 0) vbps = videoBpsOverride.Value;

            // Pouco bit para muitos pixels: baixa a resolução (e o quadro por segundo) até ficar nítido.
            var minBpp = MinBitsPerPixel(enc.Codec);
            var side = Math.Min(dispW, dispH);
            if (maxShort > 0) side = Math.Min(side, maxShort);
            double Bpp(int s, double f)
            {
                var (w, h) = ScaledSize(dispW, dispH, s);
                return w * h * f <= 0 ? 1 : vbps / (w * h * f);
            }
            if (fps > 31 && Bpp(side, fps) < minBpp)
            {
                limitFps = true;
                fps = 30;
            }
            foreach (var step in ShortSideSteps)
            {
                if (Bpp(side, fps) >= minBpp || side <= 360) break;
                if (step < side) side = step;
            }
            if (side < Math.Min(dispW, dispH))
            {
                if (maxShort == 0 || side < maxShort)
                    warnings.Add($"Para caber na meta, a resolução foi reduzida para {side}p.");
                maxShort = side;
            }
        }

        var (outW, outH) = ScaledSize(dispW, dispH, maxShort);

        // ---------- filtros ----------
        var filters = new List<string>();
        if (info.IsHdr)
        {
            // HDR (iPhone, câmeras novas) → cores normais (SDR); sem isso o vídeo fica "lavado" em outros aparelhos.
            filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        }
        switch (o.Rotate)
        {
            case 90: filters.Add("transpose=1"); break;
            case 180: filters.Add("hflip,vflip"); break;
            case 270: filters.Add("transpose=2"); break;
        }
        if (maxShort > 0)
            filters.Add($"scale=w='if(gte(iw,ih),-2,trunc(min({maxShort},iw)/2)*2)':h='if(gte(iw,ih),trunc(min({maxShort},ih)/2)*2,-2)':flags=lanczos");
        else
            filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        if (limitFps) filters.Add("fps=30");
        filters.Add(enc.Family == EncoderFamily.Intel ? "format=nv12" : "format=yuv420p");
        var vf = string.Join(",", filters);

        // ---------- entrada ----------
        var head = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        if (start > 0) head.AddRange(["-ss", Sec(start)]);
        head.AddRange(["-i", input]);
        if (o.TrimEnd is not null || start > 0) head.AddRange(["-t", Sec(dur)]);
        head.AddRange(["-map", "0:v:0", "-sn", "-dn", "-max_muxing_queue_size", "4096", "-vf", vf]);

        var video = VideoArgs(enc, level, target ? vbps : null);
        var twoPass = target && enc.Family == EncoderFamily.Cpu;
        var passes = new List<FfmpegPass>();

        if (twoPass)
        {
            var p1 = new List<string>(head);
            p1.AddRange(video);
            p1.AddRange(PassArgs(enc, 1, passLogName));
            p1.AddRange(["-an", "-f", "null", Engines.EngineTools.NullOutput]);
            passes.Add(new FfmpegPass(p1, 0.4));
        }

        var final = new List<string>(head);
        if (keepAudio) final.AddRange(["-map", "0:a:0"]);
        final.AddRange(video);
        if (twoPass) final.AddRange(PassArgs(enc, 2, passLogName));
        if (keepAudio)
        {
            final.AddRange(["-c:a", "aac", "-b:a", $"{abps / 1000}k"]);
            if (info.AudioChannels > 2) final.AddRange(["-ac", "2"]);
        }
        else final.Add("-an");
        if (o.RemoveMetadata)
        {
            final.AddRange(["-map_metadata", "-1", "-map_chapters", "-1"]);
            if (!string.IsNullOrEmpty(info.CreationTime)) final.AddRange(["-metadata", "creation_time=" + info.CreationTime]);
        }
        else final.AddRange(["-map_metadata", "0"]);
        if (enc.Codec == VideoCodec.H265) final.AddRange(["-tag:v", "hvc1"]);
        final.AddRange(["-movflags", "+faststart", output]);
        passes.Add(new FfmpegPass(final, twoPass ? 0.6 : 1.0));

        return new VideoPlan
        {
            Passes = passes,
            OutputDurationSec = dur,
            VideoBps = vbps,
            AudioBps = keepAudio ? abps : 0,
            OutWidth = outW,
            OutHeight = outH,
            TwoPass = twoPass,
            Warnings = warnings,
        };
    }

    private static IEnumerable<string> PassArgs(VideoEncoder enc, int pass, string logName)
    {
        if (enc.Codec == VideoCodec.H265)
            return ["-x265-params", $"log-level=error:pass={pass}:stats={logName}.x265"];
        return ["-pass", pass.ToString(Inv), "-passlogfile", logName];
    }

    private static List<string> VideoArgs(VideoEncoder enc, Level level, long? bps)
    {
        var a = new List<string> { "-c:v", enc.Name };
        string B(double f) => ((long)(bps!.Value * f)).ToString(Inv);
        switch (enc.Family)
        {
            case EncoderFamily.Cpu when enc.Codec == VideoCodec.H264:
                a.AddRange(["-preset", level == Level.Maxima ? "slow" : "medium", "-profile:v", "high"]);
                if (bps is null) a.AddRange(["-crf", QualityValue(enc, level).ToString(Inv)]);
                else a.AddRange(["-b:v", B(1)]);
                break;
            case EncoderFamily.Cpu:
                a.AddRange(["-preset", "medium"]);
                if (bps is null) a.AddRange(["-crf", QualityValue(enc, level).ToString(Inv), "-x265-params", "log-level=error"]);
                else a.AddRange(["-b:v", B(1)]);
                break;
            case EncoderFamily.Nvidia:
                a.AddRange(["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-spatial-aq", "1"]);
                if (bps is null) a.AddRange(["-cq", QualityValue(enc, level).ToString(Inv), "-b:v", "0"]);
                else a.AddRange(["-b:v", B(1), "-maxrate", B(1.4), "-bufsize", B(2), "-multipass", "fullres"]);
                break;
            case EncoderFamily.Intel:
                a.AddRange(["-preset", "medium"]);
                if (bps is null) a.AddRange(["-global_quality", QualityValue(enc, level).ToString(Inv)]);
                else a.AddRange(["-b:v", B(1), "-maxrate", B(1.4), "-bufsize", B(2)]);
                break;
            case EncoderFamily.Amd:
                a.AddRange(["-quality", "quality"]);
                if (bps is null)
                {
                    var q = QualityValue(enc, level);
                    a.AddRange(["-rc", "cqp", "-qp_i", q.ToString(Inv), "-qp_p", (q + 2).ToString(Inv), "-qp_b", (q + 4).ToString(Inv)]);
                }
                else a.AddRange(["-rc", "vbr_peak", "-b:v", B(1), "-maxrate", B(1.4), "-bufsize", B(2)]);
                break;
        }
        if (enc.Family == EncoderFamily.Cpu && enc.Codec == VideoCodec.H264) a.AddRange(["-pix_fmt", "yuv420p"]);
        return a;
    }

    private static string Sec(double s) => s.ToString("0.###", Inv);
}
