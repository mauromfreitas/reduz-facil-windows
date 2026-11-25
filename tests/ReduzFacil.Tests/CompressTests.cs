using ReduzFacil.Core.Compress;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Output;

namespace ReduzFacil.Tests;

public static class CompressTests
{
    private static Compressor C() => new(Env.NewContext());

    private static OutputSettings Out(string dir) => new() { Mode = OutputMode.Folder, Folder = dir };

    private static async Task<CompressResult> Run(string file, CompressOptions o, string outName)
    {
        var res = await C().RunAsync(Env.F(file), o, Out(Env.NewOut(outName)));
        Console.WriteLine($"      {file}: {res.OriginalBytes / 1024} KB → {res.FinalBytes / 1024} KB ({res.EngineNote}) {string.Join(" | ", res.Warnings)}");
        return res;
    }

    // ---------------- imagem ----------------

    [Teste("imagem: foto por qualidade fica menor, em JPG, até 2560 px e sem GPS")]
    public static async Task FotoQualidade()
    {
        var r = await Run(Fixtures.Photo, new CompressOptions(), "foto-qualidade");
        Assert.True(!r.KeptOriginal && r.OutputPath is not null, "gerou arquivo");
        Assert.True(r.FinalBytes < r.OriginalBytes / 2, "ficou bem menor");
        Assert.True(r.OutputPath!.EndsWith("-reduzido.jpg"), "nome com -reduzido.jpg");
        var info = await ImageCompressor.IdentifyAsync(Env.Tools, r.OutputPath, default);
        Assert.Equal(2560, Math.Max(info.Width, info.Height), "lado maior 2560");
        Assert.Equal("", await Env.Exif(r.OutputPath, "GPSLatitude"), "GPS removido");
        Assert.Equal("", await Env.Exif(r.OutputPath, "Make"), "câmera removida");
        Assert.True(File.GetLastWriteTimeUtc(r.OutputPath) == File.GetLastWriteTimeUtc(Env.F(Fixtures.Photo)), "mesma data do original");
    }

    [Teste("imagem: sem a opção de privacidade o EXIF continua")]
    public static async Task FotoMantemExif()
    {
        Assert.True((await Env.Exif(Env.F(Fixtures.Photo), "Make")) == "TesteCam", "amostra tem EXIF");
        var r = await Run(Fixtures.Photo, new CompressOptions { RemoveMetadata = false, Level = Level.Menor }, "foto-exif");
        Assert.Equal("TesteCam", await Env.Exif(r.OutputPath!, "Make"), "câmera mantida");
        Assert.True((await Env.Exif(r.OutputPath!, "GPSLatitude")).Length > 0, "GPS mantido");
    }

    [Teste("imagem: meta de 1 MB cabe e mantém qualidade razoável")]
    public static async Task FotoMeta()
    {
        var r = await Run(Fixtures.Photo, new CompressOptions { TargetBytes = 1024 * 1024 }, "foto-meta");
        Assert.True(r.FinalBytes <= 1024 * 1024, "até 1 MB");
        Assert.True(r.FinalBytes > 400 * 1024, "não destruiu (mais de 400 KB)");
    }

    [Teste("imagem: meta de 200 KB reduz as dimensões em vez de destruir")]
    public static async Task FotoMetaPequena()
    {
        var r = await Run(Fixtures.Photo, new CompressOptions { TargetBytes = 200 * 1024 }, "foto-meta-200");
        Assert.True(r.FinalBytes <= 200 * 1024, "até 200 KB");
        var info = await ImageCompressor.IdentifyAsync(Env.Tools, r.OutputPath!, default);
        Assert.True(Math.Max(info.Width, info.Height) < 4000, "reduziu dimensões");
        Assert.True(r.Warnings.Any(w => w.Contains("reduzida")), "avisou a redução");
    }

    [Teste("imagem: PNG transparente vira WebP com transparência")]
    public static async Task PngAlpha()
    {
        var r = await Run(Fixtures.Alpha, new CompressOptions(), "png-alpha");
        Assert.True(r.OutputPath!.EndsWith(".webp"), "saiu WebP");
        var info = await ImageCompressor.IdentifyAsync(Env.Tools, r.OutputPath, default);
        Assert.True(info.HasAlpha, "manteve transparência");
    }

    [Teste("imagem: foto já otimizada mantém o original e não cria arquivo")]
    public static async Task JaOtimizada()
    {
        var dir = Env.NewOut("ja-otimizada");
        var r = await C().RunAsync(Env.F(Fixtures.Small), new CompressOptions { Level = Level.Maxima }, Out(dir));
        Assert.True(r.KeptOriginal, "manteve original");
        Assert.Equal(0, Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length, "nenhum arquivo criado");
    }

