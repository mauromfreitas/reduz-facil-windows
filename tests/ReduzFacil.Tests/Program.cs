using ReduzFacil.Core.Util;

namespace ReduzFacil.Tests;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        Env.EngineDir = Arg("--engine");
        Env.Root = Arg("--pasta") ?? Path.Combine(Path.GetTempPath(), "reduzfacil-testes");
        if (Directory.Exists(Env.Root)) Directory.Delete(Env.Root, true);
        Directory.CreateDirectory(Env.Root);
        Log.Init(Path.Combine(Env.Root, "logs"));
        if (args.Contains("--verbose")) Log.Echo = Console.WriteLine;

        Console.WriteLine($"Motores: {(Env.EngineDir ?? "do sistema")} · pasta de teste: {Env.Root}");
        await Fixtures.CreateAsync();
        return await Runner.RunAllAsync(Arg("--filtro"), Path.Combine(Env.Root, "resultado.txt"));
    }
}
