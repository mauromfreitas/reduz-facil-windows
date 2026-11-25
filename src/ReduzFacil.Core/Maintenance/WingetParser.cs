using System.Text.RegularExpressions;

namespace ReduzFacil.Core.Maintenance;

/// <summary>Um programa com versão nova disponível (saída do "winget upgrade").</summary>
public sealed record AppUpgrade(string Name, string Id, string Current, string Available, string Source);

/// <summary>
/// Lê a tabela do "winget upgrade". Os títulos vêm no idioma do Windows, então as colunas
/// são achadas pela posição do cabeçalho (linha logo acima da linha de traços).
/// </summary>
public static partial class WingetParser
{
    [GeneratedRegex(@"\S+")]
    private static partial Regex Word();

    public static List<AppUpgrade> ParseUpgrades(string output)
    {
        var list = new List<AppUpgrade>();
        // o winget desenha um "spinner" com \r: fica só o que sobrou por último em cada linha
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => { var i = l.LastIndexOf('\r'); return (i >= 0 ? l[(i + 1)..] : l).TrimEnd(); })
            .ToList();

        var dash = lines.FindIndex(l => l.Length >= 10 && l.All(c => c == '-'));
        if (dash < 1) return list;
        var header = lines[dash - 1];
        var starts = Word().Matches(header).Select(m => m.Index).ToList();
        if (starts.Count < 4) return list;

        for (var r = dash + 1; r < lines.Count; r++)
        {
            var row = lines[r];
            if (string.IsNullOrWhiteSpace(row) || row.Length <= starts[3]) break;
            string Col(int k)
            {
                var a = starts[k];
                var b = k + 1 < starts.Count ? starts[k + 1] : row.Length;
                if (a >= row.Length) return "";
                return row[a..Math.Min(b, row.Length)].Trim();
            }
            var id = Col(1);
            var current = Col(2);
            var available = Col(3);
            // fim da tabela: frase de resumo ("3 atualizações disponíveis.") ou linha fora do formato
            if (id.Length == 0 || id.Any(char.IsWhiteSpace) || current.Length == 0 || available.Length == 0) break;
            if (list.Any(x => x.Id == id)) continue;
            list.Add(new AppUpgrade(Col(0), id, current, available, starts.Count > 4 ? Col(4) : ""));
        }
        return list;
    }

    /// <summary>Código de saída do winget que quer dizer "não havia o que atualizar".</summary>
    public static bool IsNothingToDo(int exitCode) => unchecked((uint)exitCode) is 0x8A15002B;
}
