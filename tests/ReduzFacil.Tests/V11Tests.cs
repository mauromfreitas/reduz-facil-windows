using ReduzFacil.Core.Compress;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Output;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Scan;
using ReduzFacil.Core.Settings;
using ReduzFacil.Core.Update;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Tests;

public static class V11Tests
{
    private const string ReleaseJson = """
    { "tag_name": "v1.2.0", "draft": false, "prerelease": false, "html_url": "https://github.com/x/y/releases/tag/v1.2.0", "body": "Novidades",
      "assets": [ { "name": "outra.zip", "size": 1, "browser_download_url": "https://x/outra.zip" },
                  { "name": "ReduzFacil-1.2.0.exe", "size": 1234, "digest": "sha256:ABCDEF", "browser_download_url": "https://x/ReduzFacil-1.2.0.exe" } ] }
    """;

    [Teste("atualização: compara versões e acha o .exe da release")]
    public static void Atualizacao()
    {
        Assert.True(UpdateChecker.Compare("1.1.0", "1.0.0") > 0, "1.1.0 > 1.0.0");
        Assert.True(UpdateChecker.Compare("1.10.0", "1.9.3") > 0, "1.10 > 1.9");
        Assert.True(UpdateChecker.Compare("1.1.0", "1.1.0-dev.5") > 0, "final > dev");
        Assert.Equal(0, UpdateChecker.Compare("1.1.0", "1.1.0"), "igual");
        var u = UpdateChecker.Parse(ReleaseJson, "1.1.0");
        Assert.True(u is { Version: "1.2.0", AssetName: "ReduzFacil-1.2.0.exe", Size: 1234, Sha256: "abcdef" }, "achou o .exe");
        Assert.True(UpdateChecker.Parse(ReleaseJson, "1.2.0") is null, "mesma versão: nada");
        Assert.True(UpdateChecker.Parse(ReleaseJson.Replace("\"prerelease\": false", "\"prerelease\": true"), "1.0.0") is null, "pré-versão não atualiza");
        // Conferência do download
        var dir = Env.NewOut("atualizacao");
        var f = Path.Combine(dir, "novo.exe");
        File.WriteAllBytes(f, [(byte)'M', (byte)'Z', 1, 2, 3]);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant();
        UpdateChecker.Verify(f, new UpdateInfo("9", "a", "u", 5, sha, "", ""));
        try { UpdateChecker.Verify(f, new UpdateInfo("9", "a", "u", 5, "00", "", "")); throw new Exception("Falhou: aceitou hash errado"); }
        catch (InvalidDataException) { }
        try { UpdateChecker.Verify(f, new UpdateInfo("9", "a", "u", 6, null, "", "")); throw new Exception("Falhou: aceitou tamanho errado"); }
        catch (InvalidDataException) { }
    }

