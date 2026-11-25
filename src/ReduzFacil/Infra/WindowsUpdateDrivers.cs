using System.Reflection;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Infra;

public sealed record DriverUpdate(string Id, string Title, string Provider, string Class, DateTime? Date, long Size);

/// <summary>
/// Drivers pelo Windows Update (só os que a Microsoft já validou), pela API COM do próprio Windows.
/// Procurar não precisa de administrador; instalar precisa (roda no ajudante elevado).
/// </summary>
public static class WindowsUpdateDrivers
{
    private const string Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    private static object Create(string progId)
    {
        var t = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException("O Windows Update não está disponível neste computador.");
        return Activator.CreateInstance(t) ?? throw new InvalidOperationException("O Windows Update não respondeu.");
    }

    private static object? Get(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args);

    private static object? Call(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);

    private static void Set(object o, string name, object value) =>
        o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, [value]);

    private static object NewSession()
    {
        var s = Create("Microsoft.Update.Session");
        try { Set(s, "ClientApplicationID", "Reduz Facil"); } catch { /* opcional */ }
        return s;
    }

    public static Task<List<DriverUpdate>> SearchAsync() => Task.Run(() =>
    {
        var list = new List<DriverUpdate>();
        var searcher = Call(NewSession(), "CreateUpdateSearcher")!;
        var result = Call(searcher, "Search", Criteria)!;
        var updates = Get(result, "Updates")!;
        var n = Convert.ToInt32(Get(updates, "Count"));
        for (var i = 0; i < n; i++)
        {
            var u = Get(updates, "Item", i)!;
            string Str(string prop) { try { return Convert.ToString(Get(u, prop))?.Trim() ?? ""; } catch { return ""; } }
            DateTime? date = null;
            try { if (Get(u, "DriverVerDate") is DateTime d) date = d; } catch { /* sem data */ }
            long size = 0;
            try { size = Convert.ToInt64(Get(u, "MaxDownloadSize")); } catch { /* sem tamanho */ }
            var identity = Get(u, "Identity")!;
            var id = Convert.ToString(Get(identity, "UpdateID")) ?? "";
            if (list.Any(x => x.Id == id)) continue;
            list.Add(new DriverUpdate(id, Str("Title"), Str("DriverProvider"), Str("DriverClass"), date, size));
        }
        return list;
    });

    /// <summary>Baixa e instala (precisa de administrador). Devolve o resultado de cada driver: 2 = ok, 3 = ok com avisos.</summary>
    public static Dictionary<string, int> Install(IEnumerable<string> ids, out bool reboot)
    {
        reboot = false;
        var codes = new Dictionary<string, int>();
        var session = NewSession();
        var searcher = Call(session, "CreateUpdateSearcher")!;
        var coll = Create("Microsoft.Update.UpdateColl");
        var order = new List<string>();
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out var g)) continue;
            var res = Call(searcher, "Search", $"UpdateID='{g}'")!;
            var ups = Get(res, "Updates")!;
            if (Convert.ToInt32(Get(ups, "Count")) == 0) { codes[id] = 4; continue; }
            var u = Get(ups, "Item", 0)!;
            if (Get(u, "EulaAccepted") is false) Call(u, "AcceptEula");
            Call(coll, "Add", u);
            order.Add(id);
        }
        if (order.Count == 0) return codes;

        var downloader = Call(session, "CreateUpdateDownloader")!;
        Set(downloader, "Updates", coll);
        Call(downloader, "Download");
        var installer = Call(session, "CreateUpdateInstaller")!;
        Set(installer, "Updates", coll);
        var r = Call(installer, "Install")!;
        reboot = Get(r, "RebootRequired") is true;
        for (var i = 0; i < order.Count; i++)
        {
            try { codes[order[i]] = Convert.ToInt32(Get(Call(r, "GetUpdateResult", i)!, "ResultCode")); }
            catch (Exception ex) { codes[order[i]] = 4; Log.Warn("Resultado do driver: " + ex.Message); }
        }
        return codes;
    }
}
