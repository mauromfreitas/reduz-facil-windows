using System.Runtime.InteropServices;
using System.Runtime.Loader;

using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

/// <summary>
/// Faz o WPF encontrar as próprias DLLs nativas (*_cor3.dll) em ReduzFacil-dados\engine\wpf
/// em vez de ao lado do .exe ou na pasta temporária do Windows.
/// </summary>
public static class NativeLoader
{
    // Ordem importa: o runtime C++ primeiro, depois quem depende dele.
    private static readonly string[] PreloadOrder =
    [
        "vcruntime140_cor3.dll",
        "D3DCompiler_47_cor3.dll",
        "PresentationNative_cor3.dll",
        "wpfgfx_cor3.dll",
    ];

    public static void Setup(string wpfDir)
    {
        if (!Directory.Exists(wpfDir))
            throw new DirectoryNotFoundException(wpfDir);

        // Caminhos de busca para cargas por nome (LoadLibrary comum e com LOAD_LIBRARY_SEARCH_*).
        NativeMethods.SetDllDirectory(wpfDir);
        NativeMethods.AddDllDirectory(wpfDir);

        // Reserva para DllImport que não encontrar a DLL pelos caminhos padrão.
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            var file = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
            var path = Path.Combine(wpfDir, file);
            return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
        };

        // Pré-carrega: depois disso, qualquer pedido pelo nome reaproveita o módulo já carregado.
        foreach (var file in PreloadOrder)
        {
            var path = Path.Combine(wpfDir, file);
            if (File.Exists(path))
                NativeLibrary.Load(path);
            else
                Log.Warn($"DLL nativa do WPF ausente no motor: {file}");
        }

        Log.Info("DLLs nativas do WPF carregadas de " + wpfDir);
    }
}
