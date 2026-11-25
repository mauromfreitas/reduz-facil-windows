using System.Globalization;
using System.Text.RegularExpressions;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Compress;

/// <summary>PDF pelo Ghostscript: recomprime as imagens (fotos, páginas escaneadas) e mantém o texto como texto.</summary>
public sealed partial class PdfCompressor : IKindCompressor
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Uma configuração: resolução das imagens (dpi) e "força" do JPEG (QFactor: maior = menor arquivo).</summary>
    public sealed record PdfStep(int Dpi, double QFactor);

    /// <summary>Da melhor para a mais forte. Os níveis usam 0, 2 e 4.</summary>
    public static readonly PdfStep[] Steps =
    [
        new(220, 0.40), new(180, 0.55), new(150, 0.76), new(130, 0.90), new(110, 1.10), new(96, 1.30), new(80, 1.60), new(72, 2.00),
    ];

    public static int LevelStep(Level level) => level switch
    {
        Level.Maxima => 0,
        Level.Equilibrado => 2,
        _ => 4,
    };

    [GeneratedRegex(@"Processing pages \d+ through (\d+)")]
    private static partial Regex TotalPagesRx();

    [GeneratedRegex(@"^Page (\d+)")]
    private static partial Regex PageRx();

    public async Task<EngineOutput> CompressAsync(CompressContext ctx, JobScope job, CompressOptions o)
    {
        if (o.ConvertOnly) throw new UserFacingException("PDF não precisa de conversão: use \"Por qualidade\" ou uma meta para reduzir.");
        var input = job.InputPath;
        // Ghostscript e caminhos com acentos às vezes brigam: trabalha numa cópia com nome simples.
        if (!IsPlainAscii(input))
        {
            var copy = job.Temp("entrada.pdf");
            File.Copy(input, copy, overwrite: true);
            input = copy;
        }

        var warnings = new List<string>();
        var originalSize = new FileInfo(job.InputPath).Length;
        var output = job.Temp("saida.pdf");
        long? target = o.TargetBytes;
        if (target is { } t0 && originalSize <= t0) target = null;

        int used;
        if (target is not { } tb)
        {
            used = LevelStep(o.Level);
            await RunAsync(ctx, job, o, input, output, Steps[used], 0.02, 0.98).ConfigureAwait(false);
        }
        else
        {
            // Busca binária pela melhor configuração que cabe na meta (cada tentativa é uma passada completa).
            int lo = 0, hi = Steps.Length - 1, best = -1, tries = 0;
            var attempt = job.Temp("tentativa.pdf");
            var maxTries = (int)Math.Ceiling(Math.Log2(Steps.Length)) + 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var from = 0.02 + 0.96 * tries / maxTries;
                var to = 0.02 + 0.96 * (tries + 1) / maxTries;
                await RunAsync(ctx, job, o, input, attempt, Steps[mid], from, Math.Min(to, 0.98)).ConfigureAwait(false);
                tries++;
                if (new FileInfo(attempt).Length <= tb)
                {
                    best = mid;
                    File.Copy(attempt, output, overwrite: true);
                    hi = mid - 1;
                }
                else lo = mid + 1;
            }
            if (best < 0)
            {
                // Nem a mais forte coube: entrega a mais forte e avisa (texto e desenhos não encolhem mais).
                best = Steps.Length - 1;
                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                    await RunAsync(ctx, job, o, input, output, Steps[best], 0.9, 0.98).ConfigureAwait(false);
                warnings.Add($"Não deu para chegar na meta: o menor possível foi {Format.Bytes(new FileInfo(output).Length)}. " +
                             "O que sobra é texto e desenho, que não encolhem mais.");
            }
            used = best;
        }

        return new EngineOutput
        {
            TempPath = output,
            Extension = "pdf",
            Warnings = warnings,
            EngineNote = $"imagens em {Steps[used].Dpi} dpi" + (o.PdfGrayscale ? ", tons de cinza" : ""),
        };
    }

    public static List<string> BuildArgs(CompressOptions o, string input, string output, PdfStep step)
    {
        var dpi = step.Dpi.ToString(Inv);
        var mono = Math.Max(300, step.Dpi * 2).ToString(Inv);
        var a = new List<string>
        {
            "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.5",
            "-dDetectDuplicateImages=true", "-dCompressFonts=true", "-dSubsetFonts=true", "-dCompressPages=true",
            "-dPassThroughJPEGImages=false", "-dPassThroughJPXImages=false",
            "-dAutoFilterColorImages=false", "-dAutoFilterGrayImages=false",
            "-dColorImageFilter=/DCTEncode", "-dGrayImageFilter=/DCTEncode",
            "-dDownsampleColorImages=true", "-dDownsampleGrayImages=true", "-dDownsampleMonoImages=true",
            "-dColorImageDownsampleType=/Bicubic", "-dGrayImageDownsampleType=/Bicubic", "-dMonoImageDownsampleType=/Subsample",
            "-dColorImageDownsampleThreshold=1.1", "-dGrayImageDownsampleThreshold=1.1", "-dMonoImageDownsampleThreshold=1.1",
            $"-dColorImageResolution={dpi}", $"-dGrayImageResolution={dpi}", $"-dMonoImageResolution={mono}",
        };
        if (o.PdfGrayscale) a.AddRange(["-sColorConversionStrategy=Gray", "-dProcessColorModel=/DeviceGray"]);
        a.Add("-sOutputFile=" + output);
        var q = step.QFactor.ToString("0.00", Inv);
        var hs = step.QFactor >= 0.76 ? "[2 1 1 2]" : "[1 1 1 1]";
        a.AddRange(["-c",
            $"<< /ColorACSImageDict << /QFactor {q} /Blend 1 /HSamples {hs} /VSamples {hs} >> " +
            $"/GrayACSImageDict << /QFactor {q} /Blend 1 /HSamples [1 1 1 1] /VSamples [1 1 1 1] >> " +
            $"/ColorImageDict << /QFactor {q} /Blend 1 /HSamples {hs} /VSamples {hs} >> " +
            $"/GrayImageDict << /QFactor {q} /Blend 1 /HSamples [1 1 1 1] /VSamples [1 1 1 1] >> >> setdistillerparams",
            "-f", input]);
        return a;
    }

    private static async Task RunAsync(CompressContext ctx, JobScope job, CompressOptions o, string input, string output,
        PdfStep step, double from, double to)
    {
        var total = 0;
        var needsPassword = false;
        var noPages = false;
        void Watch(string line)
        {
            if (line.Contains("requires a password", StringComparison.OrdinalIgnoreCase)) needsPassword = true;
            if (line.Contains("No pages will be processed", StringComparison.OrdinalIgnoreCase)) noPages = true;
        }
        void OnLine(string line)
        {
            Watch(line);
            var m = TotalPagesRx().Match(line);
            if (m.Success) { total = int.Parse(m.Groups[1].Value, Inv); return; }
            var p = PageRx().Match(line);
            if (p.Success && total > 0) job.Report(from + (to - from) * Math.Min(1, int.Parse(p.Groups[1].Value, Inv) / (double)total));
        }

        await ctx.Pause.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
        var res = await ProcessRunner.RunAsync(ctx.Tools.Ghostscript, BuildArgs(o, input, output, step), ctx.Tools.Environment,
            onStdOut: OnLine, onStdErr: Watch, pause: ctx.Pause, workingDir: job.Dir, ct: job.Token).ConfigureAwait(false);
        var outText = res.StdErrTail;
        if (needsPassword || outText.Contains("password", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("Este PDF é protegido por senha. Tire a senha e tente de novo.");
        if (noPages)
        {
            Log.Warn("Ghostscript não processou nenhuma página: " + outText);
            throw new UserFacingException("Não consegui ler as páginas deste PDF. Ele pode estar corrompido.");
        }
        if (!res.Ok || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            Log.Warn($"Ghostscript saiu com {res.ExitCode}: {outText}");
            throw new UserFacingException("Não consegui compactar este PDF. Ele pode estar corrompido.");
        }
        if (outText.Contains("**** Error", StringComparison.OrdinalIgnoreCase) || outText.Contains("**** Warning", StringComparison.OrdinalIgnoreCase))
            Log.Warn("Ghostscript avisou: " + outText);
    }

    private static bool IsPlainAscii(string path) => path.All(c => c < 128);
}
