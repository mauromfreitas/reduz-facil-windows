using System.Globalization;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Media;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Output;

namespace ReduzFacil.Core.Compress;

public sealed class PreviewResult
{
    /// <summary>Imagens (PNG/JPG) que a tela consegue mostrar: antes e depois.</summary>
    public required string BeforeImage { get; init; }
    public required string AfterImage { get; init; }
    public long OriginalBytes { get; init; }
    /// <summary>Tamanho final (real, se já foi reduzido; estimado a partir de um trecho, no vídeo).</summary>
    public long FinalBytes { get; init; }
    public bool Estimated { get; init; }
    public bool KeptOriginal { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// "Antes e depois": gera imagens para comparar o original com o resultado, sem salvar nada no destino.
/// Foto: a foto inteira. Vídeo: um quadro do meio, a partir de um trecho de 4 s comprimido do mesmo jeito.
/// PDF: a primeira página.
/// </summary>
public sealed class PreviewMaker(CompressContext ctx)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Displayable = ["jpg", "jpeg", "png", "bmp"];

    public async Task<PreviewResult> MakeAsync(string input, CompressOptions o, string? existingOutput, CancellationToken ct)
    {
        var kind = FileTypes.KindOf(input) ?? throw new UserFacingException("Tipo não suportado.");
        var dir = Path.Combine(ctx.Tools.WorkDir, "previa-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return kind switch
        {
            FileKind.Image => await ImageAsync(input, o, existingOutput, dir, ct).ConfigureAwait(false),
            FileKind.Video => await VideoAsync(input, o, existingOutput, dir, ct).ConfigureAwait(false),
            FileKind.Pdf => await PdfAsync(input, o, existingOutput, dir, ct).ConfigureAwait(false),
            _ => throw new UserFacingException("Para áudio não há comparação visual."),
        };
    }

    /// <summary>Apaga prévias antigas (chamado ao abrir e fechar o app).</summary>
    public static void Clean(string workDir)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(workDir, "previa-*"))
                try { Directory.Delete(d, true); } catch { /* aberta na tela */ }
        }
        catch { /* sem pasta */ }
    }

    private OutputSettings TempOut(string dir) => new() { Mode = OutputMode.Folder, Folder = dir, Suffix = "-depois", KeepDates = false };

    private async Task<PreviewResult> ImageAsync(string input, CompressOptions o, string? existing, string dir, CancellationToken ct)
    {
        var original = new FileInfo(input).Length;
        string after;
        bool kept;
        long final;
        string note;
        if (existing is not null && File.Exists(existing))
        {
            after = existing;
            kept = false;
            final = new FileInfo(existing).Length;
            note = "Resultado salvo";
        }
        else
        {
            var r = await new Compressor(ctx).RunAsync(input, o, TempOut(dir), null, null, ct).ConfigureAwait(false);
            kept = r.KeptOriginal;
            after = r.OutputPath ?? input;
            final = r.KeptOriginal ? original : r.FinalBytes;
            note = r.EngineNote ?? "";
        }
        return new PreviewResult
        {
            BeforeImage = await DisplayableAsync(input, dir, "antes", ct).ConfigureAwait(false),
            AfterImage = await DisplayableAsync(after, dir, "depois", ct).ConfigureAwait(false),
            OriginalBytes = original,
            FinalBytes = final,
            KeptOriginal = kept,
            Note = note,
        };
    }

    /// <summary>HEIC, WebP e AVIF viram PNG para a tela conseguir mostrar.</summary>
    private async Task<string> DisplayableAsync(string path, string dir, string name, CancellationToken ct)
    {
        var ext = FileTypes.Ext(path);
        if (Displayable.Contains(ext) && Path.GetExtension(path).Length > 0) return path;
        var png = Path.Combine(dir, name + ".png");
        var r = await ProcessRunner.RunAsync(ctx.Tools.MagickConvert, ["-quiet", path + "[0]", "-auto-orient", png],
            ctx.Tools.Environment, timeout: TimeSpan.FromMinutes(3), ct: ct).ConfigureAwait(false);
        if (!r.Ok) throw new UserFacingException("Não consegui preparar a imagem para comparar.");
        return png;
    }

    private async Task<PreviewResult> VideoAsync(string input, CompressOptions o, string? existing, string dir, CancellationToken ct)
    {
        var info = await MediaProbe.ProbeAsync(ctx.Tools, input, ct).ConfigureAwait(false);
        if (!info.HasVideo) throw new UserFacingException("Este arquivo não tem imagem para comparar.");
        var (start, dur) = VideoPlanner.TrimWindow(info, o);
        var original = new FileInfo(input).Length;
        var mid = start + dur / 2;
        var before = Path.Combine(dir, "antes.png");
        await FrameAsync(input, mid, before, ct).ConfigureAwait(false);

        if (existing is not null && File.Exists(existing))
        {
            var after0 = Path.Combine(dir, "depois.png");
            await FrameAsync(existing, mid - start, after0, ct).ConfigureAwait(false);
            return new PreviewResult
            {
                BeforeImage = before, AfterImage = after0, OriginalBytes = original, FinalBytes = new FileInfo(existing).Length, Note = "Resultado salvo",
            };
        }

        // Trecho de até 4 s em volta do meio, com os mesmos ajustes; a meta é proporcional ao trecho.
        var clipLen = Math.Min(4, dur);
        var clipStart = Math.Max(start, mid - clipLen / 2);
        var clip = o.Clone();
        clip.TrimStart = TimeSpan.FromSeconds(clipStart);
        clip.TrimEnd = TimeSpan.FromSeconds(clipStart + clipLen);
        clip.ExtractAudioOnly = false;
        clip.SplitPartBytes = null;
        if (o.SplitPartBytes is { } part)
        {
            // Dividido: o trecho ganha a mesma "taxa" de uma parte.
            var n = Compressor.PartsFor(info, o, dur, part);
            clip.TargetBytes = Math.Max(1, (long)(part * clipLen / (dur / n)));
        }
        else if (o.TargetBytes is { } t && o.HasVideoEdits is false && original <= t) clip.TargetBytes = null;
        else if (o.TargetBytes is { } t2) clip.TargetBytes = Math.Max(1, (long)(t2 * clipLen / dur));
        var r = await new Compressor(ctx).RunAsync(input, clip, TempOut(dir), null, null, ct).ConfigureAwait(false);
        var after = Path.Combine(dir, "depois.png");
        await FrameAsync(r.OutputPath ?? input, (mid - clipStart), after, ct).ConfigureAwait(false);
        var est = (long)(r.FinalBytes * (dur / clipLen));
        return new PreviewResult
        {
            BeforeImage = before, AfterImage = after, OriginalBytes = original, FinalBytes = Math.Min(est, long.MaxValue),
            Estimated = true, Note = (r.EngineNote ?? "") + " · tamanho estimado por um trecho de " + clipLen.ToString("0", Inv) + " s",
        };
    }

    private async Task FrameAsync(string video, double atSec, string png, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(ctx.Tools.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-ss", Math.Max(0, atSec).ToString("0.###", Inv), "-i", video, "-frames:v", "1", png],
            ctx.Tools.Environment, timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);
        if (!r.Ok || !File.Exists(png)) throw new UserFacingException("Não consegui tirar um quadro do vídeo para comparar.");
    }

    private async Task<PreviewResult> PdfAsync(string input, CompressOptions o, string? existing, string dir, CancellationToken ct)
    {
        var original = new FileInfo(input).Length;
        string after;
        long final;
        var kept = false;
        var note = "Resultado salvo";
        if (existing is not null && File.Exists(existing))
        {
            after = existing;
            final = new FileInfo(existing).Length;
        }
        else
        {
            var r = await new Compressor(ctx).RunAsync(input, o, TempOut(dir), null, null, ct).ConfigureAwait(false);
            after = r.OutputPath ?? input;
            kept = r.KeptOriginal;
            final = kept ? original : r.FinalBytes;
            note = r.EngineNote ?? "";
        }
        return new PreviewResult
        {
            BeforeImage = await PageAsync(input, Path.Combine(dir, "antes.png"), ct).ConfigureAwait(false),
            AfterImage = await PageAsync(after, Path.Combine(dir, "depois.png"), ct).ConfigureAwait(false),
            OriginalBytes = original, FinalBytes = final, KeptOriginal = kept, Note = note,
        };
    }

    private async Task<string> PageAsync(string pdf, string png, CancellationToken ct)
    {
        var src = pdf;
        if (pdf.Any(c => c > 127))
        {
            src = Path.Combine(Path.GetDirectoryName(png)!, Guid.NewGuid().ToString("N")[..8] + ".pdf");
            File.Copy(pdf, src);
        }
        var r = await ProcessRunner.RunAsync(ctx.Tools.Ghostscript,
            ["-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=png16m", "-r120", "-dFirstPage=1", "-dLastPage=1",
             "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", "-sOutputFile=" + png, src],
            ctx.Tools.Environment, timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);
        if (!r.Ok || !File.Exists(png)) throw new UserFacingException("Não consegui mostrar a página do PDF.");
        return png;
    }
}
