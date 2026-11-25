using System.Globalization;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Compress;

/// <summary>Roda passadas do FFmpeg lendo o progresso (-progress pipe:1).</summary>
internal static class FfmpegRun
{
    public static async Task RunPassesAsync(CompressContext ctx, JobScope job, IReadOnlyList<FfmpegPass> passes,
        double durationSec, double progressFrom, double progressTo)
    {
        var done = 0.0;
        foreach (var pass in passes)
        {
            var baseP = done;
            var weight = pass.ProgressWeight;
            void OnLine(string line)
            {
                if (durationSec <= 0) return;
                long us = -1;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out us);
                else if (line.StartsWith("out_time_ms=", StringComparison.Ordinal)) long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out us); // também é microssegundo
                if (us < 0) return;
                var f = Math.Clamp(us / 1_000_000.0 / durationSec, 0, 1);
                job.Report(progressFrom + (progressTo - progressFrom) * (baseP + weight * f));
            }

            await ctx.Pause.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
            var res = await ProcessRunner.RunAsync(ctx.Tools.Ffmpeg, pass.Args, ctx.Tools.Environment,
                onStdOut: OnLine, pause: ctx.Pause, workingDir: job.Dir, ct: job.Token).ConfigureAwait(false);
            if (!res.Ok)
            {
                Log.Warn($"FFmpeg saiu com {res.ExitCode}: {res.StdErrTail}");
                throw new FfmpegFailedException(res.StdErrTail);
            }
            done += weight;
            job.Report(progressFrom + (progressTo - progressFrom) * done);
        }
    }

    public static string Friendly(string stderr)
    {
        var s = stderr.ToLowerInvariant();
        if (s.Contains("no space left")) return "Acabou o espaço no disco durante a compressão.";
        if (s.Contains("invalid data found") || s.Contains("moov atom not found"))
            return "Este arquivo parece estar corrompido ou incompleto (o FFmpeg não conseguiu ler).";
        if (s.Contains("permission denied")) return "O Windows não deixou ler ou gravar o arquivo (permissão negada).";
        return "O motor de vídeo não conseguiu processar este arquivo.";
    }
}

internal sealed class FfmpegFailedException(string stderr) : Exception(FfmpegRun.Friendly(stderr))
{
    public string StdErr { get; } = stderr;
}
