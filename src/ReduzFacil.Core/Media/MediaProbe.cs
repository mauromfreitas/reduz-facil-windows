using System.Globalization;
using System.Text.Json;
using ReduzFacil.Core.Engines;
using ReduzFacil.Core.Model;

namespace ReduzFacil.Core.Media;

/// <summary>O que o ffprobe conta sobre um vídeo ou áudio.</summary>
public sealed class MediaInfo
{
    public double DurationSec { get; init; }
    public long Bitrate { get; init; }
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Rotação de exibição (vídeo de celular "em pé"): 0, 90, 180, 270.</summary>
    public int Rotation { get; init; }
    public double Fps { get; init; }
    public string? VideoCodec { get; init; }
    public string? PixFmt { get; init; }
    public string? ColorTransfer { get; init; }
    public long VideoBitrate { get; init; }
    public string? AudioCodec { get; init; }
    public long AudioBitrate { get; init; }
    public int AudioChannels { get; init; }
    public bool HasCoverArt { get; init; }
    public string? CreationTime { get; init; }

    /// <summary>Largura e altura como aparecem na tela (já considerando a rotação).</summary>
    public int DisplayWidth => Rotation is 90 or 270 ? Height : Width;
    public int DisplayHeight => Rotation is 90 or 270 ? Width : Height;

    /// <summary>HDR (vídeos do iPhone em HLG, HDR10): precisa de conversão de cores para não ficar "lavado".</summary>
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
}

public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(EngineTools tools, string path, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync(tools.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            tools.Environment, captureStdOut: true, timeout: TimeSpan.FromSeconds(60), ct: ct).ConfigureAwait(false);
        if (!res.Ok || string.IsNullOrWhiteSpace(res.StdOut))
            throw new UserFacingException("Não consegui ler este arquivo. Ele pode estar corrompido ou num formato incomum.");
        return Parse(res.StdOut);
    }

    internal static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double duration = 0;
        long bitrate = 0;
        string? creation = null;
        if (root.TryGetProperty("format", out var fmt))
        {
            duration = Num(fmt, "duration");
            bitrate = (long)Num(fmt, "bit_rate");
            creation = Tag(fmt, "creation_time");
        }

        JsonElement? video = null, audio = null;
        var cover = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                var attached = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video")
                {
                    if (attached) cover = true;
                    else video ??= s;
                }
                else if (type == "audio") audio ??= s;
            }
        }

        int w = 0, h = 0, rot = 0;
        double fps = 0;
        string? vcodec = null, pix = null, trc = null, acodec = null;
        long vbr = 0, abr = 0;
        int ach = 0;
        if (video is { } v)
        {
            w = (int)Num(v, "width");
            h = (int)Num(v, "height");
            vcodec = Str(v, "codec_name");
            pix = Str(v, "pix_fmt");
            trc = Str(v, "color_transfer");
            vbr = (long)Num(v, "bit_rate");
            fps = Rate(Str(v, "avg_frame_rate")) is var a && a > 0 ? a : Rate(Str(v, "r_frame_rate"));
            rot = RotationOf(v);
            if (duration <= 0) duration = Num(v, "duration");
        }
        if (audio is { } au)
        {
            acodec = Str(au, "codec_name");
            abr = (long)Num(au, "bit_rate");
            ach = (int)Num(au, "channels");
            if (duration <= 0) duration = Num(au, "duration");
        }

        return new MediaInfo
        {
            DurationSec = duration,
            Bitrate = bitrate,
            HasVideo = video is not null,
            HasAudio = audio is not null,
            Width = w,
            Height = h,
            Rotation = rot,
            Fps = fps is > 0 and < 1000 ? fps : 30,
            VideoCodec = vcodec,
            PixFmt = pix,
            ColorTransfer = trc,
            VideoBitrate = vbr,
            AudioCodec = acodec,
            AudioBitrate = abr,
            AudioChannels = ach,
            HasCoverArt = cover,
            CreationTime = creation,
        };
    }

    private static int RotationOf(JsonElement v)
    {
        double r = 0;
        if (v.TryGetProperty("side_data_list", out var sdl))
            foreach (var sd in sdl.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var rr) && rr.TryGetDouble(out var d)) r = d;
        if (r == 0 && Tag(v, "rotate") is { } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var tr))
            r = -tr; // a tag antiga "rotate" tem o sinal contrário da matriz de exibição
        var deg = (int)Math.Round(r);
        deg = ((deg % 360) + 360) % 360;
        // A matriz de exibição usa sentido anti-horário; convertemos para "quanto girar para ficar em pé" (horário).
        deg = (360 - deg) % 360;
        return deg is 90 or 180 or 270 ? deg : 0;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number) return p.GetDouble();
        if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }

    private static string? Tag(JsonElement e, string name) =>
        e.TryGetProperty("tags", out var tags) && tags.TryGetProperty(name, out var t) ? t.GetString() : null;

    private static double Rate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var parts = text.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }
}
