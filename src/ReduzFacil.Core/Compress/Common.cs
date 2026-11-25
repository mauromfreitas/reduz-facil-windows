using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;

namespace ReduzFacil.Core.Compress;

/// <summary>O que os compressores precisam: motores, placa de vídeo e pausa.</summary>
public sealed class CompressContext
{
    public required EngineTools Tools { get; init; }
    public HardwareCatalog Hardware { get; init; } = new();
    public PauseGate Pause { get; init; } = new();
}

/// <summary>Um trabalho em andamento: pasta temporária própria, progresso e cancelamento.</summary>
public sealed class JobScope : IDisposable
{
    public JobScope(EngineTools tools, string inputPath, Action<double>? progress, CancellationToken ct)
    {
        InputPath = inputPath;
        Progress = progress;
        Token = ct;
        Dir = Path.Combine(tools.WorkDir, "job-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Dir);
    }

    public string InputPath { get; }
    public string Dir { get; }
    public CancellationToken Token { get; }
    private Action<double>? Progress { get; }

    /// <summary>Caminho só com letras simples dentro da pasta do trabalho (motores e acentos não brigam).</summary>
    public string Temp(string name) => Path.Combine(Dir, name);

    public void Report(double fraction)
    {
        try { Progress?.Invoke(Math.Clamp(fraction, 0, 1)); } catch { /* interface fechando */ }
    }

    public void Dispose()
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(200); // motor acabou de ser encerrado e ainda segura o arquivo
            }
        }
    }
}

/// <summary>O que um compressor produziu, ainda na pasta temporária.</summary>
public sealed class EngineOutput
{
    public required string TempPath { get; init; }
    /// <summary>Extensão final, sem ponto (jpg, mp4, pdf...).</summary>
    public required string Extension { get; init; }
    public List<string> Warnings { get; init; } = [];
    public string? EngineNote { get; init; }
    /// <summary>Guardar mesmo se ficar maior que o original (conversões pedidas).</summary>
    public bool KeepEvenIfBigger { get; init; }

    /// <summary>Nada a fazer (ex.: "Converter" num arquivo que já está num formato compatível). TempPath fica vazio.</summary>
    public string? NothingToDo { get; init; }

    public static EngineOutput Nothing(string why) => new() { TempPath = "", Extension = "", NothingToDo = why };
}

public interface IKindCompressor
{
    Task<EngineOutput> CompressAsync(CompressContext ctx, JobScope job, CompressOptions o);
}
