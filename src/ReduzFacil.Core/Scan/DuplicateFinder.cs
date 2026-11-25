using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Scan;

public sealed class DupFile
{
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public DateTime Modified { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Sugestão: manter este (o melhor do grupo).</summary>
    public bool Keep { get; set; }
}

public sealed class DupGroup
{
    /// <summary>true = arquivos idênticos byte a byte; false = fotos parecidas (conferir antes).</summary>
    public bool Exact { get; init; }
    public required List<DupFile> Files { get; init; }
    /// <summary>Espaço que se libera apagando todos menos o sugerido.</summary>
    public long Wasted => Files.Where(f => !f.Keep).Sum(f => f.SizeBytes);
}

public sealed record DupProgress(string Stage, int Done, int Total);

/// <summary>
/// Repetidos: arquivos idênticos (tamanho → começo → SHA-256) e fotos parecidas
/// (impressão digital visual "dHash" de 128 bits — gradientes na horizontal e na vertical; diferença de até <see cref="SimilarBits"/> bits).
/// Fotos quase lisas (céu, parede, tela preta) têm pouca informação e ficam de fora das parecidas.
/// </summary>
public sealed class DuplicateFinder(EngineTools tools)
{
    public int SimilarBits { get; init; } = 12;
    public long MinBytes { get; init; } = 50 * 1024;

    public async Task<List<DupGroup>> FindAsync(string root, bool similarPhotos, IProgress<DupProgress>? progress, CancellationToken ct)
    {
        var files = new List<FileInfo>();
        foreach (var p in FileCollector.Walk(Path.GetFullPath(root), ct))
        {
            if (FileTypes.KindOf(p) is null || FileTypes.IsTemporary(p)) continue;
            try
            {
                var fi = new FileInfo(p);
                if (fi.Length >= MinBytes) files.Add(fi);
            }
            catch { /* inacessível */ }
            if (files.Count % 500 == 0) progress?.Report(new DupProgress("Listando", files.Count, 0));
        }

        var groups = new List<DupGroup>();
        var exactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---------- idênticos ----------
        var sameSize = files.GroupBy(f => f.Length).Where(g => g.Count() > 1).ToList();
        var total = sameSize.Sum(g => g.Count());
        var done = 0;
        foreach (var bySize in sameSize)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var byHead in bySize.GroupBy(f => Hash(f.FullName, 64 * 1024)).Where(g => g.Key is not null && g.Count() > 1))
            {
                foreach (var byFull in byHead.GroupBy(f => Hash(f.FullName, long.MaxValue)).Where(g => g.Key is not null && g.Count() > 1))
                {
                    var list = byFull.Select(f => new DupFile { Path = f.FullName, SizeBytes = f.Length, Modified = f.LastWriteTime }).ToList();
                    // Mantém o mais antigo (o original); empate: caminho mais curto.
                    list.OrderBy(f => f.Modified).ThenBy(f => f.Path.Length).First().Keep = true;
                    groups.Add(new DupGroup { Exact = true, Files = list });
                    foreach (var f in list) exactPaths.Add(f.Path);
                }
            }
            done += bySize.Count();
            progress?.Report(new DupProgress("Comparando arquivos iguais", done, total));
        }