    [Teste("imagem: Converter: BMP vira JPG; JPG que já é JPG não é refeito")]
    public static async Task ConverterGuarda()
    {
        var r = await Run(Fixtures.Bmp, new CompressOptions { ConvertOnly = true }, "converter-bmp");
        Assert.True(!r.KeptOriginal && r.OutputPath!.EndsWith(".jpg"), "gerou o JPG convertido");
        var r2 = await Run(Fixtures.Small, new CompressOptions { ConvertOnly = true }, "converter-jpg");
        Assert.True(r2.KeptOriginal, "JPG pequeno continua o original");
    }

    [Teste("imagem: formato JPG pedido guarda a conversão mesmo se maior")]
    public static async Task FormatoPedidoGuarda()
    {
        var r = await Run(Fixtures.TinyPng, new CompressOptions { ImageFormat = ImageFormat.Jpg, Level = Level.Maxima }, "png-para-jpg");
        Assert.True(!r.KeptOriginal && r.OutputPath!.EndsWith(".jpg"), "JPG salvo");
    }

    [Teste("imagem: HEIC do iPhone vira JPG")]
    public static async Task Heic()
    {
        if (!Fixtures.HasHeic) Assert.Skip("sem HEIC de teste neste sistema");
        var r = await Run(Fixtures.Heic, new CompressOptions { ImageFormat = ImageFormat.Jpg }, "heic");
        Assert.True(r.OutputPath!.EndsWith(".jpg"), "virou JPG");
        var info = await ImageCompressor.IdentifyAsync(Env.Tools, r.OutputPath, default);
        Assert.True(info.Width == 2000 && info.Height == 1500, $"mesmas dimensões ({info.Width}×{info.Height})");
    }

    // ---------------- vídeo ----------------

    [Teste("vídeo: por qualidade fica menor, MP4 tocável, mesma duração, sem localização")]
    public static async Task VideoQualidade()
    {
        var r = await Run(Fixtures.Video, new CompressOptions(), "video-qualidade");
        Assert.True(r.FinalBytes < r.OriginalBytes / 2, "ficou bem menor");
        var p = await Env.Probe(r.OutputPath!);
        Assert.True(p.HasVideo && p.HasAudio, "tem imagem e som");
        Assert.Equal("h264", p.VideoCodec, "H.264");
        Assert.Near(12, p.DurationSec, 0.3, "duração");
        Assert.True(p.CreationTime?.StartsWith("2026-01-02") == true, "data de gravação mantida");
        var tags = await Env.Run(Env.Tools.Ffprobe, "-v", "error", "-show_entries", "format_tags", "-of", "default=nw=1", r.OutputPath!);
        Assert.True(!tags.Contains("location", StringComparison.OrdinalIgnoreCase), "localização removida");
    }

    [Teste("vídeo: meta de 3 MB cabe (2 passadas) e fica perto da meta")]
    public static async Task VideoMeta()
    {
        const long target = 3 * 1024 * 1024;
        var r = await Run(Fixtures.Video, new CompressOptions { TargetBytes = target }, "video-meta");
        Assert.True(r.FinalBytes <= target, "cabe na meta");
        Assert.True(r.FinalBytes > target * 0.7, "aproveita a meta (mais de 70%)");
    }

    [Teste("vídeo: meta pequena demais explica o mínimo")]
    public static async Task VideoMetaImpossivel()
    {
        var ex = await Assert.ThrowsAsync<UserFacingException>(
            () => C().RunAsync(Env.F(Fixtures.Video), new CompressOptions { TargetBytes = 20 * 1024 }, Out(Env.NewOut("video-impossivel"))),
            "meta impossível");
        Assert.True(ex.Message.Contains("mínimo"), "fala do mínimo: " + ex.Message);
    }

    [Teste("vídeo: cortar 2–6 s, girar 90°, sem áudio")]
    public static async Task VideoEdicoes()
    {
        var o = new CompressOptions { TrimStart = TimeSpan.FromSeconds(2), TrimEnd = TimeSpan.FromSeconds(6), Rotate = 90, RemoveAudio = true, Level = Level.Menor };
        var r = await Run(Fixtures.Video, o, "video-edicoes");
        var p = await Env.Probe(r.OutputPath!);
        Assert.Near(4, p.DurationSec, 0.25, "duração do corte");
        Assert.True(p.DisplayHeight > p.DisplayWidth, $"ficou em pé ({p.DisplayWidth}×{p.DisplayHeight})");
        Assert.Equal(720, p.DisplayWidth, "nível Menor: lado menor 720");
        Assert.True(!p.HasAudio, "sem áudio");
    }

    [Teste("vídeo: extrair só o áudio gera MP3")]
    public static async Task VideoExtrairAudio()
    {
        var r = await Run(Fixtures.Video, new CompressOptions { ExtractAudioOnly = true }, "video-audio");
        Assert.True(r.OutputPath!.EndsWith(".mp3"), "MP3");
        var p = await Env.Probe(r.OutputPath);
        Assert.True(p.HasAudio && !p.HasVideo, "só áudio");
        Assert.Near(12, p.DurationSec, 0.4, "duração");
    }

