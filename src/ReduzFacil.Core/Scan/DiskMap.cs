using ReduzFacil.Core.Model;

namespace ReduzFacil.Core.Scan;

/// <summary>Categorias do mapa do disco (cores).</summary>
public enum MapKind { Fotos, Videos, Audios, Pdfs, Outros }

/// <summary>Uma pasta (ou o grupo "arquivos soltos") no mapa do disco.</summary>
public sealed class MapNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsFolder { get; init; }
    public long Size { get; set; }
    public long[] ByKind { get; } = new long[5];
    public int Files { get; set; }
    public List<MapNode> Children { get; } = [];
    public MapNode? Parent { get; set; }

    /// <summary>Tipo que mais ocupa espaço aqui dentro (define a cor).</summary>
    public MapKind Dominant
    {
        get
        {
            var best = 0;
            for (var i = 1; i < 5; i++) if (ByKind[i] > ByKind[best]) best = i;
            return (MapKind)best;
        }
    }

    public static MapKind KindOf(string path) => FileTypes.KindOf(path) switch
    {
        FileKind.Image => MapKind.Fotos,
        FileKind.Video => MapKind.Videos,
        FileKind.Audio => MapKind.Audios,
        FileKind.Pdf => MapKind.Pdfs,
        _ => MapKind.Outros,
    };
}

public sealed record MapProgress(int Folders, long Bytes, string? Current);

/// <summary>
/// Mede o tamanho de cada pasta (todos os arquivos, não só mídia) para o mapa do disco.
/// Pula atalhos de pasta (pontos de junção) para não contar nada duas vezes; pastas sem permissão ficam de fora.
/// </summary>
public static class DiskMapper
{
    public static MapNode Build(string root, IProgress<MapProgress>? progress, CancellationToken ct)
    {
        var full = System.IO.Path.GetFullPath(root);
        var node = new MapNode { Name = DisplayName(full), Path = full, IsFolder = true };
        var state = new State(progress);
        Fill(node, state, ct);
        progress?.Report(new MapProgress(state.Folders, state.Bytes, null));
        return node;
    }

    private sealed class State(IProgress<MapProgress>? progress)
    {
        public int Folders;
        public long Bytes;
        private DateTime _last = DateTime.MinValue;

        public void Tick(string current)
        {
            if ((DateTime.UtcNow - _last).TotalMilliseconds < 250) return;
            _last = DateTime.UtcNow;
            progress?.Report(new MapProgress(Folders, Bytes, current));
        }
    }

    private static readonly EnumerationOptions Opts = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
    };

    private static void Fill(MapNode node, State st, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        st.Folders++;
        st.Tick(node.Path);
        var loose = new MapNode { Name = "(arquivos desta pasta)", Path = node.Path, IsFolder = false, Parent = node };
        try
        {
            foreach (var f in new DirectoryInfo(node.Path).EnumerateFiles("*", Opts))
            {
                long len;
                try { len = f.Length; } catch { continue; }
                loose.Size += len;
                loose.ByKind[(int)MapNode.KindOf(f.Name)] += len;
                loose.Files++;
                st.Bytes += len;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // sem acesso: fica de fora
        }
        List<DirectoryInfo> dirs;
        try { dirs = new DirectoryInfo(node.Path).EnumerateDirectories("*", Opts).ToList(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { dirs = []; }

        foreach (var d in dirs)
        {
            var child = new MapNode { Name = d.Name, Path = d.FullName, IsFolder = true, Parent = node };
            Fill(child, st, ct);
            if (child.Size == 0) continue;
            node.Children.Add(child);
        }
        if (loose.Size > 0) node.Children.Add(loose);
        foreach (var c in node.Children)
        {
            node.Size += c.Size;
            node.Files += c.Files;
            for (var i = 0; i < 5; i++) node.ByKind[i] += c.ByKind[i];
        }
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    private static string DisplayName(string full)
    {
        var name = System.IO.Path.GetFileName(full.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? full : name;
    }
}

/// <summary>Retângulo do mapa (em unidades do espaço de desenho).</summary>
public readonly record struct MapRect(double X, double Y, double W, double H);

/// <summary>
/// Mapa em blocos "squarified" (Bruls et al.): blocos o mais quadrados possível, área proporcional ao tamanho.
/// Função pura, testada.
/// </summary>
public static class Treemap
{
    public static List<(T Item, MapRect Rect)> Layout<T>(IReadOnlyList<T> items, Func<T, double> size, MapRect area)
    {
        var result = new List<(T, MapRect)>();
        var list = items.Where(i => size(i) > 0).OrderByDescending(size).ToList();
        var total = list.Sum(size);
        if (total <= 0 || area.W <= 0 || area.H <= 0) return result;
        var scale = area.W * area.H / total;
        var queue = new Queue<(T Item, double Area)>(list.Select(i => (i, size(i) * scale)));
        var rect = area;
        var row = new List<(T Item, double Area)>();
        while (queue.Count > 0)
        {
            var next = queue.Peek();
            var side = Math.Min(rect.W, rect.H);
            var withNext = new List<(T Item, double Area)>(row) { next };
            if (row.Count == 0 || Worst(row, side) >= Worst(withNext, side))
            {
                row.Add(queue.Dequeue());
                continue;
            }
            rect = Place(row, rect, result);
            row.Clear();
        }
        if (row.Count > 0) Place(row, rect, result);
        return result;
    }

    private static double Worst<T>(List<(T Item, double Area)> row, double side) => WorstCore(row.Select(r => r.Area).ToList(), side);

    private static double WorstCore(List<double> areas, double side)
    {
        var s = areas.Sum();
        if (s <= 0 || side <= 0) return double.MaxValue;
        var max = areas.Max();
        var min = areas.Min();
        var s2 = s * s;
        var w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private static MapRect Place<T>(List<(T Item, double Area)> row, MapRect rect, List<(T, MapRect)> result)
    {
        var sum = row.Sum(r => r.Area);
        if (rect.W >= rect.H)
        {
            // Coluna à esquerda
            var w = sum / rect.H;
            var y = rect.Y;
            foreach (var (item, a) in row)
            {
                var h = a / w;
                result.Add((item, new MapRect(rect.X, y, w, h)));
                y += h;
            }
            return new MapRect(rect.X + w, rect.Y, Math.Max(0, rect.W - w), rect.H);
        }
        else
        {
            // Linha em cima
            var h = sum / rect.W;
            var x = rect.X;
            foreach (var (item, a) in row)
            {
                var w = a / h;
                result.Add((item, new MapRect(x, rect.Y, w, h)));
                x += w;
            }
            return new MapRect(rect.X, rect.Y + h, rect.W, Math.Max(0, rect.H - h));
        }
    }
}
