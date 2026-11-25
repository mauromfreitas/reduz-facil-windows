using System.Text.Json.Serialization;

namespace ReduzFacil.Core.Model;

public enum FileKind { Image, Video, Audio, Pdf }

/// <summary>Nível de compressão quando não há meta de tamanho (igual ao app web).</summary>
public enum Level { Maxima, Equilibrado, Menor }

public enum VideoCodec { H264, H265 }

/// <summary>Formato da imagem de saída.</summary>
public enum ImageFormat
{
    /// <summary>JPG; WebP quando a imagem tem transparência.</summary>
    Auto,
    Jpg,
    WebP,
    /// <summary>Mesmo formato do original (HEIC/BMP/AVIF viram JPG).</summary>
    Manter,
}

public enum AudioFormat
{
    /// <summary>MP3 e M4A continuam como estão; WAV, FLAC e outros viram MP3.</summary>
    Auto,
    Mp3,
    M4a,
}

/// <summary>O que fazer com cada arquivo. Copiado para cada item da fila no momento em que entra.</summary>
public sealed class CompressOptions
{
    /// <summary>Nível usado quando não há meta (ou como ponto de partida da meta).</summary>
    public Level Level { get; set; } = Level.Equilibrado;

    /// <summary>Meta de tamanho por arquivo, em bytes. Null = por qualidade.</summary>
    public long? TargetBytes { get; set; }

    /// <summary>Modo "Converter": prioriza compatibilidade (JPG/MP4/MP3) e guarda o resultado mesmo se ficar maior.</summary>
    public bool ConvertOnly { get; set; }

    // Imagem
    public ImageFormat ImageFormat { get; set; } = ImageFormat.Auto;
    /// <summary>Maior lado máximo da imagem em pixels (0 = o do nível).</summary>
    public int ImageMaxSide { get; set; }

    // Vídeo
    public VideoCodec VideoCodec { get; set; } = VideoCodec.H264;
    /// <summary>Lado menor máximo do vídeo (1080, 720, 480...). 0 = o do nível.</summary>
    public int VideoMaxShortSide { get; set; }
    public bool UseHardware { get; set; } = true;

    // Áudio
    public AudioFormat AudioFormat { get; set; } = AudioFormat.Auto;

    // PDF
    public bool PdfGrayscale { get; set; }

    /// <summary>Remove localização (GPS), câmera e outros dados das fotos e vídeos.</summary>
    public bool RemoveMetadata { get; set; } = true;

    // Ajustes por arquivo (só vídeo): cortar, girar, sem áudio, só o áudio
    public TimeSpan? TrimStart { get; set; }
    public TimeSpan? TrimEnd { get; set; }
    /// <summary>0, 90, 180 ou 270 graus no sentido horário.</summary>
    public int Rotate { get; set; }
    public bool RemoveAudio { get; set; }
    public bool ExtractAudioOnly { get; set; }
    /// <summary>Dividir o vídeo em partes de até este tamanho (ex.: 16 MB para o WhatsApp). Null = não dividir.</summary>
    public long? SplitPartBytes { get; set; }

    public CompressOptions Clone()
    {
        var c = (CompressOptions)MemberwiseClone();
        return c;
    }

    [JsonIgnore]
    public bool HasVideoEdits => TrimStart is not null || TrimEnd is not null || Rotate != 0 || RemoveAudio || ExtractAudioOnly || SplitPartBytes is > 0;
}

/// <summary>Resultado de um arquivo.</summary>
public sealed class CompressResult
{
    public required string InputPath { get; init; }
    public long OriginalBytes { get; init; }

    /// <summary>Arquivo gerado (null quando o original foi mantido).</summary>
    public string? OutputPath { get; init; }

    /// <summary>Demais arquivos gerados (partes 2, 3… de um vídeo dividido).</summary>
    public List<string> ExtraOutputs { get; init; } = [];

    public IEnumerable<string> AllOutputs => OutputPath is null ? ExtraOutputs : new[] { OutputPath }.Concat(ExtraOutputs);
    public long FinalBytes { get; init; }

    /// <summary>Não houve ganho: nenhum arquivo novo foi gerado.</summary>
    public bool KeptOriginal { get; init; }

    public List<string> Warnings { get; init; } = [];

    /// <summary>Motor usado (ex.: "Intel Quick Sync (H.264)") — mostrado nos detalhes.</summary>
    public string? EngineNote { get; init; }

    public int SavingPercent => Util.Format.SavingPercent(OriginalBytes, KeptOriginal ? OriginalBytes : FinalBytes);
}

/// <summary>Erro com mensagem já pronta para o usuário.</summary>
public sealed class UserFacingException(string message, Exception? inner = null) : Exception(message, inner);
