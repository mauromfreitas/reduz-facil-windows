using ReduzFacil.Core.Model;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Watch;

/// <summary>
/// Pasta vigiada: cada arquivo novo (foto do WhatsApp, download, cópia do celular) é reduzido sozinho.
/// Só entra quando o arquivo terminou de ser gravado (tamanho parado e arquivo livre).
/// Arquivos que o próprio app gera (sufixo) e temporários de download são ignorados.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private sealed class Pending
    {
        public long LastSize = -1;
        public int Stable;
        public DateTime FirstSeen = DateTime.UtcNow;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _newDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _onReady;
    private readonly Func<string?> _suffix;
    private readonly Func<string, bool> _ignorePath;
    private FileSystemWatcher? _fsw;
    private Timer? _timer;

    public string Folder { get; }
    public bool IncludeSubfolders { get; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <param name="ignorePath">Caminhos a ignorar (ex.: a pasta de saída, se estiver dentro da vigiada).</param>
    public FolderWatcher(string folder, bool includeSubfolders, Action<string> onReady, Func<string?> suffix, Func<string, bool>? ignorePath = null)
    {
        Folder = Path.GetFullPath(folder);
        IncludeSubfolders = includeSubfolders;
        _onReady = onReady;
        _suffix = suffix;
        _ignorePath = ignorePath ?? (_ => false);
    }

    public void Start()
    {
        if (!Directory.Exists(Folder)) throw new DirectoryNotFoundException(Folder);
        StartWatcher();
        _timer = new Timer(_ => Tick(), null, PollInterval, PollInterval);
        Log.Info($"Pasta vigiada ligada: {Folder}");
    }

    private void StartWatcher()
    {
        _fsw?.Dispose();
        _fsw = new FileSystemWatcher(Folder)
        {
            IncludeSubdirectories = IncludeSubfolders,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        _fsw.Created += (_, e) => OnCreated(e.FullPath);
        _fsw.Changed += (_, e) => Consider(e.FullPath);
        _fsw.Renamed += (_, e) => OnCreated(e.FullPath); // download terminado: "x.jpg.crdownload" → "x.jpg"
        _fsw.Error += (_, e) =>
        {
            Log.Warn("Pasta vigiada perdeu eventos; reiniciando: " + e.GetException().Message);
            try { StartWatcher(); } catch (Exception ex) { Log.Error("Não consegui reiniciar a pasta vigiada", ex); }
        };
        _fsw.EnableRaisingEvents = true;
    }

    private void OnCreated(string path)
    {
        // Pasta nova (ex.: copiou uma pasta inteira para dentro): os arquivos dela podem chegar sem evento próprio.
        if (IncludeSubfolders && Directory.Exists(path))
        {
            lock (_gate) _newDirs.Add(path);
            return;
        }
        Consider(path);
    }

    /// <summary>Usado também pelos testes.</summary>
    internal void Consider(string path)
    {
        if (!ShouldTake(path)) return;
        lock (_gate)
        {
            if (_done.Contains(Key(path))) return;
            _pending.TryAdd(path, new Pending());
        }
    }

    public bool ShouldTake(string path)
    {
        if (FileTypes.KindOf(path) is null || FileTypes.IsTemporary(path)) return false;
        if (FileCollector.IsOwnOutput(path, _suffix())) return false;
        if (path.Contains(Path.DirectorySeparatorChar + "ReduzFacil-dados" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        if (_ignorePath(path)) return false;
        var name = Path.GetFileName(path);
        return !name.StartsWith('.');
    }

    private void Tick()
    {
        List<string> ready = [];
        string[] dirs;
        lock (_gate)
        {
            dirs = _newDirs.ToArray();
            _newDirs.Clear();
        }
        foreach (var d in dirs)
        {
            try
            {
                foreach (var f in FileCollector.Walk(d, CancellationToken.None)) Consider(f);
            }
            catch (Exception ex)
            {
                Log.Warn($"Pasta vigiada: não consegui ler {d}: {ex.Message}");
            }
        }
        lock (_gate)
        {
            foreach (var (path, p) in _pending.ToList())
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists || DateTime.UtcNow - p.FirstSeen > TimeSpan.FromHours(2))
                    {
                        _pending.Remove(path);
                        continue;
                    }
                    var size = fi.Length;
                    if (size > 0 && size == p.LastSize && CanOpenExclusive(path)) p.Stable++;
                    else p.Stable = 0;
                    p.LastSize = size;
                    if (p.Stable >= 2)
                    {
                        _pending.Remove(path);
                        var key = Key(path);
                        if (_done.Add(key)) ready.Add(path);
                    }
                }
                catch
                {
                    p.Stable = 0;
                }
            }
        }
        foreach (var r in ready)
        {
            Log.Info("Pasta vigiada: novo arquivo " + r);
            try { _onReady(r); } catch (Exception ex) { Log.Error("Pasta vigiada: falha ao colocar na fila", ex); }
        }
    }

    private static string Key(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return path;
        }
    }

    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _fsw?.Dispose();
        _timer = null;
        _fsw = null;
    }
}
