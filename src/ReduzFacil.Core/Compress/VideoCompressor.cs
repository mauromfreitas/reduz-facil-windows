using ReduzFacil.Core.Media;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Compress;

public sealed class VideoCompressor : IKindCompressor
{
    public async Task<EngineOutput> CompressAsync(CompressContext ctx, JobScope job, CompressOptions o)
    {
        var info = await MediaProbe.ProbeAsync(ctx.Tools, job.InputPath, job.Token).ConfigureAwait(false);
        job.Report(0.02);

        if (o.ExtractAudioOnly || !info.HasVideo)
        {
            if (!info.HasAudio) throw new UserFacingException("Este vídeo não tem som para extrair.");
            return await new AudioCompressor().CompressProbedAsync(ctx, job, o, info, fromVideo: true).ConfigureAwait(false);
        }
        if (o.RemoveAudio && !info.HasVideo) throw new UserFacingException("Este arquivo só tem som.");

        var opts = o.Clone();
        var originalSize = new FileInfo(job.InputPath).Length;
        // Já cabe na meta e não há corte: só melhora por qualidade (nunca passa do original).
        if (opts.TargetBytes is { } t && originalSize <= t && !opts.HasVideoEdits) opts.TargetBytes = null;

        if (opts.UseHardware) await ctx.Hardware.DetectAsync(ctx.Tools, job.Token).ConfigureAwait(false);
        var enc = ctx.Hardware.Choose(opts.VideoCodec, opts.UseHardware);
        var output = job.Temp("saida.mp4");
        var warnings = new List<string>();

        long? bpsOverride = null;
        long lastSize = 0;
        VideoPlan plan = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            plan = VideoPlanner.Build(info, opts, enc, job.InputPath, output, "passe", bpsOverride);
            try
            {
                await FfmpegRun.RunPassesAsync(ctx, job, plan.Passes, plan.OutputDurationSec, 0.03, 0.99).ConfigureAwait(false);
            }
            catch (FfmpegFailedException ex) when (enc.IsHardware)
            {
                // Placa de vídeo falhou (driver, formato incomum): refaz no processador.
                Log.Warn($"{enc.Name} falhou em {Path.GetFileName(job.InputPath)}; refazendo no processador. {ex.StdErr}");
                ctx.Hardware.ReportFailure(enc);
                enc = VideoEncoder.Cpu(opts.VideoCodec);
                warnings.Add("A placa de vídeo não conseguiu processar este vídeo; ele foi feito pelo processador.");
                attempt--;
                continue;
            }

            lastSize = new FileInfo(output).Length;
            if (opts.TargetBytes is not { } target || lastSize <= target) break;
            // Passou da meta (comum na placa de vídeo, que não faz 2 passadas): tenta de novo com menos bits.
            var factor = (double)target / lastSize * 0.95;
            bpsOverride = (long)(plan.VideoBps * factor);
            Log.Info($"Meta {Format.Bytes(target)} não atingida ({Format.Bytes(lastSize)}); nova tentativa com {bpsOverride / 1000} kbps");
            if (bpsOverride < 40_000) break;
        }

        warnings.AddRange(plan.Warnings);
        if (opts.TargetBytes is { } tb && lastSize > tb)
            warnings.Add($"Ficou um pouco acima da meta ({Format.Bytes(lastSize)}). Tente uma meta menor ou corte um trecho.");

        return new EngineOutput
        {
            TempPath = output,
            Extension = "mp4",
            Warnings = warnings,
            EngineNote = $"{enc.Label}, {plan.OutWidth}×{plan.OutHeight}" + (info.IsHdr ? ", HDR convertido" : ""),
            KeepEvenIfBigger = o.ConvertOnly || o.HasVideoEdits,
        };
    }
}
