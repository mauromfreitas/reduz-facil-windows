using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using ReduzFacil.Core.Maintenance;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

public sealed record StartupChange(StartupSource Source, string Name, bool Enable);

/// <summary>O que o ajudante com permissão de administrador deve fazer.</summary>
public sealed class AdminJob
{
    public List<string> Drivers { get; set; } = [];
    public List<string> Clean { get; set; } = [];
    public List<StartupChange> Startup { get; set; } = [];
}

public sealed class AdminResult
{
    public Dictionary<string, int> DriverCodes { get; set; } = [];
    public bool Reboot { get; set; }
    public long Freed { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Tarefas que precisam de administrador. O app abre a si mesmo com "--admin ARQUIVO" pedindo permissão
/// ao Windows (a janela do Sim/Não), o ajudante faz só o que está na lista e fecha.
/// Só aceita itens conhecidos (ids da Limpeza, GUIDs do Windows Update, chaves de inicialização).
/// </summary>
public static class AdminTasks
{
    public static bool IsElevated
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    /// <summary>Lado do ajudante (roda antes do WPF, sem tela).</summary>
    public static int RunHelper(string jobPath)
    {
        var result = new AdminResult();
        try
        {
            var job = JsonSerializer.Deserialize<AdminJob>(File.ReadAllText(jobPath)) ?? new AdminJob();
            Execute(job, result);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        try { File.WriteAllText(jobPath + ".resultado", JsonSerializer.Serialize(result)); } catch { return 1; }
        return 0;
    }

    private static void Execute(AdminJob job, AdminResult result)
    {
        var errors = new List<string>();
        foreach (var c in job.Startup)
        {
            try { StartupApps.SetEnabled(c.Source, c.Name, c.Enable); }
            catch (Exception ex) { errors.Add($"{c.Name}: {ex.Message}"); }
        }
        foreach (var id in job.Clean)
        {
            if (CleanCatalog.Find(id) is not { } t) continue;
            try
            {
                var r = Cleaner.Clean(t);
                result.Freed += r.Freed;
                result.Deleted += r.Deleted;
                result.Skipped += r.Skipped;
            }
            catch (Exception ex) { errors.Add($"{t.Title}: {ex.Message}"); }
        }
        if (job.Drivers.Count > 0)
        {
            try
            {
                result.DriverCodes = WindowsUpdateDrivers.Install(job.Drivers, out var reboot);
                result.Reboot = reboot;
            }
            catch (Exception ex) { errors.Add("Drivers: " + ex.Message); }
        }
        if (errors.Count > 0) result.Error = string.Join("\n", errors);
    }

    /// <summary>
    /// Lado do app: roda o trabalho com permissão de administrador.
    /// Devolve null se a pessoa clicou em "Não" na janela do Windows.
    /// </summary>
    public static async Task<AdminResult?> RunAsync(AdminJob job)
    {
        if (IsElevated)
        {
            var direct = new AdminResult();
            await Task.Run(() => Execute(job, direct));
            return direct;
        }

        var dir = Path.Combine(Path.GetTempPath(), "ReduzFacil-admin");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(job));
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Não achei o ReduzFacil.exe.");
            var psi = new ProcessStartInfo(exe, $"--admin \"{path}\"") { UseShellExecute = true, Verb = "runas" };
            Process? p;
            try { p = Process.Start(psi); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return null; // clicou em "Não"
            }
            if (p is null) return new AdminResult { Error = "O Windows não abriu o ajudante." };
            using (p) await p.WaitForExitAsync();
            var resultPath = path + ".resultado";
            if (!File.Exists(resultPath)) return new AdminResult { Error = "O ajudante fechou sem responder." };
            return JsonSerializer.Deserialize<AdminResult>(await File.ReadAllTextAsync(resultPath)) ?? new AdminResult();
        }
        catch (Exception ex)
        {
            Log.Warn("Ajudante de administrador: " + ex.Message);
            return new AdminResult { Error = ex.Message };
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".resultado"); } catch { }
        }
    }
}