        // ---------- fotos parecidas ----------
        if (similarPhotos)
        {
            var photos = files.Where(f => FileTypes.KindOf(f.FullName) == FileKind.Image).ToList();
            var hashes = await PhotoHashesAsync(photos.Select(f => f.FullName).ToList(), progress, ct).ConfigureAwait(false);
            // Pouca informação (foto quase lisa): qualquer outra lisa "pareceria" igual.
            var items = photos.Where(f => hashes.TryGetValue(f.FullName, out var h) && PopCount(h) >= 10).ToList();
            var parent = Enumerable.Range(0, items.Count).ToArray();
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var hi = hashes[items[i].FullName];
                for (var j = i + 1; j < items.Count; j++)
                    if (PopCount(hi ^ hashes[items[j].FullName]) <= SimilarBits)
                        parent[Find(j)] = Find(i);
            }
            foreach (var g in Enumerable.Range(0, items.Count).GroupBy(Find).Where(g => g.Count() > 1))
            {
                var members = g.Select(i => items[i]).ToList();
                // Já é um grupo de idênticos inteiro: não repete.
                if (members.All(m => exactPaths.Contains(m.FullName)) &&
                    groups.Any(x => x.Exact && members.All(m => x.Files.Any(f => string.Equals(f.Path, m.FullName, StringComparison.OrdinalIgnoreCase)))))
                    continue;
                var list = members.Select(f =>
                {
                    var dims = HeaderReader.ImageSize(f.FullName);
                    return new DupFile { Path = f.FullName, SizeBytes = f.Length, Modified = f.LastWriteTime, Width = dims?.Width ?? 0, Height = dims?.Height ?? 0 };
                }).ToList();
                // Mantém o de maior resolução; empate: o maior arquivo (menos comprimido).
                list.OrderByDescending(f => (long)f.Width * f.Height).ThenByDescending(f => f.SizeBytes).First().Keep = true;
                groups.Add(new DupGroup { Exact = false, Files = list });
            }
        }

        return groups.OrderBy(g => g.Exact ? 0 : 1).ThenByDescending(g => g.Wasted).ToList();
    }

    private static string? Hash(string path, long maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            if (maxBytes == long.MaxValue) return Convert.ToHexString(SHA256.HashData(fs));
            var buf = new byte[(int)Math.Min(maxBytes, fs.Length)];
            fs.ReadExactly(buf);
            return Convert.ToHexString(SHA256.HashData(buf));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>dHash de várias fotos de uma vez (o ImageMagick aceita vários arquivos por chamada).</summary>
    internal async Task<Dictionary<string, UInt128>> PhotoHashesAsync(List<string> photos, IProgress<DupProgress>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, UInt128>(StringComparer.OrdinalIgnoreCase);
        const int batch = 40;
        var done = 0;
        var tasks = new List<Task>();
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        foreach (var chunk in photos.Chunk(batch))
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var got = await HashBatchAsync(chunk, ct).ConfigureAwait(false);
                    if (got is null) // um arquivo estragado derruba o lote: refaz um por um
                    {
                        got = [];
                        foreach (var one in chunk)
                            if (await HashBatchAsync([one], ct).ConfigureAwait(false) is { } r) foreach (var kv in r) got[kv.Key] = kv.Value;
                    }
                    lock (result) foreach (var kv in got) result[kv.Key] = kv.Value;
                }
                finally
                {
                    gate.Release();
                    var d = Interlocked.Add(ref done, chunk.Length);
                    progress?.Report(new DupProgress("Comparando fotos parecidas", d, photos.Count));
                }
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    private async Task<Dictionary<string, UInt128>?> HashBatchAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        // Cada foto vira 9×9 em cinza; comparamos cada pixel com o vizinho da direita e com o de baixo.
        var args = new List<string> { "-quiet", "-define", "jpeg:size=144x128" };
        foreach (var f in files) args.Add(f + "[0]");
        args.AddRange(["-auto-orient", "-colorspace", "Gray", "-thumbnail", "9x9!", "-depth", "8", "+adjoin", "txt:-"]);
        var r = await ProcessRunner.RunAsync(tools.MagickConvert, args, tools.Environment, captureStdOut: true,
            timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        // Saída em texto: "# ImageMagick pixel enumeration: 9,8,..." e linhas "x,y: (v) ..."
        var images = new List<byte[]>();
        byte[]? cur = null;
        foreach (var line in r.StdOut.Split('\n'))
        {
            if (line.StartsWith('#'))
            {
                cur = new byte[81];
                images.Add(cur);
                continue;
            }
            if (cur is null) continue;
            var colon = line.IndexOf(':');
            var open = line.IndexOf('(');
            if (colon < 0 || open < 0) continue;
            var xy = line[..colon].Split(',');
            if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y) || x > 8 || y > 8) continue;
            var close = line.IndexOf(')', open);
            var first = line[(open + 1)..close].Split(',')[0].Trim().TrimEnd('%');
            if (!double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            cur[y * 9 + x] = (byte)Math.Clamp(v, 0, 255);
        }
        if (images.Count != files.Count) return null;
        var map = new Dictionary<string, UInt128>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++) map[files[i]] = DHash(images[i]);
        return map;
    }

    /// <summary>128 bits: 64 da horizontal e 64 da vertical. Diferenças pequenas (≤ 2 tons) contam como "igual", para áreas lisas não virarem ruído.</summary>
    internal static UInt128 DHash(byte[] g)
    {
        UInt128 h = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++, bit++)
                if (g[y * 9 + x] - g[y * 9 + x + 1] > 2) h |= UInt128.One << bit;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++, bit++)
                if (g[y * 9 + x] - g[(y + 1) * 9 + x] > 2) h |= UInt128.One << bit;
        return h;
    }

    internal static int PopCount(UInt128 v) => BitOperations.PopCount((ulong)v) + BitOperations.PopCount((ulong)(v >> 64));
}
