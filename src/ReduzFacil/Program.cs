using System.Runtime.CompilerServices;
using ReduzFacil.Core.Util;
using ReduzFacil.Infra;

namespace ReduzFacil;

/// <summary>
/// Ponto de entrada. Prepara tudo o que precisa existir ANTES do WPF carregar:
/// instância única, pasta ReduzFacil-dados e motores (+ DLLs nativas do WPF).
/// Nada aqui pode tocar em tipos do WPF: eles só são carregados em <see cref="RunApp"/>.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var smoke = SmokeOptions.Parse(args);

        // Aberto pela versão anterior depois de uma atualização: espera ela fechar de vez.
        var waitIdx = Array.FindIndex(args, a => a.Equals("--esperar-pid", StringComparison.OrdinalIgnoreCase));
        if (waitIdx >= 0 && waitIdx + 1 < args.Length && int.TryParse(args[waitIdx + 1], out var pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(30_000); } catch { /* já fechou */ }
        }

        // 1. Uma instância por vez: as próximas aberturas mandam os arquivos para a que está aberta.
        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            if (SingleInstance.SendToExisting(args)) return 0;
            // A outra instância não respondeu (travada ou fechando): avisa em vez de abrir duas.
            NativeMethods.ShowMessage(AppInfo.Name, "O Reduz Fácil já está aberto, mas não respondeu. Feche-o e abra de novo.", error: true);
            return 4;
        }

        using (instance)
        {
            // 2. Pasta de dados ao lado do .exe. Sem plano B: se não der para gravar, avisa e fecha.
            if (!DataFolder.TryPrepare(out var data))
            {
                NativeMethods.ShowMessage(AppInfo.Name,
                    "Não é possível usar o Reduz Fácil nesta pasta.\n\n" +
                    $"O app guarda as configurações e os motores na pasta {DataFolder.FolderName}, ao lado do arquivo, " +
                    $"e o Windows não permite gravar em:\n\n{data.ExeDir}\n\n" +
                    "Mova o ReduzFacil.exe para uma pasta onde você possa salvar arquivos " +
                    "(por exemplo, Documentos, Área de Trabalho ou um pen drive) e abra de novo.",
                    error: true);
                return 1;
            }

            Log.Init(data.LogsDir);
            Log.Info($"Iniciando Reduz Fácil v{AppInfo.Version} em {data.ExeDir}");
            DataFolder.CleanWork(data);
            AppUpdater.CleanupLeftovers();

            // 3. Motores e DLLs nativas do WPF em ReduzFacil-dados\engine\.
            EngineInfo engine;
            try
            {
                engine = EngineInstaller.Prepare(data);
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao preparar os motores", ex);
                NativeMethods.ShowMessage(AppInfo.Name,
                    "Não foi possível preparar o Reduz Fácil para abrir.\n\n" +
                    $"Os motores de compressão não puderam ser copiados para {DataFolder.FolderName}\\engine. " +
                    "Confira se há espaço livre (cerca de 500 MB) e se o antivírus não bloqueou o app, e abra de novo.\n\n" +
                    $"Detalhe: {ex.Message}",
                    error: true);
                return 2;
            }

            try
            {
                NativeLoader.Setup(engine.WpfDir);
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao carregar as bibliotecas nativas do WPF", ex);
                NativeMethods.ShowMessage(AppInfo.Name,
                    "Não foi possível carregar os componentes de tela do Reduz Fácil.\n\n" +
                    $"Apague a pasta {DataFolder.FolderName}\\engine (suas configurações continuam salvas) e abra de novo.\n\n" +
                    $"Detalhe: {ex.Message}",
                    error: true);
                return 3;
            }

            return RunApp(data, engine, instance, args, smoke);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(DataPaths data, EngineInfo engine, SingleInstance instance, string[] args, SmokeOptions? smoke)
    {
        var app = new App(data, engine, args, smoke);
        app.InitializeComponent();
        instance.StartListening(a => app.OnExternalArgs(a));
        return app.Run();
    }
}

/// <summary>
/// Teste automático do build (GitHub Actions): --teste-auto PASTA_AMOSTRAS --capturas PASTA.
/// O app abre, reduz as amostras, passa por todas as telas tirando capturas e fecha.
/// --teste-espera N: abre normalmente e fecha sozinho depois de N segundos sem trabalho (teste da 2ª abertura).
/// --teste-atualizar ARQUIVO: instala ARQUIVO como se fosse uma versão nova e reabre (teste da atualização).
/// </summary>
public sealed record SmokeOptions(string? Samples, string? ShotsDir, int? WaitSeconds, string? UpdateFrom = null)
{
    public static SmokeOptions? Parse(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var samples = Arg("--teste-auto");
        var wait = int.TryParse(Arg("--teste-espera"), out var w) ? w : (int?)null;
        var update = Arg("--teste-atualizar");
        if (samples is null && wait is null && update is null) return null;
        return new SmokeOptions(samples, Arg("--capturas"), wait, update);
    }
}
