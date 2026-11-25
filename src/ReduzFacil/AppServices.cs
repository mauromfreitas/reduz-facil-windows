using ReduzFacil.Core.Compress;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Output;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Settings;
using ReduzFacil.Core.Stats;
using ReduzFacil.Core.Util;
using ReduzFacil.Core.Watch;
using ReduzFacil.Infra;

namespace ReduzFacil;

/// <summary>Pedido vindo da linha de comando (botão direito, segunda abertura, inicialização do Windows).</summary>
public sealed record LaunchRequest(string[] Files, string? Preset, bool OnlyAdd, bool Tray, bool Show);

/// <summary>Tudo o que o app usa, criado uma vez: configurações, motores, fila, histórico, pasta vigiada.</summary>
public sealed class AppServices : IDisposable
{
    private readonly SettingsStore _store;

    public DataPaths Data { get; }
    public EngineTools Tools { get; }
    public AppSettings Settings { get; }
    public HistoryStore History { get; }
    public HardwareCatalog Hardware { get; } = new();
    public Compressor Compressor { get; }
    public JobQueue Queue { get; }

    public event Action? WatchStateChanged;

    public AppServices(DataPaths data, EngineInfo engine)
    {
        Data = data;
        PreviewMaker.Clean(data.WorkDir);
        _store = new SettingsStore(data.SettingsFile);
        Settings = _store.Load();
        History = new HistoryStore(data.Root);
        Tools = EngineTools.FromEngineDir(engine.Root, data.WorkDir);
        Compressor = new Compressor(new CompressContext { Tools = Tools, Hardware = Hardware });
        Queue = new JobQueue(Compressor, () => Settings.Output.Clone());
        Queue.ItemFinished += OnFinished;
        ApplyQueueSettings();

        // Testa a placa de vídeo em segundo plano (leva 1–3 s).
        _ = Hardware.DetectAsync(Tools);
    }

    public void Save() => _store.Save(Settings);

    /// <summary>Sufixo que realmente vai no nome (ao lado do original ele nunca fica vazio).</summary>
    public string EffectiveSuffix => string.IsNullOrWhiteSpace(Settings.Output.Suffix) ? "-reduzido" : Settings.Output.Suffix.Trim();

    public void ApplyQueueSettings()
    {
        Queue.HeavyLimit = Settings.VideosAtOnce;
        Queue.LightLimit = Settings.OthersAtOnce;
        Queue.AutoStart = Settings.AutoStart;
    }

    /// <summary>Opções do que está escolhido na tela principal agora.</summary>
    public CompressOptions CurrentOptions() => Settings.OptionsFor(Settings.PresetId, Settings.CustomTargetMb);

    /// <summary>Opções para um atalho do botão direito ou de um perfil salvo; null = o da tela.</summary>
    public CompressOptions OptionsFor(string? preset) =>
        string.IsNullOrWhiteSpace(preset) ? CurrentOptions() : Settings.OptionsFor(preset, Settings.CustomTargetMb);

    /// <summary>Coloca arquivos e pastas na fila. Devolve quantos entraram.</summary>
    public int Enqueue(IEnumerable<string> paths, CompressOptions options, JobOrigin origin = JobOrigin.Manual)
    {
        var found = FileCollector.Collect(paths, EffectiveSuffix);
        var n = 0;
        foreach (var f in found)
            if (Queue.Add(f.Path, options, f.SourceRoot, origin) is not null) n++;
        if (n > 0) Log.Info($"{n} arquivo(s) na fila ({origin})");
        return n;
    }

    private void OnFinished(QueueItem item)
    {
        if (item.Result is { } r && item.Status is JobStatus.Done or JobStatus.KeptOriginal)
            History.Add(r, item.Kind);
    }

    // ---------------- pastas vigiadas ----------------

    private readonly List<(WatchSettings Settings, FolderWatcher Watcher)> _watchers = [];

    /// <summary>Erro de cada pasta (pasta apagada, sem permissão), para mostrar nas Configurações.</summary>
    public Dictionary<WatchSettings, string> WatchErrors { get; } = [];

    public string? WatchError => WatchErrors.Values.FirstOrDefault();

    public int WatchCount => _watchers.Count;

    public bool WatchRunning => _watchers.Count > 0;

    public bool IsWatching(WatchSettings w) => _watchers.Any(x => ReferenceEquals(x.Settings, w));

    public void RestartWatcher()
    {
        foreach (var (_, fw) in _watchers) fw.Dispose();
        _watchers.Clear();
        WatchErrors.Clear();
        var outDir = Settings.Output.Mode == OutputMode.Folder && !string.IsNullOrWhiteSpace(Settings.Output.Folder)
            ? Path.GetFullPath(Settings.Output.Folder).TrimEnd('\\') : null;
        foreach (var w in Settings.Watches.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Folder)))
        {
            FolderWatcher? fw = null;
            try
            {
                // Saída na própria pasta vigiada: quem evita o laço é o sufixo (ignorar a pasta inteira desligaria tudo).
                var ignore = outDir is not null && !string.Equals(outDir, Path.GetFullPath(w.Folder!).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    ? outDir : null;
                var settings = w;
                fw = new FolderWatcher(w.Folder!, w.IncludeSubfolders, p => OnWatchedFile(settings, p), () => EffectiveSuffix,
                    p => ignore is not null && p.StartsWith(ignore + "\\", StringComparison.OrdinalIgnoreCase));
                fw.Start();
                _watchers.Add((w, fw));
            }
            catch (Exception ex)
            {
                WatchErrors[w] = ex is DirectoryNotFoundException ? "a pasta não existe mais" : ex.Message;
                Log.Warn($"Pasta vigiada {w.Folder} não ligou: {ex.Message}");
                fw?.Dispose();
            }
        }
        WatchStateChanged?.Invoke();
    }

    private void OnWatchedFile(WatchSettings w, string path)
    {
        var options = OptionsFor(w.Profile);
        Queue.Add(path, options, null, JobOrigin.Watch);
    }

    // ---------------- linha de comando ----------------

    public static LaunchRequest ParseArgs(string[] args)
    {
        string? preset = null;
        bool onlyAdd = false, tray = false, show = false;
        var files = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--perfil" when i + 1 < args.Length:
                    preset = args[++i];
                    break;
                case "--adicionar":
                    onlyAdd = true;
                    break;
                case "--bandeja":
                    tray = true;
                    break;
                case "--mostrar":
                    show = true;
                    break;
                default:
                    if (!a.StartsWith("--", StringComparison.Ordinal)) files.Add(a);
                    else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && IsValueOption(a)) i++;
                    break;
            }
        }
        return new LaunchRequest(files.ToArray(), preset, onlyAdd, tray, show || files.Count > 0 || (!tray && args.Length == 0));
    }

    /// <summary>Opções de teste que levam um valor (o valor não é um arquivo).</summary>
    private static bool IsValueOption(string a) => a.ToLowerInvariant() is "--teste-auto" or "--capturas" or "--teste-espera"
        or "--teste-atualizar" or "--atualizado" or "--esperar-pid";

    public void Dispose()
    {
        foreach (var (_, fw) in _watchers) fw.Dispose();
        Queue.CancelAll();
        Save();
        PreviewMaker.Clean(Data.WorkDir);
    }
}
