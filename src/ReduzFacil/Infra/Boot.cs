using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

/// <summary>Versão e metadados gravados no .exe durante o build.</summary>
public static class AppInfo
{
    private static readonly Assembly Asm = typeof(AppInfo).Assembly;

    public static string Version { get; } =
        Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Asm.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string EngineHash { get; } =
        Asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "EngineHash")?.Value
        ?? "sem-motor";

    public const string EngineResourceName = "ReduzFacil.engine.zip";
    public const string Name = "Reduz Fácil";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ReduzFacil.exe");
}

/// <summary>Caminhos dentro de ReduzFacil-dados.</summary>
public sealed record DataPaths(string ExeDir, string Root)
{
    public string EngineDir => Path.Combine(Root, "engine");
    public string LogsDir => Path.Combine(Root, "logs");
    public string WorkDir => Path.Combine(Root, "temp");
    public string SettingsFile => Path.Combine(Root, "configuracoes.json");
}

/// <summary>
/// Pasta ReduzFacil-dados, sempre ao lado do .exe, criada no primeiro uso.
/// Não há pasta reserva: se não der para gravar ali, o app avisa e fecha.
/// </summary>
public static class DataFolder
{
    public const string FolderName = "ReduzFacil-dados";

    public static bool TryPrepare(out DataPaths paths)
    {
        var exeDir = Path.GetDirectoryName(AppInfo.ExePath) ?? AppContext.BaseDirectory;
        var root = Path.Combine(exeDir, FolderName);
        paths = new DataPaths(exeDir, root);
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(paths.LogsDir);
            Directory.CreateDirectory(paths.WorkDir);
            var probe = Path.Combine(root, $".teste-gravacao-{Environment.ProcessId}.tmp");
            var token = Guid.NewGuid().ToString("N");
            File.WriteAllText(probe, token);
            var ok = File.ReadAllText(probe) == token;
            File.Delete(probe);
            return ok;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Restos de trabalhos interrompidos (app fechado no meio de um vídeo, queda de energia).</summary>
    public static void CleanWork(DataPaths data)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(data.WorkDir, "job-*"))
            {
                try { Directory.Delete(d, true); } catch { /* em uso por outra instância */ }
            }
        }
        catch { /* sem pasta */ }
    }
}

public sealed record EngineInfo(string Root, bool ExtractedNow, TimeSpan Elapsed)
{
    public string WpfDir => Path.Combine(Root, "wpf");
}

/// <summary>
/// Extrai os motores embutidos no .exe para ReduzFacil-dados\engine\ na primeira execução
/// e sempre que o .exe trouxer motores diferentes. Nunca mexe no resto da pasta de dados.
/// </summary>
public static class EngineInstaller
{
    private const string StampFile = "motor.id";

    private static readonly string[] Required =
    [
        Path.Combine("ffmpeg", "ffmpeg.exe"),
        Path.Combine("ffmpeg", "ffprobe.exe"),
        Path.Combine("magick", "magick.exe"),
        Path.Combine("gs", "gswin64c.exe"),
    ];

    public static EngineInfo Prepare(DataPaths data, Action<string>? status = null)
    {
        var sw = Stopwatch.StartNew();
        var engineDir = data.EngineDir;
        var expected = AppInfo.EngineHash;

        if (IsReady(engineDir, expected))
        {
            Log.Info($"Motor já preparado ({expected[..Math.Min(12, expected.Length)]})");
            return new EngineInfo(engineDir, false, sw.Elapsed);
        }

        Log.Info("Extraindo o motor para " + engineDir);
        status?.Invoke("Preparando o Reduz Fácil (só na primeira vez)…");
        var temp = engineDir + ".novo";
        var old = engineDir + ".antigo";
        TryDelete(temp);
        TryDelete(old);

        using (var stream = typeof(EngineInstaller).Assembly.GetManifestResourceStream(AppInfo.EngineResourceName)
                            ?? throw new InvalidOperationException("Os motores não estão dentro do .exe."))
        {
            ZipFile.ExtractToDirectory(stream, temp, overwriteFiles: true);
        }

        foreach (var r in Required)
            if (!File.Exists(Path.Combine(temp, r)))
                throw new InvalidOperationException($"O motor extraído está incompleto ({r} ausente).");

        File.WriteAllText(Path.Combine(temp, StampFile), expected);
        if (Directory.Exists(engineDir)) Directory.Move(engineDir, old);
        Directory.Move(temp, engineDir);
        TryDelete(old);

        sw.Stop();
        Log.Info($"Motor extraído em {sw.Elapsed.TotalSeconds:0.0} s");
        return new EngineInfo(engineDir, true, sw.Elapsed);
    }

    private static bool IsReady(string engineDir, string expected)
    {
        try
        {
            var stamp = Path.Combine(engineDir, StampFile);
            return File.Exists(stamp)
                   && File.ReadAllText(stamp).Trim() == expected
                   && Required.All(r => File.Exists(Path.Combine(engineDir, r)))
                   && Directory.Exists(Path.Combine(engineDir, "wpf"));
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Não foi possível apagar {dir}: {ex.Message}");
        }
    }
}