    [Teste("vídeo: gravado em pé continua em pé e limita o lado menor")]
    public static async Task VideoEmPe()
    {
        var src = await Env.Probe(Env.F(Fixtures.Rotated));
        Assert.True(src.Rotation is 90 or 270, $"amostra tem rotação ({src.Rotation})");
        var r = await Run(Fixtures.Rotated, new CompressOptions { Level = Level.Menor }, "video-em-pe");
        var p = await Env.Probe(r.OutputPath!);
        Assert.Equal(720, p.DisplayWidth, "largura 720 (lado menor)");
        Assert.Equal(1280, p.DisplayHeight, "altura 1280");
    }

    [Teste("vídeo: HDR do iPhone vira SDR (BT.709, 8 bits)")]
    public static async Task VideoHdr()
    {
        var src = await Env.Probe(Env.F(Fixtures.Hdr));
        Assert.True(src.IsHdr, "amostra é HDR");
        var r = await Run(Fixtures.Hdr, new CompressOptions { ConvertOnly = true }, "video-hdr");
        var p = await Env.Probe(r.OutputPath!);
        Assert.Equal("yuv420p", p.PixFmt, "8 bits");
        Assert.True(!p.IsHdr, "não é mais HDR");
    }

    [Teste("vídeo: H.265 sai com etiqueta hvc1 (abre no iPhone/Mac)")]
    public static async Task VideoH265()
    {
        var r = await Run(Fixtures.Rotated, new CompressOptions { VideoCodec = VideoCodec.H265 }, "video-h265");
        var tag = await Env.Run(Env.Tools.Ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,codec_tag_string", "-of", "csv=p=0", r.OutputPath!);
        Assert.True(tag.Contains("hevc") && tag.Contains("hvc1"), "hevc/hvc1: " + tag.Trim());
    }

    [Teste("vídeo: arquivo corrompido dá mensagem clara")]
    public static async Task VideoQuebrado()
    {
        var ex = await Assert.ThrowsAsync<UserFacingException>(
            () => C().RunAsync(Env.F(Fixtures.Broken), new CompressOptions(), Out(Env.NewOut("quebrado"))), "corrompido");
        Assert.True(ex.Message.Contains("corrompido"), ex.Message);
    }

    [Teste("vídeo: placa de vídeo que falha cai para o processador")]
    public static async Task VideoFallbackPlaca()
    {
        var hw = new HardwareCatalog();
        hw.SetForTests("h264_nvenc"); // finge uma NVIDIA que não existe aqui
        var c = new Compressor(Env.NewContext(hw));
        var r = await c.RunAsync(Env.F(Fixtures.Rotated), new CompressOptions(), Out(Env.NewOut("fallback")));
        Assert.True(File.Exists(r.OutputPath), "gerou o vídeo");
        Assert.True(r.EngineNote!.Contains("Processador"), "feito no processador: " + r.EngineNote);
        Assert.True(r.Warnings.Any(w => w.Contains("placa de vídeo")), "avisou");
    }

    // ---------------- áudio ----------------

    [Teste("áudio: WAV vira MP3 bem menor; meta de 300 KB cabe")]
    public static async Task Audio()
    {
        var r = await Run(Fixtures.Wav, new CompressOptions(), "audio");
        Assert.True(r.OutputPath!.EndsWith(".mp3") && r.FinalBytes < r.OriginalBytes / 5, "MP3 bem menor");
        var r2 = await Run(Fixtures.Wav, new CompressOptions { TargetBytes = 300 * 1024 }, "audio-meta");
        Assert.True(r2.FinalBytes <= 300 * 1024, "cabe na meta");
    }

    // ---------------- PDF ----------------

    [Teste("pdf: escaneado fica bem menor por qualidade")]
    public static async Task PdfEscaneado()
    {
        var r = await Run(Fixtures.Scanned, new CompressOptions(), "pdf");
        Assert.True(r.FinalBytes < r.OriginalBytes * 0.7, "ficou menor");
        var pages = await Env.Run(Env.Tools.Ghostscript, "-q", "-dNODISPLAY", "-dNOSAFER", "-c", $"({r.OutputPath!.Replace("\\", "/")}) (r) file runpdfbegin pdfpagecount = quit");
        Assert.Equal("3", pages.Trim(), "3 páginas");
    }

    [Teste("pdf: meta de 500 KB cabe")]
    public static async Task PdfMeta()
    {
        var r = await Run(Fixtures.Scanned, new CompressOptions { TargetBytes = 500 * 1024 }, "pdf-meta");
        Assert.True(r.FinalBytes <= 500 * 1024, "cabe na meta");
    }

    [Teste("pdf: tons de cinza fica ainda menor")]
    public static async Task PdfCinza()
    {
        var a = await Run(Fixtures.Scanned, new CompressOptions(), "pdf-cor");
        var b = await Run(Fixtures.Scanned, new CompressOptions { PdfGrayscale = true }, "pdf-cinza");
        Assert.True(b.FinalBytes <= a.FinalBytes, "cinza ≤ colorido");
    }

    [Teste("pdf: só texto mantém o original")]
    public static async Task PdfTexto()
    {
        var r = await Run(Fixtures.TextPdf, new CompressOptions(), "pdf-texto");
        Assert.True(r.KeptOriginal, "manteve original");
    }

    [Teste("pdf: com senha dá mensagem clara")]
    public static async Task PdfSenha()
    {
        var ex = await Assert.ThrowsAsync<UserFacingException>(
            () => C().RunAsync(Env.F(Fixtures.Locked), new CompressOptions(), Out(Env.NewOut("pdf-senha"))), "senha");
        Assert.True(ex.Message.Contains("senha"), ex.Message);
    }

    // ---------------- saída ----------------

    [Teste("saída: ao lado do original, nomes repetidos ganham (2), original intacto")]
    public static async Task SaidaAoLado()
    {
        var dir = Env.NewOut("ao-lado");
        var src = Path.Combine(dir, "ação.jpg");
        File.Copy(Env.F(Fixtures.Photo), src);
        var before = File.ReadAllBytes(src);
        var s = new OutputSettings { Mode = OutputMode.NextToOriginal, Suffix = "" };
        var r1 = await C().RunAsync(src, new CompressOptions(), s);
        var r2 = await C().RunAsync(src, new CompressOptions(), s);
        Assert.Equal(Path.Combine(dir, "ação-reduzido.jpg"), r1.OutputPath, "sufixo obrigatório ao lado");
        Assert.Equal(Path.Combine(dir, "ação-reduzido (2).jpg"), r2.OutputPath, "não sobrescreve");
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(src)), "original intacto");
    }

    [Teste("saída: pasta escolhida recria as subpastas da pasta arrastada")]
    public static void SaidaSubpastas()
    {
        var s = new OutputSettings { Mode = OutputMode.Folder, Folder = Path.Combine(Env.Root, "destino") };
        var root = Path.Combine(Env.Root, "Viagem");
        var p = OutputNamer.Decide(s, Path.Combine(root, "Dia 1", "a.jpg"), "jpg", root);
        Assert.Equal(Path.Combine(Env.Root, "destino", "Viagem", "Dia 1", "a-reduzido.jpg"), p, "subpastas");
        var p2 = OutputNamer.Decide(new OutputSettings { Mode = OutputMode.Folder, Folder = s.Folder, Suffix = "" }, Path.Combine(root, "b.png"), "jpg", null);
        Assert.Equal(Path.Combine(Env.Root, "destino", "b.jpg"), p2, "sem sufixo em outra pasta");
    }

    [Teste("planejador: bitrate da meta, resolução cai com pouco bit")]
    public static void Planejador()
    {
        var (v, a) = VideoPlanner.BitrateForTarget(16L * 1024 * 1024, 60, true);
        Assert.True(v > 1_800_000 && v < 2_200_000, $"~2 Mbps para 16 MB/min ({v})");
        Assert.Equal(128_000L, a, "áudio 128k (sobra bit)");
        var (_, a2) = VideoPlanner.BitrateForTarget(16L * 1024 * 1024, 180, true);
        Assert.Equal(96_000L, a2, "áudio 96k com 3 min");
        var info = new Core.Media.MediaInfo { DurationSec = 600, Width = 3840, Height = 2160, Fps = 60, HasVideo = true, HasAudio = true };
        var plan = VideoPlanner.Build(info, new CompressOptions { TargetBytes = 16L * 1024 * 1024 }, VideoEncoder.Cpu(VideoCodec.H264), "in", "out", "p");
        Assert.True(plan.OutHeight <= 480, $"10 min em 16 MB cai para ≤480p ({plan.OutWidth}×{plan.OutHeight})");
        Assert.True(plan.Passes.Count == 2 && plan.TwoPass, "2 passadas no processador");
        Assert.True(string.Join(' ', plan.Passes[1].Args).Contains("fps=30"), "limita a 30 fps");
        var hw = VideoPlanner.Build(info, new CompressOptions { TargetBytes = 16L * 1024 * 1024 }, new VideoEncoder(EncoderFamily.Intel, VideoCodec.H264, "h264_qsv"), "in", "out", "p");
        Assert.True(hw.Passes.Count == 1 && string.Join(' ', hw.Passes[0].Args).Contains("format=nv12"), "Quick Sync: 1 passada, nv12");
    }
}
