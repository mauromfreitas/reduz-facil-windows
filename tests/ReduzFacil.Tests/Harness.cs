using System.Diagnostics;
using System.Reflection;

namespace ReduzFacil.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TesteAttribute(string nome) : Attribute
{
    public string Nome { get; } = nome;
}

public sealed class SkipException(string reason) : Exception(reason);

public static class Assert
{
    public static void True(bool cond, string msg)
    {
        if (!cond) throw new Exception("Falhou: " + msg);
    }

    public static void Equal<T>(T expected, T actual, string msg)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Falhou: {msg} (esperado {expected}, veio {actual})");
    }

    public static void Near(double expected, double actual, double tol, string msg)
    {
        if (Math.Abs(expected - actual) > tol)
            throw new Exception($"Falhou: {msg} (esperado {expected}±{tol}, veio {actual})");
    }

    public static async Task<TEx> ThrowsAsync<TEx>(Func<Task> act, string msg) where TEx : Exception
    {
        try
        {
            await act();
        }
        catch (TEx ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new Exception($"Falhou: {msg} (esperado {typeof(TEx).Name}, veio {ex.GetType().Name}: {ex.Message})");
        }
        throw new Exception($"Falhou: {msg} (nenhum erro)");
    }

    public static void Skip(string reason) => throw new SkipException(reason);
}

public static class Runner
{
    public static async Task<int> RunAllAsync(string? filter, string reportPath)
    {
        var methods = typeof(Runner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (m, a: m.GetCustomAttribute<TesteAttribute>()))
            .Where(x => x.a is not null)
            .Where(x => filter is null || x.a!.Nome.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.a!.Nome, StringComparer.Ordinal)
            .ToList();

        int ok = 0, fail = 0, skip = 0;
        var lines = new List<string>();
        foreach (var (m, a) in methods)
        {
            var sw = Stopwatch.StartNew();
            string status;
            try
            {
                var r = m.Invoke(null, null);
                if (r is Task t) await t;
                status = "OK";
                ok++;
            }
            catch (Exception ex)
            {
                var e = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
                if (e is SkipException s)
                {
                    status = "PULADO: " + s.Message;
                    skip++;
                }
                else
                {
                    status = "FALHOU: " + e.Message + (e is not Exception { Message: var msg } || !msg.StartsWith("Falhou") ? "\n" + e : "");
                    fail++;
                }
            }
            var line = $"[{sw.Elapsed.TotalSeconds,6:0.0}s] {a!.Nome}: {status}";
            Console.WriteLine(line);
            lines.Add(line);
        }
        var summary = $"Resultado: {ok} ok, {fail} falharam, {skip} pulados";
        Console.WriteLine(summary);
        lines.Add(summary);
        try { File.WriteAllLines(reportPath, lines); } catch { /* sem relatório */ }
        return fail == 0 && ok > 0 ? 0 : 1;
    }
}
