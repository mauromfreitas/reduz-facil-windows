using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Update;

public sealed record UpdateInfo(string Version, string AssetName, string DownloadUrl, long Size, string? Sha256, string Notes, string PageUrl);

/// <summary>
/// Procura versão nova nas Releases do GitHub (repositório público) e baixa o .exe conferindo tamanho e SHA-256.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string repo = UpdateChecker.DefaultRepo)
{
    public const string DefaultRepo = "mauromfreitas/reduz-facil-windows";

    public static HttpClient CreateHttp(string currentVersion)
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ReduzFacil", currentVersion.Split('-')[0]));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    /// <summary>Null quando não há versão mais nova (ou sem internet).</summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", cts.Token).ConfigureAwait(false);
        return Parse(json, currentVersion);
    }

    /// <summary>Lê a resposta do GitHub e decide se é mais nova. Função pura (testada).</summary>
    public static UpdateInfo? Parse(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (Compare(version, currentVersion) <= 0) return null;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("ReduzFacil", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = null;
            if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String && dg.GetString() is { } dv && dv.StartsWith("sha256:"))
                sha = dv[7..].ToLowerInvariant();
            return new UpdateInfo(version, name, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha,
                r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
        }
        return null;
    }

    /// <summary>Compara versões X.Y.Z[-sufixo]. Sem sufixo é mais nova que com sufixo (1.1.0 &gt; 1.1.0-beta).</summary>
    public static int Compare(string a, string b)
    {
        static (Version v, string suf) Split(string s)
        {
            var parts = s.Trim().Split('-', 2);
            var v = Version.TryParse(parts[0], out var x) ? x : new Version(0, 0);
            return (new Version(v.Major, v.Minor, Math.Max(0, v.Build)), parts.Length > 1 ? parts[1] : "");
        }
        var (va, sa) = Split(a);
        var (vb, sb) = Split(b);
        var c = va.CompareTo(vb);
        if (c != 0) return c;
        if (sa == sb) return 0;
        if (sa == "") return 1;
        if (sb == "") return -1;
        return string.CompareOrdinal(sa, sb);
    }

    /// <summary>Baixa para <paramref name="destination"/> e confere. Em erro, apaga o arquivo e lança.</summary>
    public async Task DownloadAsync(UpdateInfo info, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        var tmp = destination + ".baixando";
        try
        {
            using (var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    total += n;
                    if (info.Size > 0) progress?.Report(Math.Min(1, (double)total / info.Size));
                }
            }
            Verify(tmp, info);
            File.Move(tmp, destination, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* sem arquivo */ }
            throw;
        }
    }

    public static void Verify(string file, UpdateInfo info)
    {
        var size = new FileInfo(file).Length;
        if (info.Size > 0 && size != info.Size)
            throw new InvalidDataException($"O download veio incompleto ({Format.Bytes(size)} de {Format.Bytes(info.Size)}).");
        if (info.Sha256 is { } expected)
        {
            using var fs = File.OpenRead(file);
            var got = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (got != expected) throw new InvalidDataException("O arquivo baixado não confere (assinatura SHA-256 diferente).");
        }
        // Um .exe de verdade começa com "MZ".
        using var f = File.OpenRead(file);
        if (f.ReadByte() != 'M' || f.ReadByte() != 'Z') throw new InvalidDataException("O arquivo baixado não é um programa do Windows.");
    }
}
