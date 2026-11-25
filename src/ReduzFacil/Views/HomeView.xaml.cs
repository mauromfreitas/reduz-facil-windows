using System.Windows;
using System.Windows.Controls;
using ReduzFacil.Core.Scan;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Views;

/// <summary>Início: espaço dos discos, economia e até 3 sugestões do que fazer.</summary>
public partial class HomeView : UserControl
{
    private readonly AppServices _s;
    private readonly MainWindow _main;
    private bool _tipsRunning;
    private DateTime _tipsAt = DateTime.MinValue;

    public HomeView(AppServices services, MainWindow main)
    {
        _s = services;
        _main = main;
        InitializeComponent();
        _s.History.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) UpdateSavings(); });
        Reload();
    }

    public void Reload()
    {
        var h = DateTime.Now.Hour;
        Hello.Text = h < 12 ? "Bom dia!" : h < 18 ? "Boa tarde!" : "Boa noite!";
        BuildDrives();
        UpdateSavings();
        // Sugestões custam uma varredura: no máximo a cada 10 minutos.
        if (!_tipsRunning && DateTime.Now - _tipsAt > TimeSpan.FromMinutes(10)) _ = BuildTipsAsync();
    }

    private static IEnumerable<DriveInfo> Drives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable && d.TotalSize > 0; }
            catch { ok = false; }
            if (ok) yield return d;
        }
    }

    private void BuildDrives()
    {
        DrivesPanel.Children.Clear();
        foreach (var d in Drives())
        {
            long total, free;
            string label;
            try { total = d.TotalSize; free = d.AvailableFreeSpace; label = d.VolumeLabel; }
            catch { continue; }
            var used = total - free;
            var pctFree = free * 100.0 / total;
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var top = new DockPanel();
            var right = new TextBlock { Text = $"{Format.Bytes(free)} livres de {Format.Bytes(total)}", FontSize = 13 };
            right.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
            top.Children.Add(new TextBlock
            {
                Text = $"Disco {d.Name.TrimEnd('\\')}" + (string.IsNullOrWhiteSpace(label) ? "" : $" ({label})"),
                FontWeight = FontWeights.SemiBold,
            });
            row.Children.Add(top);
            var bar = new ProgressBar { Maximum = 1, Value = (double)used / total, Height = 10, Margin = new Thickness(0, 6, 0, 0) };
            if (pctFree < 10) bar.SetResourceReference(ForegroundProperty, "Danger");
            else if (pctFree < 20) bar.SetResourceReference(ForegroundProperty, "Warn");
            row.Children.Add(bar);
            DrivesPanel.Children.Add(row);
        }
    }

    private void UpdateSavings()
    {
        var t = _s.History.Totals;
        SavedTotal.Text = Format.Bytes(t.SavedBytes);
        SavedSub.Text = t.Files == 0 ? "Ainda nada por aqui: reduza seus primeiros arquivos!" : $"em {t.Files:N0} arquivo(s), desde o primeiro uso";
        var now = DateTime.Now;
        var month = _s.History.Entries.Where(e => e.When.Year == now.Year && e.When.Month == now.Month).ToList();
        SavedMonth.Text = $"{Format.Bytes(month.Sum(e => e.Saved))} em {month.Count:N0} arquivo(s)";
    }

    private sealed record Tip(string Text, string Button, Action Act);

    private async Task BuildTipsAsync()
    {
        _tipsRunning = true;
        TipsPanel.Children.Clear();
        TipsPanel.Children.Add(Muted("Olhando as pastas de fotos, vídeos e downloads…"));
        var tips = new List<Tip>();
        try
        {
            // Disco quase cheio
            foreach (var d in Drives())
            {
                double pct;
                try { pct = d.AvailableFreeSpace * 100.0 / d.TotalSize; } catch { continue; }
                if (pct < 15)
                {
                    var root = d.RootDirectory.FullName;
                    tips.Add(new Tip($"O disco {d.Name.TrimEnd('\\')} está quase cheio ({pct:0}% livre). Veja no mapa o que mais ocupa.", "Ver o mapa",
                        () => { _main.ShowPage("peso"); _main.Peso.StartScan(root, map: true); }));
                }
            }

            // Pastas pessoais com muita mídia
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folders = new (string Label, string Path)[]
            {
                ("Vídeos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
                ("Imagens", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                ("Downloads", Path.Combine(home, "Downloads")),
                ("Área de trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                ("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            };
            var measured = new List<(string Label, string Path, long Media)>();
            foreach (var (label, path) in folders.Where(f => !string.IsNullOrEmpty(f.Path) && Directory.Exists(f.Path)).DistinctBy(f => f.Path))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    var map = await Task.Run(() => DiskMapper.Build(path, null, cts.Token));
                    measured.Add((label, path, map.ByKind[0] + map.ByKind[1] + map.ByKind[2] + map.ByKind[3]));
                }
                catch (OperationCanceledException) { /* pasta enorme: pula */ }
            }
            foreach (var (label, path, media) in measured.Where(m => m.Media > 500L * 1024 * 1024).OrderByDescending(m => m.Media))
            {
                var p = path;
                tips.Add(new Tip($"{label} tem {Format.Bytes(media)} em fotos, vídeos, áudios e PDFs. Veja o que dá para reduzir.", "Ver o que reduzir",
                    () => { _main.ShowPage("peso"); _main.Peso.StartScan(p, map: false); }));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Sugestões do Início: " + ex.Message);
        }
        finally
        {
            _tipsRunning = false;
            _tipsAt = DateTime.Now;
        }

        TipsPanel.Children.Clear();
        if (tips.Count == 0)
        {
            TipsPanel.Children.Add(Muted("Tudo em ordem: nenhuma pasta pessoal com muito peso em mídia, e os discos têm espaço."));
            return;
        }
        foreach (var tip in tips.Take(3))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var b = new Button { Content = tip.Button, Style = (Style)FindResource("Small") };
            b.Click += (_, _) => tip.Act();
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
            row.Children.Add(new TextBlock { Text = "💡  " + tip.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            TipsPanel.Children.Add(row);
        }
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private void OnReduce(object sender, RoutedEventArgs e) => _main.ShowReduzir();

    private void OnMap(object sender, RoutedEventArgs e)
    {
        _main.ShowPage("peso");
        _main.Peso.ShowMapMode();
    }

    private void OnDup(object sender, RoutedEventArgs e)
    {
        _main.ShowPage("peso");
        _main.Peso.ShowDuplicatesMode();
    }
}