    [Teste("repetidos: idênticos com nomes diferentes e fotos parecidas em grupos separados")]
    public static async Task Repetidos()
    {
        var root = Env.NewOut("repetidos");
        Directory.CreateDirectory(Path.Combine(root, "Backup"));
        File.Copy(Env.F(Fixtures.Video), Path.Combine(root, "video.mp4"));
        File.Copy(Env.F(Fixtures.Video), Path.Combine(root, "Backup", "copia do video.mp4"));
        // "Foto" com formas (como uma cena real), a mesma menor e recomprimida, e uma cena diferente
        await Env.Run(Env.Tools.MagickConvert, "-seed", "7", "-size", "3000x2000", "plasma:fractal", "-blur", "0x8", "-quality", "95", Path.Combine(root, "foto.jpg"));
        await Env.Run(Env.Tools.MagickConvert, Path.Combine(root, "foto.jpg"), "-resize", "1500x1000", "-quality", "70", Path.Combine(root, "foto pequena.jpg"));
        await Env.Run(Env.Tools.MagickConvert, "-seed", "99", "-size", "3000x2000", "plasma:fractal", "-blur", "0x8", "-quality", "95", Path.Combine(root, "outra.jpg"));
        // Duas fotos lisas diferentes (céu): pouca informação, não podem virar "parecidas"
        await Env.Run(Env.Tools.MagickConvert, "-size", "1000x800", "xc:#7ab", "-attenuate", "0.1", "+noise", "Gaussian", "-quality", "90", Path.Combine(root, "ceu1.jpg"));
        await Env.Run(Env.Tools.MagickConvert, "-size", "1000x800", "xc:#69a", "-attenuate", "0.1", "+noise", "Gaussian", "-quality", "90", Path.Combine(root, "ceu2.jpg"));
        File.Copy(Env.F(Fixtures.Scanned), Path.Combine(root, "doc.pdf"));

        var groups = await new DuplicateFinder(Env.Tools).FindAsync(root, true, null, default);
        foreach (var g in groups) Console.WriteLine($"      {(g.Exact ? "idênticos" : "parecidas")}: {string.Join(" | ", g.Files.Select(f => (f.Keep ? "[manter] " : "") + Path.GetFileName(f.Path)))}");
        var exact = groups.Where(g => g.Exact).ToList();
        var similar = groups.Where(g => !g.Exact).ToList();
        Assert.Equal(1, exact.Count, "1 grupo de idênticos");
        Assert.Equal(2, exact[0].Files.Count, "os 2 vídeos");
        Assert.Equal(1, exact[0].Files.Count(f => f.Keep), "mantém 1");
        Assert.Equal(1, similar.Count, "1 grupo de parecidas");
        Assert.Equal("foto pequena.jpg,foto.jpg", string.Join(",", similar[0].Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n)), "as duas fotos");
        Assert.True(similar[0].Files.Single(f => f.Keep).Path.EndsWith("foto.jpg"), "mantém a maior");
    }

    [Teste("dividir vídeo: partes dentro da meta, na ordem, somando a duração")]
    public static async Task DividirVideo()
    {
        var dir = Env.NewOut("dividir");
        var c = new Compressor(Env.NewContext());
        const long part = 1024 * 1024;
        var r = await c.RunAsync(Env.F(Fixtures.Video), new CompressOptions { SplitPartBytes = part }, new OutputSettings { Mode = OutputMode.Folder, Folder = dir });
        var outs = r.AllOutputs.ToList();
        Console.WriteLine($"      {outs.Count} partes: {string.Join(", ", outs.Select(o => $"{Path.GetFileName(o)} {Format.Bytes(new FileInfo(o).Length)}"))} · {r.EngineNote}");
        Assert.True(outs.Count >= 2, "dividiu");
        Assert.True(outs.All(o => new FileInfo(o).Length <= part), "todas cabem em 1 MB");
        Assert.True(outs.Select(Path.GetFileName).SequenceEqual(Enumerable.Range(1, outs.Count).Select(i => $"vídeo festa ação-reduzido-parte{i}.mp4")), "nomes em ordem");
        double sum = 0;
        foreach (var o in outs) sum += (await Env.Probe(o)).DurationSec;
        Assert.Near(12, sum, 0.6, "soma das durações");
        Assert.True(FileCollector.IsOwnOutput(outs[1], "-reduzido"), "pasta vigiada reconhece as partes como saída nossa");
    }

    [Teste("pdf: juntar fotos (A4 e tamanho da foto) e separar páginas")]
    public static async Task FerramentasPdf()
    {
        var dir = Env.NewOut("pdf-ferramentas");
        var t = new PdfTools(Env.Tools);
        var photos = new[] { Env.F(Fixtures.Photo), Env.F(Fixtures.Alpha), Env.F(Fixtures.Bmp) };
        var a4 = await t.ImagesToPdfAsync(photos, Path.Combine(dir, "álbum A4.pdf"), true, Level.Equilibrado, null, default);
        Assert.Equal(3, await t.PageCountAsync(a4, default), "3 páginas A4");
        var size = await Env.Run(Env.Tools.Ghostscript, "-q", "-dNODISPLAY", "-dNOSAFER", "-c",
            $"({a4.Replace("\\", "/")}) (r) file runpdfbegin 1 pdfgetpage /MediaBox pget pop == quit");
        Console.WriteLine("      A4 página 1 (deitada): " + size.Trim());
        Assert.True(size.Contains("841") && size.Contains("595"), "A4 deitado (842×595 pt) para foto deitada");
        Assert.True(new FileInfo(a4).Length < 2 * 1024 * 1024, "PDF leve");
        var again = await t.ImagesToPdfAsync(photos, a4, false, Level.Menor, null, default);
        Assert.True(again.EndsWith("álbum A4 (2).pdf"), "não sobrescreve: " + Path.GetFileName(again));

        var ranges = PdfTools.ParseRanges("1-2, 3", 3);
        var parts = await t.SplitAsync(Env.F(Fixtures.Scanned), ranges, dir, null, default);
        Assert.Equal("documento escaneado-paginas-1-2.pdf,documento escaneado-pagina-3.pdf", string.Join(",", parts.Select(Path.GetFileName)), "nomes");
        Assert.Equal(2, await t.PageCountAsync(parts[0], default), "2 páginas na primeira");
        var each = await t.SplitAsync(Env.F(Fixtures.Scanned), null, Path.Combine(dir, "uma-por-arquivo"), null, default);
        Assert.Equal(3, each.Count, "uma por página");
        await Assert.ThrowsAsync<UserFacingException>(() => Task.FromResult(PdfTools.ParseRanges("1-9", 3)), "passa do total");
        Assert.Equal(8, PdfTools.ParseRanges("8-", 12)[0].From, "8-");
        Assert.Equal(12, PdfTools.ParseRanges("8-", 12)[0].To ?? 0, "8- até o fim");
    }

    [Teste("configurações: migração da pasta vigiada da 1.0 para a lista")]
    public static void MigracaoPastas()
    {
        var dir = Env.NewOut("migracao");
        var path = Path.Combine(dir, "configuracoes.json");
        File.WriteAllText(path, """{ "Watch": { "Enabled": true, "Folder": "C:\\Downloads", "IncludeSubfolders": false, "Profile": "whatsapp" } }""");
        var s = new SettingsStore(path).Load();
        Assert.Equal(1, s.Watches.Count, "1 pasta");
        Assert.True(s.Watches[0] is { Enabled: true, Folder: "C:\\Downloads", IncludeSubfolders: false, Profile: "whatsapp" }, "mesmos dados");
        Assert.True(s.Watch is null, "formato antigo some");
        Assert.Equal("sistema", s.Theme, "tema padrão");
    }
}

