namespace ReduzFacil.Core.Maintenance;

/// <summary>Uma pasta (ou um padrão de arquivos) que pode ser limpa.</summary>
public sealed record CleanRoot(string Dir, string Pattern = "*", bool Recursive = true);

/// <summary>Um item da Limpeza (temporários, cache de navegadores...).</summary>
public sealed class CleanTarget
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool NeedsAdmin { get; init; }
    /// <summary>Só apaga arquivos sem mudança há pelo menos este tempo (temporários em uso ficam).</summary>
    public TimeSpan MinAge { get; init; }
    public required Func<IEnumerable<CleanRoot>> Roots { get; init; }
}

public sealed record CleanSize(long Bytes, int Files, bool Denied);
public sealed record CleanResult(long Freed, int Deleted, int Skipped);

public static class Cleaner
{
    private static readonly EnumerationOptions Rec = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    private static readonly EnumerationOptions Flat = new() { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    private static IEnumerable<FileInfo> Files(CleanRoot root, TimeSpan minAge, Action? denied)
    {
        var dir = new DirectoryInfo(root.Dir);
        if (!dir.Exists) yield break;
        IEnumerator<FileInfo> e;
        try { e = dir.EnumerateFiles(root.Pattern, root.Recursive ? Rec : Flat).GetEnumerator(); }
        catch (UnauthorizedAccessException) { denied?.Invoke(); yield break; }
        catch (IOException) { yield break; }
        var limit = DateTime.UtcNow - minAge;
        using (e)
        {
            while (true)
            {
                FileInfo f;
                try
                {
                    if (!e.MoveNext()) break;
                    f = e.Current;
                }
                catch (UnauthorizedAccessException) { denied?.Invoke(); break; }
                catch (IOException) { break; }
                if (minAge > TimeSpan.Zero)
                {
                    DateTime t;
                    try { t = f.LastWriteTimeUtc; } catch { continue; }
                    if (t > limit) continue;
                }
                yield return f;
            }
        }
    }

    public static CleanSize Measure(CleanTarget t, CancellationToken ct = default) => Measure(t.Roots(), t.MinAge, ct);

    public static CleanSize Measure(IEnumerable<CleanRoot> roots, TimeSpan minAge, CancellationToken ct = default)
    {
        long bytes = 0;
        var n = 0;
        var denied = false;
        foreach (var r in roots)
        {
            foreach (var f in Files(r, minAge, () => denied = true))
            {
                ct.ThrowIfCancellationRequested();
                try { bytes += f.Length; n++; } catch { /* sumiu */ }
            }
        }
        return new CleanSize(bytes, n, denied);
    }

    public static CleanResult Clean(CleanTarget t, CancellationToken ct = default) => Clean(t.Roots(), t.MinAge, ct);

    /// <summary>Apaga os arquivos (os que estão em uso ficam) e as subpastas que ficaram vazias. A pasta raiz fica.</summary>
    public static CleanResult Clean(IEnumerable<CleanRoot> roots, TimeSpan minAge, CancellationToken ct = default)
    {
        long freed = 0;
        int del = 0, skip = 0;
        foreach (var r in roots)
        {
            foreach (var f in Files(r, minAge, null).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var len = f.Length;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    freed += len;
                    del++;
                }
                catch { skip++; }
            }
            if (r.Recursive && r.Pattern == "*") RemoveEmptyDirs(r.Dir, isRoot: true);
        }
        return new CleanResult(freed, del, skip);
    }

    private static void RemoveEmptyDirs(string dir, bool isRoot)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir, "*", Flat)) RemoveEmptyDirs(d, false);
            if (!isRoot && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { /* em uso ou sem permissão: fica */ }
    }
}

/// <summary>Os itens da Limpeza no Windows.</summary>
public static class CleanCatalog
{
    private static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    public static IReadOnlyList<CleanTarget> All { get; } =
    [
        new()
        {
            Id = "temp", Title = "Arquivos temporários", Description = "Sobras de instalações e programas (só os com mais de 1 dia).",
            MinAge = TimeSpan.FromDays(1), Roots = () => [new CleanRoot(Path.GetTempPath())],
        },
        new()
        {
            Id = "navegadores", Title = "Cache dos navegadores", Description = "Chrome, Edge, Brave, Opera e Firefox. Não apaga senhas, histórico nem logins.",
            Roots = BrowserCaches,
        },
        new()
        {
            Id = "miniaturas", Title = "Miniaturas de fotos", Description = "O Windows refaz quando você abrir as pastas.",
            Roots = () => [new CleanRoot(Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer"), "thumbcache_*.db", false)],
        },
        new()
        {
            Id = "windowsupdate", Title = "Sobras do Windows Update", Description = "Instaladores de atualizações que já foram aplicadas.",
            NeedsAdmin = true,
            Roots = () => [new CleanRoot(Path.Combine(Env(Environment.SpecialFolder.Windows), @"SoftwareDistribution\Download"))],
        },
        new()
        {
            Id = "wintemp", Title = "Temporários do Windows", Description = "Pasta Temp do próprio Windows (só os com mais de 1 dia).",
            NeedsAdmin = true, MinAge = TimeSpan.FromDays(1),
            Roots = () => [new CleanRoot(Path.Combine(Env(Environment.SpecialFolder.Windows), "Temp"))],
        },
        new()
        {
            Id = "erros", Title = "Relatórios de erro", Description = "Relatórios de programas que travaram, já enviados ou antigos.",
            NeedsAdmin = true,
            Roots = () =>
            [
                new CleanRoot(Path.Combine(Env(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportArchive")),
                new CleanRoot(Path.Combine(Env(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportQueue")),
            ],
        },
    ];

    public static CleanTarget? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    private static readonly string[] ChromiumCaches = ["Cache", "Code Cache", "GPUCache"];

    private static IEnumerable<CleanRoot> BrowserCaches()
    {
        var local = Env(Environment.SpecialFolder.LocalApplicationData);
        string[] chromium =
        [
            Path.Combine(local, @"Google\Chrome\User Data"),
            Path.Combine(local, @"Microsoft\Edge\User Data"),
            Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"),
            Path.Combine(local, @"Vivaldi\User Data"),
        ];
        foreach (var userData in chromium)
        {
            if (!Directory.Exists(userData)) continue;
            foreach (var profile in SafeDirs(userData))
            {
                var name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                foreach (var c in ChromiumCaches)
                {
                    var p = Path.Combine(profile, c);
                    if (Directory.Exists(p)) yield return new CleanRoot(p);
                }
            }
        }
        // Opera guarda o cache no Local (sem perfis)
        foreach (var opera in new[] { Path.Combine(local, @"Opera Software\Opera Stable"), Path.Combine(local, @"Opera Software\Opera GX Stable") })
            foreach (var c in ChromiumCaches)
                if (Directory.Exists(Path.Combine(opera, c))) yield return new CleanRoot(Path.Combine(opera, c));
        var ff = Path.Combine(local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff))
            foreach (var profile in SafeDirs(ff))
                if (Directory.Exists(Path.Combine(profile, "cache2"))) yield return new CleanRoot(Path.Combine(profile, "cache2"));
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return []; }
    }
}
