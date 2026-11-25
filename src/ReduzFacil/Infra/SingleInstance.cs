using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

/// <summary>
/// Uma única instância por usuário do Windows. Abrir de novo (ou usar o botão direito do Explorer
/// com vários arquivos) manda os argumentos para a instância que já está aberta, pelo "cano" nomeado.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // string.GetHashCode muda a cada processo: aqui precisa ser o mesmo em todas as aberturas.
    private static readonly string Id = "ReduzFacil." + StableHash(Environment.UserDomainName + "\\" + Environment.UserName);
    private static readonly string MutexName = @"Local\" + Id + ".Instancia";
    private static readonly string PipeName = Id + ".Argumentos";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                if (!mutex.WaitOne(0))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // A instância anterior caiu sem soltar: assumimos.
            }
        }
        return new SingleInstance(mutex);
    }

    /// <summary>Manda os argumentos para a instância aberta. Tenta por até ~10 s (ela pode estar abrindo).</summary>
    public static bool SendToExisting(string[] args)
    {
        var payload = JsonSerializer.Serialize(args.Length == 0 ? ["--mostrar"] : args);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            try
            {
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1000);
                var bytes = Encoding.UTF8.GetBytes(payload);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(250);
            }
        }
        return false;
    }

    /// <summary>Começa a ouvir. Cada mensagem traz os argumentos de uma nova abertura.</summary>
    public void StartListening(Action<string[]> onArgs)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, _cts.Token).ConfigureAwait(false);
                    var args = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(ms.ToArray())) ?? [];
                    try { onArgs(args); } catch (Exception ex) { Log.Error("Erro ao receber argumentos", ex); }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("Cano de instância única: " + ex.Message);
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
        });
    }

    private static string StableHash(string text)
    {
        uint h = 2166136261;
        foreach (var c in text.ToLowerInvariant()) h = (h ^ c) * 16777619;
        return h.ToString("x8");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { /* já liberado */ }
        _mutex.Dispose();
    }
}
