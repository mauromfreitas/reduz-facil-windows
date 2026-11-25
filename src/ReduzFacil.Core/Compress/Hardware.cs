using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Compress;

public enum EncoderFamily { Cpu, Nvidia, Intel, Amd }

/// <summary>Um codificador de vídeo do FFmpeg.</summary>
public sealed record VideoEncoder(EncoderFamily Family, VideoCodec Codec, string Name)
{
    public bool IsHardware => Family != EncoderFamily.Cpu;

    public string Label => Family switch
    {
        EncoderFamily.Nvidia => "Placa NVIDIA",
        EncoderFamily.Intel => "Intel Quick Sync",
        EncoderFamily.Amd => "Placa AMD",
        _ => "Processador",
    } + (Codec == VideoCodec.H265 ? " (H.265)" : " (H.264)");

    public static VideoEncoder Cpu(VideoCodec c) => new(EncoderFamily.Cpu, c, c == VideoCodec.H265 ? "libx265" : "libx264");

    public static IEnumerable<VideoEncoder> HardwareCandidates()
    {
        // Ordem de preferência quando há mais de uma placa: NVIDIA, Intel, AMD.
        yield return new(EncoderFamily.Nvidia, VideoCodec.H264, "h264_nvenc");
        yield return new(EncoderFamily.Nvidia, VideoCodec.H265, "hevc_nvenc");
        yield return new(EncoderFamily.Intel, VideoCodec.H264, "h264_qsv");
        yield return new(EncoderFamily.Intel, VideoCodec.H265, "hevc_qsv");
        yield return new(EncoderFamily.Amd, VideoCodec.H264, "h264_amf");
        yield return new(EncoderFamily.Amd, VideoCodec.H265, "hevc_amf");
    }

    public static VideoEncoder? FromName(string? name) =>
        HardwareCandidates().Concat([Cpu(VideoCodec.H264), Cpu(VideoCodec.H265)]).FirstOrDefault(e => e.Name == name);
}

/// <summary>
/// Descobre quais codificadores de placa de vídeo funcionam neste PC, com um teste de verdade
/// (o FFmpeg lista todos, mas só funcionam os que têm placa e driver).
/// Se uma placa falhar durante o uso, ela é desligada até o app fechar e o vídeo é refeito no processador.
/// </summary>
public sealed class HardwareCatalog
{
    private readonly object _gate = new();
    private readonly HashSet<string> _working = [];
    private readonly Dictionary<string, int> _failures = [];
    private Task? _detection;

    public bool Detected { get; private set; }

    /// <summary>Nomes dos que funcionaram no teste (para mostrar nas configurações).</summary>
    public IReadOnlyList<VideoEncoder> Working
    {
        get
        {
            lock (_gate) return VideoEncoder.HardwareCandidates().Where(e => _working.Contains(e.Name)).ToList();
        }
    }

    public Task DetectAsync(EngineTools tools, CancellationToken ct = default)
    {
        lock (_gate) return _detection ??= RunDetectionAsync(tools, ct);
    }

    /// <summary>Esquece o resultado e testa de novo (botão "Detectar de novo").</summary>
    public Task RedetectAsync(EngineTools tools, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _working.Clear();
            _failures.Clear();
            Detected = false;
            _detection = RunDetectionAsync(tools, ct);
            return _detection;
        }
    }

    /// <summary>Só para testes: finge que estes codificadores funcionam.</summary>
    internal void SetForTests(params string[] names)
    {
        lock (_gate)
        {
            _working.Clear();
            foreach (var n in names) _working.Add(n);
            Detected = true;
            _detection = Task.CompletedTask;
        }
    }

    private async Task RunDetectionAsync(EngineTools tools, CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var enc in VideoEncoder.HardwareCandidates())
        {
            try
            {
                var pix = enc.Family == EncoderFamily.Intel ? "nv12" : "yuv420p";
                var res = await ProcessRunner.RunAsync(tools.Ffmpeg,
                    ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30:d=0.5",
                     "-frames:v", "10", "-pix_fmt", pix, "-c:v", enc.Name, "-f", "null", EngineTools.NullOutput],
                    tools.Environment, timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
                if (res.Ok) found.Add(enc.Name);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Teste do codificador {enc.Name} falhou: {ex.Message}");
            }
        }
        lock (_gate)
        {
            foreach (var n in found) _working.Add(n);
            Detected = true;
        }
        Log.Info("Placa de vídeo: " + (found.Count == 0 ? "nenhuma disponível, vídeos no processador" : string.Join(", ", found)));
    }

    /// <summary>Melhor codificador para o codec pedido. Sem placa (ou desligada), o processador.</summary>
    public VideoEncoder Choose(VideoCodec codec, bool allowHardware)
    {
        if (allowHardware)
        {
            lock (_gate)
            {
                var hw = VideoEncoder.HardwareCandidates()
                    .FirstOrDefault(e => e.Codec == codec && _working.Contains(e.Name) && _failures.GetValueOrDefault(e.Name) < 2);
                if (hw is not null) return hw;
            }
        }
        return VideoEncoder.Cpu(codec);
    }

    /// <summary>A placa falhou num vídeo. Duas falhas desligam esse codificador até o app fechar.</summary>
    public void ReportFailure(VideoEncoder enc)
    {
        if (!enc.IsHardware) return;
        lock (_gate)
        {
            _failures[enc.Name] = _failures.GetValueOrDefault(enc.Name) + 1;
            if (_failures[enc.Name] >= 2) Log.Warn($"Codificador {enc.Name} desligado depois de falhar 2 vezes.");
        }
    }
}