public static class V121Tests
{
    [Teste("mapa do disco: tamanhos por pasta e tipo; blocos cobrem a área sem sobrar")]
    public static void MapaDoDisco()
    {
        var root = Env.NewOut("mapa");
        Directory.CreateDirectory(Path.Combine(root, "Videos"));
        Directory.CreateDirectory(Path.Combine(root, "Fotos", "2026"));
        File.Copy(Env.F(Fixtures.Video), Path.Combine(root, "Videos", "a.mp4"));
        File.Copy(Env.F(Fixtures.Photo), Path.Combine(root, "Fotos", "2026", "b.jpg"));
        File.WriteAllBytes(Path.Combine(root, "notas.txt"), new byte[1000]);
        var map = DiskMapper.Build(root, null, default);
        var expected = new FileInfo(Env.F(Fixtures.Video)).Length + new FileInfo(Env.F(Fixtures.Photo)).Length + 1000;
        Assert.Equal(expected, map.Size, "soma de tudo");
        Assert.Equal("Videos", map.Children[0].Name, "maior primeiro");
        Assert.Equal(MapKind.Videos, map.Children[0].Dominant, "cor de vídeo");
        Assert.Equal(MapKind.Fotos, map.Children[1].Dominant, "cor de foto");
        Assert.True(map.Children.Any(c => !c.IsFolder && c.Size == 1000), "arquivos soltos agrupados");

        var sizes = new double[] { 50, 25, 12, 8, 3, 1, 1 };
        var rects = Treemap.Layout(sizes, s => s, new MapRect(0, 0, 400, 300));
        Assert.Equal(sizes.Length, rects.Count, "um bloco por item");
        Assert.Near(400 * 300, rects.Sum(r => r.Rect.W * r.Rect.H), 0.5, "cobre a área toda");
        foreach (var (s, r) in rects)
        {
            Assert.Near(s / 100.0 * 120000, r.W * r.H, 0.5, "área proporcional");
            Assert.True(r.X >= -0.01 && r.Y >= -0.01 && r.X + r.W <= 400.01 && r.Y + r.H <= 300.01, "dentro da área");
        }
    }
}
