using System.Globalization;
using System.Text.RegularExpressions;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Compress;

/// <summary>Ferramentas de PDF: juntar fotos num PDF e separar páginas.</summary>
public sealed partial class PdfTools(EngineTools tools)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Resolução das páginas e qualidade do JPEG por nível.</summary>
    public static (int Dpi, int Quality, int MaxSide) LevelParams(Level l) => l switch
    {
        Level.Maxima => (200, 88, 3000),
        Level.Equilibrado => (150, 80, 2000),
        _ => (110, 70, 1400),
    };

    /// <summary>Junta fotos (na ordem dada) num PDF. A4 = cada foto centralizada numa página A4 (em pé ou deitada, conforme a foto).</summary>
    public async Task<string> ImagesToPdfAsync(IReadOnlyList<string> images, string output, bool a4, Level level,
        Action<double>? progress, CancellationToken ct)
    {
        if (images.Count == 0) throw new UserFacingException("Escolha pelo menos uma foto.");
        var (dpi, q, maxSide) = LevelParams(level);
        var dir = Path.Combine(tools.WorkDir, "pdf-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var pages = new List<string>();
            for (var i = 0; i < images.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var info = await ImageCompressor.IdentifyAsync(tools, images[i], ct).ConfigureAwait(false);
                var page = $"p{i:00000}.pdf";
                var a = new List<string> { "-quiet", images[i] + "[0]", "-auto-orient", "-background", "white", "-alpha", "remove", "-alpha", "off" };
                if (a4)
                {
                    // A4 = 210×297 mm. Página deitada para foto deitada (depois do giro automático).
                    var landscape = info.Width > info.Height;
                    var w = (int)Math.Round(210 / 25.4 * dpi);
                    var h = (int)Math.Round(297 / 25.4 * dpi);
                    if (landscape) (w, h) = (h, w);
                    var margin = (int)(dpi * 0.4); // ~1 cm
                    a.AddRange(["-resize", $"{w - 2 * margin}x{h - 2 * margin}>", "-gravity", "center", "-extent", $"{w}x{h}"]);
                }
                else a.AddRange(["-resize", $"{maxSide}x{maxSide}>"]);
                a.AddRange(["+profile", "!icc,*", "-units", "PixelsPerInch", "-density", dpi.ToString(Inv),
                            "-compress", "JPEG", "-quality", q.ToString(Inv), page]);
                var r = await ProcessRunner.RunAsync(tools.MagickConvert, a, tools.Environment, timeout: TimeSpan.FromMinutes(5),
                    workingDir: dir, ct: ct).ConfigureAwait(false);
                if (!r.Ok || !File.Exists(Path.Combine(dir, page)))
                {
                    Log.Warn($"Foto → PDF falhou ({images[i]}): {r.StdErrTail}");
                    throw new UserFacingException($"Não consegui usar a foto {Path.GetFileName(images[i])}. Ela pode estar corrompida.");
                }
                pages.Add(page);
                progress?.Invoke((i + 1.0) / (images.Count + 1));
            }

            var merged = Path.Combine(dir, "juntas.pdf");
            await MergeAsync(pages, "juntas.pdf", dir, ct).ConfigureAwait(false);
            progress?.Invoke(1);
            return MoveNoOverwrite(merged, output);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private async Task MergeAsync(List<string> pages, string outName, string dir, CancellationToken ct)
    {
        var args = new List<string> { "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.5",
            "-dPassThroughJPEGImages=true", "-dAutoRotatePages=/None", "-sOutputFile=" + outName };
        args.AddRange(pages);
        var r = await ProcessRunner.RunAsync(tools.Ghostscript, args, tools.Environment, timeout: TimeSpan.FromMinutes(30),
            workingDir: dir, ct: ct).ConfigureAwait(false);
        if (!r.Ok || !File.Exists(Path.Combine(dir, outName)))
        {
            Log.Warn("Juntar PDF falhou: " + r.StdErrTail);
            throw new UserFacingException("Não consegui montar o PDF.");
        }
    }

    // ---------------- separar páginas ----------------

    public sealed record PageRange(int From, int? To)
    {
        public string Label => To == From ? $"pagina-{From}" : To is null ? $"paginas-{From}-fim" : $"paginas-{From}-{To}";
        public string GsList => To == From ? From.ToString(Inv) : To is null ? $"{From}-" : $"{From}-{To}";
    }

    [GeneratedRegex(@"^\s*(\d+)\s*(-\s*(\d*)\s*)?$")]
    private static partial Regex RangeRx();

    /// <summary>"1-3, 5, 8-" → [1..3], [5], [8..fim]. Erro claro se estiver errado ou passar do total.</summary>
    public static List<PageRange> ParseRanges(string text, int totalPages)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new UserFacingException("Diga quais páginas, por exemplo: 1-3, 5, 8-");
        var list = new List<PageRange>();
        foreach (var part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = RangeRx().Match(part);
            if (!m.Success) throw new UserFacingException($"Não entendi \"{part.Trim()}\". Use, por exemplo: 1-3, 5, 8-");
            var from = int.Parse(m.Groups[1].Value, Inv);
            int? to = !m.Groups[2].Success ? from : m.Groups[3].Value.Length == 0 ? null : int.Parse(m.Groups[3].Value, Inv);
            if (from < 1 || (to is { } t && t < from)) throw new UserFacingException($"O intervalo \"{part.Trim()}\" está invertido.");
            if (totalPages > 0 && (from > totalPages || to > totalPages))
                throw new UserFacingException($"O PDF tem {totalPages} página(s); \"{part.Trim()}\" passa disso.");
            if (to is null && totalPages > 0) to = totalPages;
            list.Add(new PageRange(from, to));
        }
        return list;
    }

    public async Task<int> PageCountAsync(string pdf, CancellationToken ct)
    {
        var (src, cleanup) = AsciiCopy(pdf);
        try
        {
            var r = await ProcessRunner.RunAsync(tools.Ghostscript,
                ["-q", "-dNODISPLAY", "-dNOSAFER", "-dNOPAUSE", "-dBATCH", "-c", $"({src.Replace("\\", "/")}) (r) file runpdfbegin pdfpagecount = quit"],
                tools.Environment, captureStdOut: true, timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);
            var text = (r.StdOut + "\n" + r.StdErrTail).ToLowerInvariant();
            if (text.Contains("password")) throw new UserFacingException("Este PDF é protegido por senha. Tire a senha e tente de novo.");
            var line = r.StdOut.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && l.All(char.IsDigit));
            if (line is null || !int.TryParse(line, out var n) || n <= 0) throw new UserFacingException("Não consegui ler as páginas deste PDF.");
            return n;
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>Gera um PDF por intervalo (ou um por página, se <paramref name="ranges"/> for null). Devolve os arquivos criados.</summary>
    public async Task<List<string>> SplitAsync(string pdf, List<PageRange>? ranges, string destDir, Action<double>? progress, CancellationToken ct)
    {
        var total = await PageCountAsync(pdf, ct).ConfigureAwait(false);
        ranges ??= Enumerable.Range(1, total).Select(i => new PageRange(i, i)).ToList();
        var stem = Path.GetFileNameWithoutExtension(pdf);
        var dir = Path.Combine(tools.WorkDir, "pdf-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var (src, cleanup) = AsciiCopy(pdf);
        var created = new List<string>();
        try
        {
            Directory.CreateDirectory(destDir);
            for (var i = 0; i < ranges.Count; i++)
            {
                var rg = ranges[i];
                var name = $"parte{i:0000}.pdf";
                var r = await ProcessRunner.RunAsync(tools.Ghostscript,
                    ["-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite", "-sPageList=" + rg.GsList, "-sOutputFile=" + name, src],
                    tools.Environment, timeout: TimeSpan.FromMinutes(10), workingDir: dir, ct: ct).ConfigureAwait(false);
                var tmp = Path.Combine(dir, name);
                if (!r.Ok || !File.Exists(tmp))
                {
                    Log.Warn("Separar PDF falhou: " + r.StdErrTail);
                    throw new UserFacingException($"Não consegui separar as páginas {rg.GsList}.");
                }
                var final = Path.Combine(destDir, $"{stem}-{rg.Label}.pdf");
                created.Add(MoveNoOverwrite(tmp, final));
                progress?.Invoke((i + 1.0) / ranges.Count);
            }
            return created;
        }
        catch
        {
            foreach (var c in created) try { File.Delete(c); } catch { /* em uso */ }
            throw;
        }
        finally
        {
            cleanup();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private (string Path, Action Cleanup) AsciiCopy(string pdf)
    {
        if (pdf.All(c => c < 128)) return (pdf, () => { });
        Directory.CreateDirectory(tools.WorkDir);
        var copy = Path.Combine(tools.WorkDir, "pdf-" + Guid.NewGuid().ToString("N")[..10] + ".pdf");
        File.Copy(pdf, copy);
        return (copy, () => { try { File.Delete(copy); } catch { /* best effort */ } });
    }

    /// <summary>Move sem sobrescrever: "nome.pdf" já existe → "nome (2).pdf".</summary>
    public static string MoveNoOverwrite(string from, string to)
    {
        var dir = Path.GetDirectoryName(to)!;
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(to);
        var ext = Path.GetExtension(to);
        var target = to;
        for (var n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{stem} ({n}){ext}");
        File.Move(from, target);
        return target;
    }
}
