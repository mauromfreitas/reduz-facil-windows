using ReduzFacil.Core.Maintenance;

namespace ReduzFacil.Tests;

public static class V130Tests
{
    [Teste("manutenção: lê a tabela do winget upgrade (português e inglês, com spinner)")]
    public static void Winget()
    {
        var pt = "   - \r   \\ \r\r" + """
        Nome                                   Id                          Versão        Disponível    Origem
        ---------------------------------------------------------------------------------------------------------
        Google Chrome                          Google.Chrome               130.0.1       131.0.2       winget
        Microsoft Visual C++ 2015-2022 Redist… Microsoft.VCRedist.2015+.x64 14.38.33130.0 14.42.34433.0 winget
        Discord                                Discord.Discord             < 1.0.9170    1.0.9175      winget
        3 atualizações disponíveis.
        1 pacote(s) têm números de versão que não podem ser determinados. Use --include-unknown para ver todos os resultados.
        """;
        var l = WingetParser.ParseUpgrades(pt);
        Assert.Equal(3, l.Count, "3 programas");
        Assert.True(l[0] is { Name: "Google Chrome", Id: "Google.Chrome", Current: "130.0.1", Available: "131.0.2", Source: "winget" }, "chrome");
        Assert.Equal("Microsoft.VCRedist.2015+.x64", l[1].Id, "id com +");
        Assert.Equal("< 1.0.9170", l[2].Current, "versão com <");

        var en = """
        Name     Id           Version Available Source
        ----------------------------------------------
        7-Zip    7zip.7zip    23.01   24.08     winget

        The following packages have an upgrade available, but require explicit targeting for upgrade:
        Name Id Version Available Source
        --------------------------------
        X    Y  1       2         winget
        """;
        var e = WingetParser.ParseUpgrades(en);
        Assert.True(e.Count == 1 && e[0].Id == "7zip.7zip", "só a primeira tabela");
        Assert.Equal(0, WingetParser.ParseUpgrades("Nenhuma atualização disponível.").Count, "nada");
        Assert.True(WingetParser.IsNothingToDo(unchecked((int)0x8A15002B)), "código de nada a fazer");
    }

    [Teste("manutenção: limpeza mede, respeita a idade e apaga só o que pode")]
    public static void Limpeza()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf-limpeza-" + Guid.NewGuid().ToString("N"));
        var sub = Directory.CreateDirectory(Path.Combine(root, "a", "b")).FullName;
        File.WriteAllBytes(Path.Combine(root, "velho.tmp"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(sub, "velho2.tmp"), new byte[500]);
        File.WriteAllBytes(Path.Combine(root, "novo.tmp"), new byte[300]);
        foreach (var f in new[] { Path.Combine(root, "velho.tmp"), Path.Combine(sub, "velho2.tmp") })
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-3));
        try
        {
            var roots = new[] { new CleanRoot(root) };
            var all = Cleaner.Measure(roots, TimeSpan.Zero);
            Assert.True(all is { Bytes: 1800, Files: 3 }, "mede tudo");
            var old = Cleaner.Measure(roots, TimeSpan.FromDays(1));
            Assert.True(old is { Bytes: 1500, Files: 2 }, "só os velhos");
            var r = Cleaner.Clean(roots, TimeSpan.FromDays(1));
            Assert.True(r is { Freed: 1500, Deleted: 2 }, "apagou os velhos");
            Assert.True(File.Exists(Path.Combine(root, "novo.tmp")), "o novo ficou");
            Assert.True(!Directory.Exists(Path.Combine(root, "a")), "pastas vazias saem");
            Assert.True(Directory.Exists(root), "a raiz fica");
            Assert.True(Cleaner.Measure([new CleanRoot(Path.Combine(root, "nao-existe"))], TimeSpan.Zero) is { Bytes: 0 }, "pasta que não existe");
            Assert.True(CleanCatalog.All.Select(t => t.Id).Distinct().Count() == CleanCatalog.All.Count, "ids únicos");
        }
        finally { Directory.Delete(root, true); }
    }
}
