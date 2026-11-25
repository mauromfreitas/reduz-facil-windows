using ReduzFacil.Core.Compress;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Output;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Scan;
using ReduzFacil.Core.Settings;
using ReduzFacil.Core.Stats;
using ReduzFacil.Core.Util;
using ReduzFacil.Core.Watch;

namespace ReduzFacil.Tests;

public static class QueueTests
{
    private static JobQueue NewQueue(string outDir, out Compressor c)
    {
        c = new Compressor(Env.NewContext());
        var o = new OutputSettings { Mode = OutputMode.Folder, Folder = outDir };
        return new JobQueue(c, () => o);
    }

    private static async Task WaitIdle(JobQueue q, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (q.IsBusy)
        {
            if (DateTime.UtcNow > until) throw new Exception("Falhou: fila não terminou a tempo");
            await Task.Delay(100);
        }
    }

    [Teste("fila: vários tipos juntos, vídeo numa pista e o resto em paralelo")]
    public static async Task FilaMista()
    {
        var dir = Env.NewOut("fila-mista");
        var q = NewQueue(dir, out _);
        q.HeavyLimit = 1;
        q.LightLimit = 3;
        var maxLight = 0;
        q.ItemChanged += _ =>
        {
            var running = q.Items.Count(i => i.Status == JobStatus.Running && !i.IsHeavy);
            lock (q) maxLight = Math.Max(maxLight, running);
        };
        foreach (var f in new[] { Fixtures.Photo, Fixtures.Alpha, Fixtures.Scanned, Fixtures.Wav, Fixtures.Rotated, Fixtures.Bmp })
            q.Add(Env.F(f), new CompressOptions());
        Assert.Equal(6, q.Items.Count, "6 na fila");
        Assert.True(q.Add(Env.F(Fixtures.Photo), new CompressOptions()) == q.Items[0], "mesmo arquivo não entra duas vezes");
        await WaitIdle(q, TimeSpan.FromMinutes(3));
        Assert.True(q.Items.All(i => i.Status is JobStatus.Done or JobStatus.KeptOriginal), string.Join(", ", q.Items.Select(i => $"{i.Name}={i.Status} {i.Error}")));
        Assert.True(maxLight >= 2, $"rodou em paralelo ({maxLight})");
    }

    [Teste("fila: cancelar vídeo no meio não deixa arquivo nem temporário")]
    public static async Task FilaCancelar()
    {
        var dir = Env.NewOut("fila-cancelar");
        var q = NewQueue(dir, out _);
        var item = q.Add(Env.F(Fixtures.Video), new CompressOptions { Level = Level.Maxima })!;
        var until = DateTime.UtcNow.AddSeconds(30);
        while (item.Progress < 0.1 && DateTime.UtcNow < until) await Task.Delay(50);
        q.Cancel(item);
        await WaitIdle(q, TimeSpan.FromSeconds(30));
        Assert.Equal(JobStatus.Canceled, item.Status, "cancelado");
        Assert.Equal(0, Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length, "nenhum arquivo de saída");
        await Task.Delay(500);
        Assert.Equal(0, Directory.GetDirectories(Env.Work, "job-*").Length, "pasta de trabalho limpa");
    }

    [Teste("fila: pausar suspende o motor e continuar termina")]
    public static async Task FilaPausar()
    {
        var dir = Env.NewOut("fila-pausar");
        var q = NewQueue(dir, out _);
        var item = q.Add(Env.F(Fixtures.Video), new CompressOptions())!;
        var until = DateTime.UtcNow.AddSeconds(30);
        while (item.Progress < 0.05 && DateTime.UtcNow < until) await Task.Delay(50);
        q.PauseAll();
        await Task.Delay(400);
        var p1 = item.Progress;
        await Task.Delay(2000);
        Assert.True(item.Progress - p1 < 0.02, $"parou durante a pausa ({p1:0.00} → {item.Progress:0.00})");
        q.ResumeAll();
        await WaitIdle(q, TimeSpan.FromMinutes(2));
        Assert.Equal(JobStatus.Done, item.Status, "terminou depois de continuar");
    }

    [Teste("fila: sem começar automaticamente espera o Iniciar; falha pode tentar de novo")]
    public static async Task FilaManual()
    {
        var dir = Env.NewOut("fila-manual");
        var q = NewQueue(dir, out _);
        q.AutoStart = false;
        var ok = q.Add(Env.F(Fixtures.Bmp), new CompressOptions())!;
        var bad = q.Add(Env.F(Fixtures.Broken), new CompressOptions())!;
        await Task.Delay(500);
        Assert.Equal(JobStatus.Waiting, ok.Status, "esperando");
        q.StartAll();
        await WaitIdle(q, TimeSpan.FromMinutes(1));
        Assert.Equal(JobStatus.Done, ok.Status, "bmp ok");
        Assert.Equal(JobStatus.Failed, bad.Status, "quebrado falhou");
        Assert.True(bad.Error!.Contains("corrompido"), bad.Error);
        q.Retry(bad);
        await WaitIdle(q, TimeSpan.FromMinutes(1));
        Assert.Equal(JobStatus.Failed, bad.Status, "falhou de novo (esperado)");
    }

