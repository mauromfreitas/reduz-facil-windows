using ReduzFacil.Core.Compress;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Output;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Queue;

public enum JobStatus { Waiting, Running, Done, KeptOriginal, Failed, Canceled }

public enum JobOrigin { Manual, Watch, Scan, ShellMenu }

public sealed class QueueItem
{
    private static long _next;

    public long Id { get; } = Interlocked.Increment(ref _next);
    public required string Path { get; init; }
    public required FileKind Kind { get; init; }
    public required CompressOptions Options { get; set; }
    public string? SourceRoot { get; init; }
    public JobOrigin Origin { get; init; }
    public long SizeBytes { get; init; }

    public JobStatus Status { get; internal set; }
    /// <summary>Pode começar (com "Começar sozinho" desligado, só depois do Iniciar).</summary>
    public bool Released { get; internal set; }
    public double Progress { get; internal set; }
    public CompressResult? Result { get; internal set; }
    public string? Error { get; internal set; }
    public DateTime Added { get; } = DateTime.Now;

    internal CancellationTokenSource? Cts { get; set; }

    public bool IsFinished => Status is JobStatus.Done or JobStatus.KeptOriginal or JobStatus.Failed or JobStatus.Canceled;
    public bool IsHeavy => Kind == FileKind.Video;
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Fila de compressão: vídeos numa "pista" (poucos ao mesmo tempo, usam muito o PC),
/// fotos/PDFs/áudios em outra (vários ao mesmo tempo). Pausar suspende os motores.
/// Os eventos vêm de outras threads: a tela é quem leva para a thread da interface.
/// </summary>
public sealed class JobQueue
{
    private readonly object _gate = new();
    private readonly List<QueueItem> _items = [];
    private readonly Compressor _compressor;
    private readonly Func<OutputSettings> _output;
    private int _runningHeavy, _runningLight;

    public JobQueue(Compressor compressor, Func<OutputSettings> output)
    {
        _compressor = compressor;
        _output = output;
    }

    public int HeavyLimit { get; set; } = 1;
    public int LightLimit { get; set; } = 3;

    /// <summary>Começa sozinho quando um arquivo entra. Desligado: só com <see cref="StartAll"/>.</summary>
    public bool AutoStart { get; set; } = true;

    public PauseGate Pause => _compressor.Context.Pause;
    public bool IsPaused => Pause.IsPaused;

    /// <summary>Item mudou (status ou progresso).</summary>
    public event Action<QueueItem>? ItemChanged;
    /// <summary>Item terminou (concluído, mantido, falhou ou cancelado).</summary>
    public event Action<QueueItem>? ItemFinished;
    /// <summary>Lista mudou (entrou ou saiu item).</summary>
    public event Action? ListChanged;
    /// <summary>Não há mais nada esperando nem rodando.</summary>
    public event Action? Idle;

    public IReadOnlyList<QueueItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public bool IsBusy
    {
        get { lock (_gate) return _items.Any(i => i.Status is JobStatus.Waiting or JobStatus.Running); }
    }

    /// <summary>Coloca um arquivo na fila. Se ele já está esperando ou rodando, devolve o mesmo item.</summary>
    public QueueItem? Add(string path, CompressOptions options, string? sourceRoot = null, JobOrigin origin = JobOrigin.Manual)
    {
        var full = System.IO.Path.GetFullPath(path);
        var kind = FileTypes.KindOf(full);
        if (kind is null) return null;
        long size;
        try { size = new FileInfo(full).Length; } catch { size = 0; }
        QueueItem item;
        lock (_gate)
        {
            var existing = _items.FirstOrDefault(i => !i.IsFinished && SamePath(i.Path, full));
            if (existing is not null) return existing;
            // Pasta vigiada sempre começa sozinha (ninguém está olhando para apertar Iniciar).
            item = new QueueItem
            {
                Path = full, Kind = kind.Value, Options = options.Clone(), SourceRoot = sourceRoot, Origin = origin, SizeBytes = size,
                Released = AutoStart || origin == JobOrigin.Watch,
            };
            _items.Add(item);
        }
        ListChanged?.Invoke();
        Schedule();
        return item;
    }

    /// <summary>Há arquivos esperando o botão Iniciar.</summary>
    public bool HasUnreleased
    {
        get { lock (_gate) return _items.Any(i => i.Status == JobStatus.Waiting && !i.Released); }
    }

    public void StartAll()
    {
        lock (_gate)
            foreach (var i in _items)
                if (i.Status == JobStatus.Waiting) i.Released = true;
        if (IsPaused) Pause.Resume();
        Schedule();
    }

    public void PauseAll() => Pause.Pause();

    public void ResumeAll()
    {
        Pause.Resume();
        Schedule();
    }

