using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Engines;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErrTail, TimeSpan Elapsed)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Controle de pausa compartilhado: pausar suspende os motores que estão rodando.</summary>
public sealed class PauseGate
{
    private readonly object _gate = new();
    private readonly HashSet<Process> _running = [];
    private TaskCompletionSource _resumed = NewResumed();

    public bool IsPaused { get; private set; }

    private static TaskCompletionSource NewResumed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public Task WaitIfPausedAsync(CancellationToken ct)
    {
        Task t;
        lock (_gate) t = _resumed.Task;
        return t.IsCompleted ? Task.CompletedTask : t.WaitAsync(ct);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (IsPaused) return;
            IsPaused = true;
            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var p in _running) ProcessControl.Suspend(p);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!IsPaused) return;
            IsPaused = false;
            foreach (var p in _running) ProcessControl.Resume(p);
            _resumed.TrySetResult();
        }
    }

    internal void Register(Process p)
    {
        lock (_gate)
        {
            _running.Add(p);
            if (IsPaused) ProcessControl.Suspend(p);
        }
    }

    internal void Unregister(Process p)
    {
        lock (_gate) _running.Remove(p);
    }
}

/// <summary>Roda um motor, lê as saídas linha a linha, cancela matando a árvore de processos.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        Tool tool,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        PauseGate? pause = null,
        TimeSpan? timeout = null,
        bool captureStdOut = false,
        string? workingDir = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(tool.Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        foreach (var a in tool.Prefix) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var tail = new Queue<string>();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.TrySetResult(); return; }
            if (captureStdOut) lock (stdout) stdout.AppendLine(e.Data);
            try { onStdOut?.Invoke(e.Data); } catch { /* callback nunca derruba o motor */ }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.TrySetResult(); return; }
            lock (tail)
            {
                tail.Enqueue(e.Data);
                while (tail.Count > 60) tail.Dequeue();
            }
            try { onStdErr?.Invoke(e.Data); } catch { /* idem */ }
        };

        try
        {
            if (!proc.Start()) throw new InvalidOperationException("O motor não abriu.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new EngineMissingException(Path.GetFileName(tool.Exe), ex);
        }

        try { proc.StandardInput.Close(); } catch { /* sem entrada */ }
        try
        {
            // Motores em prioridade mais baixa: o PC continua respondendo enquanto comprime.
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch { /* sem permissão: segue normal */ }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        pause?.Register(proc);

        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(outDone.Task, errDone.Task).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            pause?.Unregister(proc);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(tool.Exe)} passou do tempo limite.");
        }
        catch (TimeoutException)
        {
            // saídas não fecharam a tempo: segue com o que temos
        }
        finally
        {
            pause?.Unregister(proc);
        }

        string tailText;
        lock (tail) tailText = string.Join('\n', tail);
        string outText;
        lock (stdout) outText = stdout.ToString();
        return new ProcessResult(proc.ExitCode, outText, tailText, sw.Elapsed);
    }

    private static void Kill(Process p)
    {
        try
        {
            ProcessControl.Resume(p); // processo suspenso não morre direito em alguns casos
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log.Warn("Não consegui encerrar o motor: " + ex.Message);
        }
    }
}

public sealed class EngineMissingException(string name, Exception inner)
    : Exception($"O motor {name} não foi encontrado. Apague a pasta ReduzFacil-dados\\engine e abra o app de novo.", inner);

/// <summary>Suspender/retomar processos (pausa da fila).</summary>
internal static class ProcessControl
{
    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int sig);

    public static void Suspend(Process p) => Signal(p, suspend: true);
    public static void Resume(Process p) => Signal(p, suspend: false);

    private static void Signal(Process p, bool suspend)
    {
        try
        {
            if (p.HasExited) return;
            if (OperatingSystem.IsWindows())
            {
                if (suspend) NtSuspendProcess(p.Handle); else NtResumeProcess(p.Handle);
            }
            else
            {
                SysKill(p.Id, suspend ? 19 /* SIGSTOP */ : 18 /* SIGCONT */);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Pausa do motor falhou: {ex.Message}");
        }
    }
}
