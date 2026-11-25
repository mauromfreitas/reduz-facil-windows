using System.Diagnostics;
using System.Net.Http;
using ReduzFacil.Core.Update;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

/// <summary>
/// Atualização com um clique: baixa o .exe novo ao lado do atual (ReduzFacil.exe.novo), troca os arquivos
/// (o que está aberto vira .antigo — o Windows deixa renomear um .exe em uso) e abre a versão nova.
/// A pasta ReduzFacil-dados não é tocada: configurações e histórico continuam.
/// </summary>
public sealed class AppUpdater : IDisposable
{
    private readonly HttpClient _http = UpdateChecker.CreateHttp(AppInfo.Version);
    private readonly UpdateChecker _checker;

    public AppUpdater() => _checker = new UpdateChecker(_http);

    public UpdateInfo? Available { get; private set; }

    public static string NewPath => AppInfo.ExePath + ".novo";
    public static string OldPath => AppInfo.ExePath + ".antigo";

    /// <summary>
    /// Nome do .exe depois da atualização: "ReduzFacil-1.1.2.exe" (nome do download antigo) vira "ReduzFacil.exe", uma vez.
    /// Qualquer outro nome que o usuário escolheu é mantido.
    /// </summary>
    public static string TargetPath(string exe)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var name = Path.GetFileName(exe);
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^ReduzFacil-\d+\.\d+\.\d+.*\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var clean = Path.Combine(dir, "ReduzFacil.exe");
            if (!File.Exists(clean)) return clean;
        }
        return exe;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            Available = await _checker.CheckAsync(AppInfo.Version, ct);
            Log.Info(Available is null ? $"Atualização: {AppInfo.Version} já é a mais nova" : $"Atualização disponível: {Available.Version}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn("Não consegui procurar atualização: " + ex.Message);
            Available = null;
        }
        return Available;
    }

    public Task DownloadAsync(IProgress<double> progress, CancellationToken ct = default) =>
        _checker.DownloadAsync(Available ?? throw new InvalidOperationException("Nenhuma atualização."), NewPath, progress, ct);

    /// <summary>Troca o .exe pelo baixado (ou por <paramref name="newExe"/>) e abre a versão nova. Quem chama fecha o app em seguida.</summary>
    public static void SwapAndRestart(string? newExe = null, IEnumerable<string>? extraArgs = null)
    {
        var exe = AppInfo.ExePath;
        var target = TargetPath(exe);
        var source = newExe ?? NewPath;
        if (!File.Exists(source)) throw new FileNotFoundException("O arquivo da versão nova sumiu.", source);
        TryDelete(OldPath);
        File.Move(exe, OldPath);
        try
        {
            if (newExe is null) File.Move(source, target);
            else File.Copy(source, target);
        }
        catch
        {
            File.Move(OldPath, exe); // volta como estava
            throw;
        }
        if (!string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) Log.Info($"Arquivo renomeado: {Path.GetFileName(exe)} → {Path.GetFileName(target)}");
        var psi = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        psi.ArgumentList.Add("--atualizado");
        psi.ArgumentList.Add(AppInfo.Version);
        psi.ArgumentList.Add("--esperar-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        foreach (var a in extraArgs ?? []) psi.ArgumentList.Add(a);
        Process.Start(psi);
        Log.Info("Atualização instalada; abrindo a versão nova");
    }

    /// <summary>Na abertura: apaga o .exe antigo e restos de download.</summary>
    public static void CleanupLeftovers()
    {
        // O antigo pode ter outro nome (ex.: ReduzFacil-1.1.2.exe.antigo, quando o arquivo foi renomeado na atualização).
        var dir = Path.GetDirectoryName(AppInfo.ExePath)!;
        string[] olds;
        try { olds = Directory.GetFiles(dir, "ReduzFacil*.exe.antigo"); } catch { olds = []; }
        foreach (var old in olds)
        {
            for (var i = 0; i < 10 && File.Exists(old); i++)
            {
                TryDelete(old);
                if (File.Exists(old)) Thread.Sleep(300);
            }
        }
        TryDelete(NewPath + ".baixando");
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* em uso */ }
    }

    public void Dispose() => _http.Dispose();
}