    public void Cancel(QueueItem item)
    {
        CancellationTokenSource? cts = null;
        var notify = false;
        lock (_gate)
        {
            if (item.Status == JobStatus.Waiting)
            {
                item.Status = JobStatus.Canceled;
                notify = true;
            }
            else if (item.Status == JobStatus.Running) cts = item.Cts;
        }
        cts?.Cancel();
        if (notify)
        {
            ItemChanged?.Invoke(item);
            ItemFinished?.Invoke(item);
            CheckIdle();
        }
    }

    public void CancelAll()
    {
        foreach (var i in Items.Where(i => !i.IsFinished)) Cancel(i);
    }

    /// <summary>Tira da lista (cancelando se estiver rodando).</summary>
    public void Remove(QueueItem item)
    {
        Cancel(item);
        lock (_gate) _items.Remove(item);
        ListChanged?.Invoke();
    }

    public void ClearFinished()
    {
        lock (_gate) _items.RemoveAll(i => i.IsFinished);
        ListChanged?.Invoke();
    }

    /// <summary>Muda as opções de um item que ainda não começou (cortar, girar...).</summary>
    public bool UpdateOptions(QueueItem item, CompressOptions o)
    {
        lock (_gate)
        {
            if (item.Status != JobStatus.Waiting) return false;
            item.Options = o.Clone();
        }
        ItemChanged?.Invoke(item);
        return true;
    }

    /// <summary>Põe de novo na fila um item que falhou ou foi cancelado.</summary>
    public void Retry(QueueItem item)
    {
        lock (_gate)
        {
            if (item.Status is not (JobStatus.Failed or JobStatus.Canceled)) return;
            item.Status = JobStatus.Waiting;
            item.Progress = 0;
            item.Error = null;
            item.Result = null;
            item.Released = true;
        }
        ItemChanged?.Invoke(item);
        Schedule();
    }

    public void Schedule()
    {
        var toStart = new List<QueueItem>();
        lock (_gate)
        {
            if (Pause.IsPaused) return;
            foreach (var i in _items)
            {
                if (i.Status != JobStatus.Waiting || !i.Released) continue;
                if (i.IsHeavy)
                {
                    if (_runningHeavy >= Math.Max(1, HeavyLimit)) continue;
                    _runningHeavy++;
                }
                else
                {
                    if (_runningLight >= Math.Max(1, LightLimit)) continue;
                    _runningLight++;
                }
                i.Status = JobStatus.Running;
                i.Progress = 0;
                i.Cts = new CancellationTokenSource();
                toStart.Add(i);
            }
        }
        foreach (var i in toStart)
        {
            ItemChanged?.Invoke(i);
            _ = Task.Run(() => RunItemAsync(i));
        }
    }

    private async Task RunItemAsync(QueueItem item)
    {
        var lastReport = DateTime.MinValue;
        void OnProgress(double p)
        {
            item.Progress = p;
            // No máximo ~8 atualizações por segundo para a tela.
            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds < 120 && p < 1) return;
            lastReport = now;
            ItemChanged?.Invoke(item);
        }

        try
        {
            var res = await _compressor.RunAsync(item.Path, item.Options, _output(), item.SourceRoot, OnProgress, item.Cts!.Token).ConfigureAwait(false);
            item.Result = res;
            item.Progress = 1;
            item.Status = res.KeptOriginal ? JobStatus.KeptOriginal : JobStatus.Done;
        }
        catch (OperationCanceledException)
        {
            item.Status = JobStatus.Canceled;
        }
        catch (Exception ex)
        {
            item.Status = JobStatus.Failed;
            item.Error = Friendly(ex);
            if (ex is not UserFacingException) Log.Error($"Falha em {item.Path}", ex);
            else Log.Warn($"{item.Name}: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (item.IsHeavy) _runningHeavy--; else _runningLight--;
                item.Cts?.Dispose();
                item.Cts = null;
            }
        }

        ItemChanged?.Invoke(item);
        try { ItemFinished?.Invoke(item); } catch (Exception ex) { Log.Error("Erro ao registrar resultado", ex); }
        Schedule();
        CheckIdle();
    }

    private void CheckIdle()
    {
        if (!IsBusy) Idle?.Invoke();
    }

    public static string Friendly(Exception ex) => ex switch
    {
        UserFacingException u => u.Message,
        Engines.EngineMissingException m => m.Message,
        UnauthorizedAccessException => "O Windows não deixou gravar na pasta de destino (permissão negada). Escolha outra pasta nas Configurações.",
        PathTooLongException => "O caminho do arquivo é longo demais.",
        IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) => "Acabou o espaço no disco.",
        IOException io => "Erro ao ler ou gravar o arquivo: " + io.Message,
        TimeoutException => "O motor demorou demais e foi interrompido.",
        _ => "Algo deu errado: " + ex.Message,
    };

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
