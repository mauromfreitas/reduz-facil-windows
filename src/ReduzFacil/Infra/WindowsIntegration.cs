using Microsoft.Win32;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

/// <summary>
/// Botão direito do Explorer ("Reduzir com Reduz Fácil" com os atalhos) e "Iniciar com o Windows".
/// Tudo fica no registro do usuário (HKCU): não pede administrador e some quando desliga.
/// No Windows 11 o menu aparece em "Mostrar mais opções" (menu clássico).
/// </summary>
public static class WindowsIntegration
{
    private const string KeyName = "ReduzFacil";
    private const string ClassesRoot = @"Software\Classes";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly (string Id, string Label)[] MenuEntries =
    [
        ("qualidade", "Por qualidade"),
        ("whatsapp", "Para WhatsApp (16 MB)"),
        ("email", "Para e-mail (18 MB)"),
        ("gov", "Para gov.br e portais (2 MB)"),
        ("enem", "Para Enem e concursos (1 MB)"),
        ("curriculo", "Para currículo (2 MB)"),
        ("converter", "Converter (JPG, MP4, MP3)"),
        ("abrir", "Abrir no Reduz Fácil…"),
    ];

    private static IEnumerable<string> MenuParents()
    {
        foreach (var ext in FileTypes.AllExtensions)
            yield return $@"{ClassesRoot}\SystemFileAssociations\.{ext}\shell\{KeyName}";
        yield return $@"{ClassesRoot}\Directory\shell\{KeyName}";
    }

    public static bool IsShellMenuInstalled(out bool pointsToThisExe)
    {
        pointsToThisExe = false;
        using var k = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\SystemFileAssociations\.jpg\shell\{KeyName}\shell\01qualidade\command");
        if (k?.GetValue(null) is not string cmd) return false;
        pointsToThisExe = cmd.Contains(AppInfo.ExePath, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public static void InstallShellMenu()
    {
        var exe = AppInfo.ExePath;
        foreach (var parent in MenuParents())
        {
            using var root = Registry.CurrentUser.CreateSubKey(parent);
            root.SetValue("MUIVerb", "Reduzir com Reduz Fácil");
            root.SetValue("Icon", $"\"{exe}\",0");
            root.SetValue("SubCommands", "");
            root.SetValue("MultiSelectModel", "Player");
            var i = 1;
            foreach (var (id, label) in MenuEntries)
            {
                using var item = root.CreateSubKey($@"shell\{i:00}{id}");
                item.SetValue("MUIVerb", label);
                if (id == "abrir") item.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord); // linha separadora antes
                using var cmd = item.CreateSubKey("command");
                var perfil = id == "abrir" ? "--adicionar" : $"--perfil {id}";
                cmd.SetValue(null, $"\"{exe}\" {perfil} \"%1\"");
                i++;
            }
        }
        NativeMethods.NotifyShellAssocChanged();
        Log.Info("Menu do botão direito instalado para " + exe);
    }

    public static void RemoveShellMenu()
    {
        foreach (var parent in MenuParents())
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(parent, throwOnMissingSubKey: false); }
            catch (Exception ex) { Log.Warn($"Não consegui remover {parent}: {ex.Message}"); }
        }
        NativeMethods.NotifyShellAssocChanged();
        Log.Info("Menu do botão direito removido");
    }

    /// <summary>O .exe mudou de pasta (é portátil): atualiza o caminho no menu e na inicialização.</summary>
    public static void RepairIfMoved(bool menuWanted, bool startupWanted)
    {
        try
        {
            if (menuWanted && (!IsShellMenuInstalled(out var ok) || !ok)) InstallShellMenu();
            if (!menuWanted && IsShellMenuInstalled(out var mine) && mine) RemoveShellMenu();
            if (startupWanted) SetStartWithWindows(true);
        }
        catch (Exception ex)
        {
            Log.Warn("Não consegui conferir a integração com o Windows: " + ex.Message);
        }
    }

    public static void SetStartWithWindows(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(KeyName, $"\"{AppInfo.ExePath}\" --bandeja");
        else if (k.GetValue(KeyName) is not null) k.DeleteValue(KeyName, throwOnMissingValue: false);
    }

    public static bool IsStartWithWindows()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(KeyName) is string s && s.Contains(AppInfo.ExePath, StringComparison.OrdinalIgnoreCase);
    }
}