    [Teste("coletor: pasta arrastada pula saídas nossas, temporários e ocultos")]
    public static void Coletor()
    {
        var root = Env.NewOut("coletor");
        Directory.CreateDirectory(Path.Combine(root, "Sub"));
        File.WriteAllText(Path.Combine(root, "a.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "a-reduzido.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "a-reduzido (3).jpg"), "x");
        File.WriteAllText(Path.Combine(root, "b.mp4.crdownload"), "x");
        File.WriteAllText(Path.Combine(root, "notas.txt"), "x");
        File.WriteAllText(Path.Combine(root, "Sub", "c.pdf"), "x");
        var found = FileCollector.Collect([root], "-reduzido");
        var names = found.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();
        Assert.Equal("a.jpg,c.pdf", string.Join(",", names), "arquivos certos");
        Assert.True(found.All(f => f.SourceRoot == root), "lembra a pasta raiz");
        Assert.True(FileCollector.Collect([Path.Combine(root, "a-reduzido.jpg")], "-reduzido").Count == 1, "arquivo escolhido direto entra");
    }

    [Teste("pasta vigiada: arquivo novo entra uma vez só, depois de terminar de copiar")]
    public static async Task PastaVigiada()
    {
        var root = Env.NewOut("vigiada");
        var got = new List<string>();
        using var w = new FolderWatcher(root, true, p => { lock (got) got.Add(p); }, () => "-reduzido") { PollInterval = TimeSpan.FromMilliseconds(300) };
        w.Start();

        // Cópia "lenta": escreve em partes com o arquivo aberto
        var slow = Path.Combine(root, "lenta.jpg");
        using (var fs = new FileStream(slow, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            for (var i = 0; i < 5; i++)
            {
                fs.Write(new byte[50_000]);
                fs.Flush();
                await Task.Delay(300);
                lock (got) Assert.True(got.Count == 0, "não pega enquanto está sendo gravado");
            }
        }
        File.Copy(Env.F(Fixtures.Bmp), Path.Combine(root, "desenho.bmp"));
        File.Copy(Env.F(Fixtures.Bmp), Path.Combine(root, "desenho-reduzido.jpg"));
        File.WriteAllText(Path.Combine(root, "x.jpg.crdownload"), "baixando");
        File.Move(Path.Combine(root, "x.jpg.crdownload"), Path.Combine(root, "baixado.jpg"));
        Directory.CreateDirectory(Path.Combine(root, "Sub"));
        File.Copy(Env.F(Fixtures.Small), Path.Combine(root, "Sub", "sub.jpg"));
        await Task.Delay(2500);
        File.SetLastAccessTimeUtc(slow, DateTime.UtcNow); // eventos repetidos não duplicam
        await Task.Delay(1500);
        string[] names;
        lock (got) names = got.Select(Path.GetFileName).OrderBy(n => n).ToArray()!;
        Assert.Equal("baixado.jpg,desenho.bmp,lenta.jpg,sub.jpg", string.Join(",", names), "entrou cada um uma vez");
    }

    [Teste("onde está o peso: acha os pesados e estima a economia")]
    public static async Task OndeEstaOPeso()
    {
        var root = Env.NewOut("peso");
        File.Copy(Env.F(Fixtures.Photo), Path.Combine(root, "foto.jpg"));
        File.Copy(Env.F(Fixtures.Photo), Path.Combine(root, "foto-reduzido.jpg"));
        File.Copy(Env.F(Fixtures.Video), Path.Combine(root, "video.mp4"));
        File.Copy(Env.F(Fixtures.Wav), Path.Combine(root, "audio.wav"));
        File.Copy(Env.F(Fixtures.Scanned), Path.Combine(root, "doc.pdf"));
        File.Copy(Env.F(Fixtures.Small), Path.Combine(root, "pequena.jpg"));
        var sc = new WeightScanner(Env.Tools);
        var hits = await sc.ScanAsync(root, new CompressOptions(), 100 * 1024, null, default);
        var names = hits.Select(h => Path.GetFileName(h.Path)).OrderBy(n => n).ToArray();
        Assert.Equal("audio.wav,doc.pdf,foto.jpg,video.mp4", string.Join(",", names), "achou os 4 pesados (sem a saída nossa)");
        Assert.True(hits[0].SavingBytes >= hits[^1].SavingBytes, "ordenado por economia");
        foreach (var h in hits) Console.WriteLine($"      {Path.GetFileName(h.Path)}: {Format.Bytes(h.SizeBytes)} → ~{Format.Bytes(h.EstimatedBytes)} ({h.Detail})");

        // A estimativa precisa ficar na mesma ordem de grandeza do resultado real.
        var c = new Compressor(Env.NewContext());
        foreach (var h in hits)
        {
            var r = await c.RunAsync(h.Path, new CompressOptions(), new OutputSettings { Mode = OutputMode.Folder, Folder = Env.NewOut("peso-real") });
            var real = r.KeptOriginal ? r.OriginalBytes : r.FinalBytes;
            Console.WriteLine($"      real {Path.GetFileName(h.Path)}: {Format.Bytes(real)}");
            Assert.True(real < h.SizeBytes * 0.8, $"{Path.GetFileName(h.Path)} encolheu de verdade");
        }
    }

    [Teste("cabeçalhos: tamanho de JPG/PNG/WebP/BMP, duração de WAV, páginas de PDF")]
    public static async Task Cabecalhos()
    {
        var jpg = HeaderReader.ImageSize(Env.F(Fixtures.Photo));
        Assert.True(jpg is { Width: 4000, Height: 3000 }, $"jpg {jpg}");
        var png = HeaderReader.ImageSize(Env.F(Fixtures.Alpha));
        Assert.True(png is { Width: 1600, Height: 1000, HasAlpha: true }, $"png {png}");
        var bmp = HeaderReader.ImageSize(Env.F(Fixtures.Bmp));
        Assert.True(bmp is { Width: 800, Height: 600 }, $"bmp {bmp}");
        var webp = Path.Combine(Env.NewOut("cab"), "x.webp");
        await Env.Run(Env.Tools.MagickConvert, "-size", "321x123", "xc:red", webp);
        var wd = HeaderReader.ImageSize(webp);
        Assert.True(wd is { Width: 321, Height: 123 }, $"webp {wd}");
        Assert.Near(40, HeaderReader.AudioDuration(Env.F(Fixtures.Wav)) ?? 0, 0.1, "wav 40 s");
        Assert.Equal(3, HeaderReader.PdfPages(Env.F(Fixtures.Scanned)) ?? 0, "3 páginas");
        Assert.Equal(1, HeaderReader.CountPages("x /Type /Pages y /Type/Page z"u8, 30), "não conta /Pages");
    }

    [Teste("configurações: salvam, voltam e arquivo estragado não impede abrir")]
    public static void Configuracoes()
    {
        var dir = Env.NewOut("config");
        var path = Path.Combine(dir, "configuracoes.json");
        var store = new SettingsStore(path);
        var s = new AppSettings { PresetId = "whatsapp", CustomTargetMb = 7.5 };
        s.Options.Level = Level.Menor;
        s.Options.Rotate = 90; // ajuste por arquivo não pode virar padrão
        s.Profiles.Add(new SavedProfile { Name = "Fotos família", PresetId = Preset.Meta, TargetMb = 2, Options = new CompressOptions { Level = Level.Maxima } });
        store.Save(s);
        var back = store.Load();
        Assert.Equal("whatsapp", back.PresetId, "atalho");
        Assert.Equal(Level.Menor, back.Options.Level, "nível");
        Assert.Equal(0, back.Options.Rotate, "giro não fica salvo");
        var po = back.OptionsFor("Fotos família");
        Assert.Equal(2L * 1024 * 1024, po.TargetBytes ?? 0, "perfil: meta 2 MB");
        Assert.Equal(Level.Maxima, po.Level, "perfil: nível");
        Assert.Equal(16L * 1024 * 1024, back.OptionsFor("whatsapp").TargetBytes ?? 0, "WhatsApp 16 MB");
        Assert.True(back.OptionsFor(Preset.Converter).ConvertOnly, "Converter");
        File.WriteAllText(path, "{ isto não é json");
        var bad = store.Load();
        Assert.Equal(Preset.Qualidade, bad.PresetId, "padrão");
        Assert.True(File.Exists(path + ".corrompido"), "guardou cópia do estragado");
    }

    [Teste("histórico: guarda resultado e soma o total economizado")]
    public static void Historico()
    {
        var dir = Env.NewOut("historico");
        var h = new HistoryStore(dir);
        h.Add(new CompressResult { InputPath = "a.jpg", OriginalBytes = 1000, FinalBytes = 300, OutputPath = "a-r.jpg" }, FileKind.Image);
        h.Add(new CompressResult { InputPath = "b.jpg", OriginalBytes = 500, FinalBytes = 500, KeptOriginal = true }, FileKind.Image);
        var h2 = new HistoryStore(dir);
        Assert.Equal(2, h2.Entries.Count, "2 entradas");
        Assert.Equal(700L, h2.Totals.SavedBytes, "economizou 700");
        Assert.Equal("b.jpg", h2.Entries[0].Input, "mais recente primeiro");
    }

    [Teste("formatos: bytes, tempo e números em português")]
    public static void Formatos()
    {
        Assert.Equal("1,5 MB", Format.Bytes(1536 * 1024), "MB");
        Assert.Equal("120 KB", Format.Bytes(120 * 1024), "KB");
        Assert.True(Format.TryParseTime("1:05", out var t) && t == TimeSpan.FromSeconds(65), "1:05");
        Assert.True(Format.TryParseTime("01:00:02,5", out var t2) && t2 == TimeSpan.FromSeconds(3602.5), "hh:mm:ss,f");
        Assert.True(!Format.TryParseTime("abc", out _), "texto inválido");
        Assert.True(Format.TryParseNumber("2,5", out var n) && n == 2.5, "2,5");
        Assert.True(Format.TryParseNumber("2.5", out var n2) && n2 == 2.5, "2.5");
        Assert.Equal(70, Format.SavingPercent(1000, 300), "70%");
    }
}
