using System.Diagnostics;
using System.Text;
using ReduzFacil.Core.Maintenance;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

/// <summary>Atualiza programas pelo winget (o instalador oficial da Microsoft, que já vem no Windows 11).</summary>
public static class Winget
{
    public static string? FindExe()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* entrada inválida no PATH */ }
        }
        return null;
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("O winget não abriu.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* já fechou */ }
            throw new TimeoutException("O winget demorou demais para responder.");
        }
        var output = await outTask + await errTask;
        return (p.ExitCode, output);
    }

    public static async Task<List<AppUpgrade>> ListAsync(string exe, CancellationToken ct)
    {
        var (code, output) = await RunAsync(exe, "upgrade --accept-source-agreements --disable-interactivity", TimeSpan.FromMinutes(3), ct);
        var list = WingetParser.ParseUpgrades(output);
        if (list.Count == 0 && code != 0 && !WingetParser.IsNothingToDo(code))
            Log.Warn($"winget upgrade saiu com {code}: {Tail(output)}");
        return list;
    }

    /// <summary>Atualiza um programa. Devolve o código de saída do winget (0 = deu certo).</summary>
    public static async Task<int> UpgradeAsync(string exe, string id, CancellationToken ct)
    {
        var (code, output) = await RunAsync(exe,
            $"upgrade --id \"{id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            TimeSpan.FromMinutes(20), ct);
        Log.Info($"winget upgrade {id}: {code}");
        if (code != 0) Log.Warn(Tail(output));
        return code;
    }

    private static string Tail(string s) => s.Length > 600 ? s[^600..] : s;
}
