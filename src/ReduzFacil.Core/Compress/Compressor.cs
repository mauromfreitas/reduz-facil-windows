using ReduzFacil.Core.Model;
using ReduzFacil.Core.Output;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Compress;

/// <summary>
/// Entrada única: escolhe o compressor pelo tipo, aplica as regras de segurança e salva o resultado.
/// Regras: o original nunca é tocado; se não houver ganho, nada é salvo (a não ser numa conversão pedida).
/// </summary>
public sealed class Compressor(CompressContext ctx)
{
    private static readonly object NameGate = new();

    public CompressContext Context => ctx;

    public static IKindCompressor For(FileKind kind) => kind switch
    {
        FileKind.Image => new ImageCompressor(),
        FileKind.Video => new VideoCompressor(),
        FileKind.Audio => new AudioCompressor(),
        FileKind.Pdf => new PdfCompressor(),
        _ => throw new UserFacingException("Tipo de arquivo não suportado."),
    };

    public async Task<CompressResult> RunAsync(string inputPath, CompressOptions o, OutputSettings output,
        string? sourceRoot = null, Action<double>? progress = null, CancellationToken ct = default)
    {
        var kind = FileTypes.KindOf(inputPath) ?? throw new UserFacingException("Este tipo de arquivo não é suportado.");
        var src = new FileInfo(inputPath);
        if (!src.Exists) throw new UserFacingException("O arquivo não existe mais (foi movido ou apagado).");
        if (src.Length == 0) throw new UserFacingException("O arquivo está vazio.");
        var originalSize = src.Length;
        var originalDate = src.LastWriteTimeUtc;

        using var job = new JobScope(ctx.Tools, inputPath, progress, ct);
        var result = await For(kind).CompressAsync(ctx, job, o).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var outSize = new FileInfo(result.TempPath).Length;
        var warnings = new List<string>(result.Warnings);

        var bigger = outSize >= originalSize;
        if (bigger && !result.KeepEvenIfBigger)
        {
            var extra = o.TargetBytes is { } t && originalSize > t ? " Ele continua acima da meta escolhida." : "";
            warnings.Insert(0, "Este arquivo já estava bem otimizado: o original foi mantido e nada novo foi salvo." + extra);
            job.Report(1);
            return new CompressResult
            {
                InputPath = inputPath, OriginalBytes = originalSize, FinalBytes = originalSize,
                KeptOriginal = true, Warnings = warnings, EngineNote = result.EngineNote,
            };
        }
        if (bigger) warnings.Add("O arquivo convertido ficou maior que o original (o formato novo ocupa mais espaço).");

        string final;
        lock (NameGate)
        {
            final = OutputNamer.Decide(output, inputPath, result.Extension, sourceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            // Move de dentro da pasta de trabalho: o arquivo só aparece no destino quando está completo.
            File.Move(result.TempPath, final);
        }
        try
        {
            if (output.KeepDates) File.SetLastWriteTimeUtc(final, originalDate);
        }
        catch (Exception ex)
        {
            Log.Warn($"Não consegui ajustar a data de {final}: {ex.Message}");
        }

        job.Report(1);
        Log.Info($"OK {kind} {Path.GetFileName(inputPath)}: {Format.Bytes(originalSize)} → {Format.Bytes(outSize)} ({result.EngineNote})");
        return new CompressResult
        {
            InputPath = inputPath, OriginalBytes = originalSize, OutputPath = final, FinalBytes = outSize,
            Warnings = warnings, EngineNote = result.EngineNote,
        };
    }
}
