using System.Text.Json;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Stats;

public sealed class HistoryEntry
{
    public DateTime When { get; set; }
    public string Input { get; set; } = "";
    public string? Output { get; set; }
    public long OriginalBytes { get; set; }
    public long FinalBytes { get; set; }
    public bool KeptOriginal { get; set; }
    public FileKind Kind { get; set; }
    public string? Note { get; set; }

    public long Saved => KeptOriginal ? 0 : Math.Max(0, OriginalBytes - FinalBytes);
}

public sealed class Totals
{
    public long Files { get; set; }
    public long OriginalBytes { get; set; }
    public long SavedBytes { get; set; }
}

/// <summary>Histórico (últimos 1.000 arquivos) e o total economizado desde sempre.</summary>
public sealed class HistoryStore
{
    private const int Keep = 1000;
    private readonly object _gate = new();
    private readonly string _historyPath;
    private readonly string _totalsPath;
    private readonly List<HistoryEntry> _entries = [];

    public Totals Totals { get; private set; } = new();

    public event Action? Changed;

    public HistoryStore(string dataDir)
    {
        _historyPath = Path.Combine(dataDir, "historico.json");
        _totalsPath = Path.Combine(dataDir, "totais.json");
        try
        {
            if (File.Exists(_historyPath))
                _entries.AddRange(JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_historyPath), Settings.SettingsStore.Json) ?? []);
            if (File.Exists(_totalsPath))
                Totals = JsonSerializer.Deserialize<Totals>(File.ReadAllText(_totalsPath), Settings.SettingsStore.Json) ?? new Totals();
        }
        catch (Exception ex)
        {
            Log.Error("Histórico ilegível; começando vazio", ex);
            _entries.Clear();
        }
    }

    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public void Add(CompressResult r, FileKind kind)
    {
        var e = new HistoryEntry
        {
            When = DateTime.Now,
            Input = r.InputPath,
            Output = r.OutputPath,
            OriginalBytes = r.OriginalBytes,
            FinalBytes = r.FinalBytes,
            KeptOriginal = r.KeptOriginal,
            Kind = kind,
            Note = r.EngineNote,
        };
        lock (_gate)
        {
            _entries.Insert(0, e);
            if (_entries.Count > Keep) _entries.RemoveRange(Keep, _entries.Count - Keep);
            Totals.Files++;
            Totals.OriginalBytes += r.OriginalBytes;
            Totals.SavedBytes += e.Saved;
            Persist();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Persist();
        }
        Changed?.Invoke();
    }

    private void Persist()
    {
        try
        {
            Write(_historyPath, JsonSerializer.Serialize(_entries, Settings.SettingsStore.Json));
            Write(_totalsPath, JsonSerializer.Serialize(Totals, Settings.SettingsStore.Json));
        }
        catch (Exception ex)
        {
            Log.Warn("Não consegui salvar o histórico: " + ex.Message);
        }
    }

    private static void Write(string path, string text)
    {
        var tmp = path + ".gravando";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
