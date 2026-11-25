using Microsoft.Win32;

namespace ReduzFacil.Infra;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public sealed record StartupApp(string Name, string Command, StartupSource Source, bool Enabled)
{
    public bool NeedsAdmin => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;
    public string DisplayName => Source is StartupSource.UserFolder or StartupSource.CommonFolder ? Path.GetFileNameWithoutExtension(Name) : Name;
}

/// <summary>
/// Programas que abrem com o Windows. Liga/desliga do mesmo jeito que o Gerenciador de Tarefas
/// (chave StartupApproved): nada é apagado, dá para voltar atrás a qualquer momento.
/// </summary>
public static class StartupApps
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static RegistryKey Hive(StartupSource s, RegistryView view = RegistryView.Registry64) =>
        RegistryKey.OpenBaseKey(s is StartupSource.UserRun or StartupSource.UserFolder ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);

    private static string ApprovedKey(StartupSource s) => Approved + s switch
    {
        StartupSource.MachineRun32 => "Run32",
        StartupSource.UserFolder or StartupSource.CommonFolder => "StartupFolder",
        _ => "Run",
    };

    private static bool IsEnabled(StartupSource s, string name)
    {
        try
        {
            using var k = Hive(s).OpenSubKey(ApprovedKey(s));
            return k?.GetValue(name) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
        catch { return true; }
    }

    public static List<StartupApp> List()
    {
        var list = new List<StartupApp>();
        void FromRun(StartupSource s, RegistryView view)
        {
            try
            {
                using var k = Hive(s, view).OpenSubKey(RunKey);
                if (k is null) return;
                foreach (var n in k.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    list.Add(new StartupApp(n, Convert.ToString(k.GetValue(n)) ?? "", s, IsEnabled(s, n)));
                }
            }
            catch { /* sem acesso */ }
        }
        void FromFolder(StartupSource s, Environment.SpecialFolder f)
        {
            try
            {
                var dir = Environment.GetFolderPath(f);
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.GetFiles(dir))
                {
                    var n = Path.GetFileName(file);
                    if (n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new StartupApp(n, file, s, IsEnabled(s, n)));
                }
            }
            catch { /* sem acesso */ }
        }
        FromRun(StartupSource.UserRun, RegistryView.Registry64);
        FromRun(StartupSource.MachineRun, RegistryView.Registry64);
        FromRun(StartupSource.MachineRun32, RegistryView.Registry32);
        FromFolder(StartupSource.UserFolder, Environment.SpecialFolder.Startup);
        FromFolder(StartupSource.CommonFolder, Environment.SpecialFolder.CommonStartup);
        return list.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Liga ou desliga. As de "todos os usuários" precisam de administrador.</summary>
    public static void SetEnabled(StartupSource s, string name, bool enable)
    {
        var data = new byte[12];
        data[0] = (byte)(enable ? 2 : 3);
        if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        using var k = Hive(s).CreateSubKey(ApprovedKey(s), true);
        k.SetValue(name, data, RegistryValueKind.Binary);
    }
}
