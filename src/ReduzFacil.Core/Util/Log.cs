using System.Text;

namespace ReduzFacil.Core.Util;

/// <summary>Registro simples em ReduzFacil-dados\logs\app.log (máx. ~2 MB, guarda o anterior).</summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _file;

    public static string? FilePath => _file;

    /// <summary>Linhas também vão para cá (testes usam para mostrar no console).</summary>
    public static Action<string>? Echo { get; set; }

    public static void Init(string logsDir)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            _file = Path.Combine(logsDir, "app.log");
            var info = new FileInfo(_file);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Copy(_file, Path.Combine(logsDir, "app.anterior.log"), overwrite: true);
                File.Delete(_file);
            }
        }
        catch
        {
            _file = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("AVISO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERRO", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        try { Echo?.Invoke(line); } catch { /* nunca derruba */ }
        if (_file is null) return;
        try
        {
            lock (Gate) File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Registro nunca pode derrubar o app.
        }
    }
}
