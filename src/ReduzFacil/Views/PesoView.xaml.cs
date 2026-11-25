using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Scan;
using ReduzFacil.Core.Util;
using ReduzFacil.Infra;

namespace ReduzFacil.Views;

public partial class PesoView : UserControl
{
    private readonly AppServices _s;
    private readonly MainWindow _main;
    private readonly ObservableCollection<HitVm> _hits = [];
    private readonly ObservableCollection<DupGroupVm> _dups = [];
    private CancellationTokenSource? _cts;
    private bool _bulk;

    public PesoView(AppServices services, MainWindow main)
    {
        _s = services;
        _main = main;
        InitializeComponent();
        HitList.ItemsSource = _hits;
        DupList.ItemsSource = _dups;
        FolderText.Text = _s.Settings.ScanLastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        MinText.Text = Format.Number(_s.Settings.ScanMinMb);
        BuildQuick();
        BuildLegend();
        _s.Queue.ItemChanged += i => Dispatcher.BeginInvoke(() => OnQueueItem(i));
        ThemeManager.Changed += () =>
        {
            foreach (var h in _hits) h.Refresh();
            DupList.Items.Refresh();
        };
        UpdateSelection();
    }

    public bool IsScanning => _cts is not null;

    private void BuildQuick()
    {
        void Add(string label, string? path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            var b = new Button { Content = label, Style = (Style)FindResource("Small"), Margin = new Thickness(0, 0, 8, 6), ToolTip = path };
            b.Click += (_, _) => FolderText.Text = path;
            QuickPanel.Children.Add(b);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("Minha pasta", home);
        Add("Imagens", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Vídeos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Downloads", Path.Combine(home, "Downloads"));
        Add("Área de trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                    Add($"Disco {d.Name.TrimEnd('\\')} ({Format.Bytes(d.AvailableFreeSpace)} livres)", d.RootDirectory.FullName);
            }
            catch { /* disco indisponível */ }
        }
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Onde procurar?" };
        if (Directory.Exists(FolderText.Text)) dlg.InitialDirectory = FolderText.Text;
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) FolderText.Text = dlg.FolderName;
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }
        var folder = FolderText.Text.Trim().Trim('"');
        if (!Directory.Exists(folder))
        {
            _main.Toast("Essa pasta não existe.");
            return;
        }
        if (!Format.TryParseNumber(MinText.Text, out var minMb) || minMb < 0) minMb = 1;
        _s.Settings.ScanLastFolder = folder;
        _s.Settings.ScanMinMb = minMb;
        _s.Save();
        if (ModeDup.IsChecked == true) await FindDuplicatesAsync(folder, SimilarCheck.IsChecked == true);
        else if (ModeMap.IsChecked == true) await BuildMapAsync(folder);
        else await ScanAsync(folder, minMb);
    }

    /// <summary>Também usado pelo teste automático.</summary>
    public async Task ScanAsync(string folder, double minMb)
    {
        _hits.Clear();
        UpdateSelection();
        _cts = new CancellationTokenSource();
        ScanBtn.Content = "Parar";
        Status.Text = "Procurando…";
        var progress = new Progress<ScanProgress>(p =>
            Status.Text = $"Procurando… {p.FilesSeen:N0} arquivos vistos · {p.Hits:N0} pesados · ~{Format.Bytes(p.PotentialBytes)} para economizar · {p.Current}");
        try
        {
            var scanner = new WeightScanner(_s.Tools);
            var opts = _s.CurrentOptions();
            var token = _cts.Token;
            var suffix = _s.Settings.Output.Suffix;
            var hits = await Task.Run(() => scanner.ScanAsync(folder, opts, (long)(minMb * 1024 * 1024), progress, token, suffix));
            foreach (var h in hits)
            {
                var vm = new HitVm(h);
                vm.PropertyChanged += (_, pe) => { if (!_bulk && pe.PropertyName == nameof(HitVm.Selected)) UpdateSelection(); };
                _hits.Add(vm);
            }
            var total = hits.Sum(h => h.SavingBytes);
            Status.Text = hits.Count == 0
                ? "Nada pesado por aqui: nenhum arquivo daria uma economia que valha a pena."
                : $"{hits.Count:N0} arquivo(s) pesado(s): daria para economizar ~{Format.Bytes(total)}. A estimativa é aproximada; o valor real aparece depois de reduzir.";
        }
        catch (OperationCanceledException)
        {
            Status.Text = $"Busca interrompida. {_hits.Count} resultado(s).";
        }
        catch (Exception ex)
        {
            Status.Text = "Erro na busca: " + ex.Message;
            Log.Error("Erro no Onde está o peso", ex);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            ScanBtn.Content = "Procurar";
            UpdateSelection();
        }
    }

    private void OnAll(object sender, RoutedEventArgs e)
    {
        _bulk = true;
        foreach (var h in _hits) h.Selected = true;
        _bulk = false;
        UpdateSelection();
    }

    private void OnNone(object sender, RoutedEventArgs e)
    {
        _bulk = true;
        foreach (var h in _hits) h.Selected = false;
        _bulk = false;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var sel = _hits.Where(h => h.Selected).ToList();
        SelText.Text = _hits.Count == 0 ? "" : $"{sel.Count} selecionado(s) · ~{Format.Bytes(sel.Sum(h => h.Hit.SavingBytes))} de economia estimada";
        ReduceBtn.IsEnabled = sel.Any(h => h.Queued is null || h.Queued.Status is JobStatus.Failed or JobStatus.Canceled);
        RecycleBtn.IsEnabled = RecyclableItems().Any();
    }

    private readonly List<QueueItem> _batch = [];

    private void OnReduce(object sender, RoutedEventArgs e)
    {
        var todo = _hits.Where(h => h.Selected && (h.Queued is null || h.Queued.IsFinished && h.Queued.Status is not (JobStatus.Done or JobStatus.KeptOriginal))).ToList();
        if (todo.Count == 0) return;
        var opts = _s.CurrentOptions();
        var level = opts.Level switch { Core.Model.Level.Maxima => "Máxima qualidade", Core.Model.Level.Menor => "Menor tamanho", _ => "Equilibrado" };
        var how = opts.ConvertOnly ? "no modo Converter" : opts.TargetBytes is { } t ? $"com meta de {Format.Bytes(t)} por arquivo" : $"no nível {level}";
        var est = todo.Sum(h => h.Hit.SavingBytes);
        var msg = $"Reduzir {todo.Count} arquivo(s) {how}?\n\nEconomia estimada: ~{Format.Bytes(est)}.\n" +
                  "Os originais continuam onde estão; os reduzidos são salvos com \"" + _s.EffectiveSuffix + "\" no nome.\n\n" +
                  "(Para mudar o nível ou a meta, ajuste na tela Reduzir antes.)";
        if (MessageBox.Show(Window.GetWindow(this), msg, "Reduzir agora", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        // Novo lote: o que já terminou sai da faixa.
        _batch.RemoveAll(i => i.IsFinished);
        foreach (var h in todo)
        {
            var item = _s.Queue.Add(h.Hit.Path, opts, null, JobOrigin.Scan);
            if (item is null) continue;
            h.Queued = item;
            h.State = "Na fila…";
            _batch.Add(item);
        }
        if (_s.Queue.IsPaused) _s.Queue.ResumeAll();
        _s.Queue.Schedule();
        ReduceCloseBtn.Visibility = Visibility.Collapsed;
        ReduceBar.Visibility = Visibility.Visible;
        UpdateReduceBar();
        UpdateSelection();
    }

    private void UpdateReduceBar()
    {
        if (_batch.Count == 0) return;
        var done = _batch.Count(i => i.IsFinished);
        var saved = _batch.Where(i => i.Result is { KeptOriginal: false }).Sum(i => Math.Max(0, i.Result!.OriginalBytes - i.Result.FinalBytes));
        var running = _batch.Where(i => i.Status == JobStatus.Running).Sum(i => i.Progress);
        ReduceProgress.Value = (done + running) / _batch.Count;
        ReducePauseBtn.Content = _s.Queue.IsPaused ? "Continuar" : "Pausar";
        if (done == _batch.Count)
        {
            var failed = _batch.Count(i => i.Status == JobStatus.Failed);
            ReduceText.Text = $"Pronto: {_batch.Count - failed} de {_batch.Count} reduzido(s), {Format.Bytes(saved)} economizados." +
                              (RecyclableItems().Any() ? " Para liberar o espaço de verdade, use \"Originais para a Lixeira…\"." : "") +
                              (failed > 0 ? $" {failed} com erro (veja na lista)." : "");
            ReducePauseBtn.Visibility = Visibility.Collapsed;
            ReduceCloseBtn.Visibility = Visibility.Visible;
            if (RecyclableItems().Any()) RecycleBtn.Style = (Style)FindResource("Primary");
        }
        else
        {
            ReduceText.Text = _s.Queue.IsPaused
                ? $"Pausado: {done} de {_batch.Count} prontos · {Format.Bytes(saved)} economizados até agora"
                : $"Reduzindo {Math.Min(done + 1, _batch.Count)} de {_batch.Count} · {Format.Bytes(saved)} economizados até agora";
            ReducePauseBtn.Visibility = Visibility.Visible;
        }
    }

    private void OnReducePause(object sender, RoutedEventArgs e)
    {
        if (_s.Queue.IsPaused) _s.Queue.ResumeAll();
        else _s.Queue.PauseAll();
        UpdateReduceBar();
    }

    private void OnSeeQueue(object sender, RoutedEventArgs e) => _main.ShowReduzir();

    private void OnReduceClose(object sender, RoutedEventArgs e)
    {
        _batch.Clear();
        ReduceBar.Visibility = Visibility.Collapsed;
    }

    private void OnQueueItem(QueueItem i)
    {
        var h = _hits.FirstOrDefault(x => x.Queued == i);
        if (h is null) return;
        h.State = i.Status switch
        {
            JobStatus.Waiting => "Na fila…",
            JobStatus.Running => $"Reduzindo… {i.Progress * 100:0}%",
            JobStatus.Done when i.Result is { } r => $"✓ Reduzido: {Format.Bytes(r.OriginalBytes)} → {Format.Bytes(r.FinalBytes)} (−{r.SavingPercent}%) · {Path.GetFileName(r.OutputPath)}",
            JobStatus.KeptOriginal => "Já estava otimizado (nada foi salvo)",
            JobStatus.Failed => "Erro: " + i.Error,
            JobStatus.Canceled => "Cancelado",
            _ => "",
        };
        if (_batch.Contains(i)) UpdateReduceBar();
        if (i.IsFinished) UpdateSelection();
    }

    /// <summary>Originais cujo arquivo reduzido existe e é menor (só esses podem ir para a Lixeira).</summary>
    private IEnumerable<(HitVm Hit, string Output)> RecyclableItems() =>
        _hits.Where(h => h.Queued is { Status: JobStatus.Done, Result: { OutputPath: { } o } r } && r.FinalBytes < r.OriginalBytes && File.Exists(o) && File.Exists(h.Hit.Path))
             .Select(h => (h, h.Queued!.Result!.OutputPath!));

    private void OnRecycle(object sender, RoutedEventArgs e)
    {
        var list = RecyclableItems().ToList();
        if (list.Count == 0) return;
        var bytes = list.Sum(x => x.Hit.Queued!.Result!.OriginalBytes);
        var msg = $"Mandar {list.Count} original(is) ({Format.Bytes(bytes)}) para a Lixeira?\n\n" +
                  "As versões reduzidas continuam salvas. Se mudar de ideia, dá para restaurar os originais pela Lixeira do Windows " +
                  "(arquivos de pen drive ou rede podem ser apagados de vez — o Windows avisa antes).";
        if (MessageBox.Show(Window.GetWindow(this), msg, "Liberar espaço", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var n = RecycleBin.Send(list.Select(x => x.Hit.Hit.Path));
        foreach (var (h, _) in list.Where(x => !File.Exists(x.Hit.Hit.Path))) h.State = "✓ Original na Lixeira · " + h.State.Replace("✓ ", "");
        Log.Info($"{n} originais enviados para a Lixeira");
        _main.Toast($"{n} original(is) na Lixeira.");
        UpdateSelection();
    }

    // ---------------- repetidos ----------------

    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (DupList is null || HitList is null || MapPanel is null) return; // durante o InitializeComponent
        var dup = ModeDup.IsChecked == true;
        var map = ModeMap.IsChecked == true;
        var heavy = !dup && !map;
        HitList.Visibility = HeavyFooter.Visibility = MinRow.Visibility = heavy ? Visibility.Visible : Visibility.Collapsed;
        DupList.Visibility = DupFooter.Visibility = SimilarCheck.Visibility = dup ? Visibility.Visible : Visibility.Collapsed;
        MapPanel.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
        ReduceBar.Visibility = heavy && _batch.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = map && _mapRoot is null ? "Escolha a pasta ou o disco e clique em Procurar para ver o mapa." : "";
    }

    public void ShowDuplicatesMode() => ModeDup.IsChecked = true;

    public void ShowMapMode() => ModeMap.IsChecked = true;

    /// <summary>Também usado pelo teste automático.</summary>
    public async Task FindDuplicatesAsync(string folder, bool similar)
    {
        _dups.Clear();
        UpdateDupSelection();
        _cts = new CancellationTokenSource();
        ScanBtn.Content = "Parar";
        Status.Text = "Procurando repetidos…";
        var progress = new Progress<DupProgress>(p =>
            Status.Text = p.Total > 0 ? $"{p.Stage}… {p.Done:N0} de {p.Total:N0}" : $"{p.Stage}… {p.Done:N0} arquivos");
        try
        {
            var finder = new DuplicateFinder(_s.Tools);
            var token = _cts.Token;
            var groups = await Task.Run(() => finder.FindAsync(folder, similar, progress, token));
            foreach (var g in groups) _dups.Add(new DupGroupVm(g, UpdateDupSelection));
            var exact = groups.Where(g => g.Exact).ToList();
            var sim = groups.Count - exact.Count;
            Status.Text = groups.Count == 0
                ? "Nenhum repetido encontrado."
                : $"{exact.Count} grupo(s) de idênticos (libera ~{Format.Bytes(exact.Sum(g => g.Wasted))})" +
                  (sim > 0 ? $" e {sim} grupo(s) de fotos parecidas para conferir." : ".");
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Busca interrompida.";
        }
        catch (Exception ex)
        {
            Status.Text = "Erro na busca: " + ex.Message;
            Log.Error("Erro em Repetidos", ex);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            ScanBtn.Content = "Procurar";
            UpdateDupSelection();
        }
    }

    private void UpdateDupSelection()
    {
        var sel = _dups.SelectMany(g => g.Files).Where(f => f.Selected).ToList();
        DupSelText.Text = _dups.Count == 0 ? "" : $"{sel.Count} marcado(s) para a Lixeira · {Format.Bytes(sel.Sum(f => f.File.SizeBytes))}";
        DupRecycleBtn.IsEnabled = sel.Count > 0;
    }

    private void OnDupSuggested(object sender, RoutedEventArgs e)
    {
        foreach (var g in _dups)
            foreach (var f in g.Files)
                f.SetSelectedSilently(g.Group.Exact && !f.File.Keep);
        UpdateDupSelection();
    }

    private void OnDupNone(object sender, RoutedEventArgs e)
    {
        foreach (var f in _dups.SelectMany(g => g.Files)) f.SetSelectedSilently(false);
        UpdateDupSelection();
    }

    private static DupFileVm? DupVm(object sender) => (sender as FrameworkElement)?.Tag as DupFileVm;

    private void OnDupOpen(object sender, RoutedEventArgs e)
    {
        if (DupVm(sender) is { } f) Explorer.Open(f.File.Path);
    }

    private void OnDupFolder(object sender, RoutedEventArgs e)
    {
        if (DupVm(sender) is { } f) Explorer.ShowInFolder(f.File.Path);
    }

    private void OnDupRecycle(object sender, RoutedEventArgs e)
    {
        // Segurança: de cada grupo, pelo menos um arquivo fica.
        var allMarked = _dups.Where(g => g.Files.All(f => f.Selected)).ToList();
        if (allMarked.Count > 0)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"Em {allMarked.Count} grupo(s) todos os arquivos estão marcados. Deixe pelo menos um de cada grupo desmarcado (é o que fica).",
                "Repetidos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var sel = _dups.SelectMany(g => g.Files.Select(f => (g, f))).Where(x => x.f.Selected).ToList();
        if (sel.Count == 0) return;
        var similar = sel.Count(x => !x.g.Group.Exact);
        var msg = $"Mandar {sel.Count} arquivo(s) ({Format.Bytes(sel.Sum(x => x.f.File.SizeBytes))}) para a Lixeira?\n\n" +
                  (similar > 0 ? $"Atenção: {similar} são fotos PARECIDAS, não idênticas. Confira se não há diferença que importe.\n\n" : "") +
                  "Dá para restaurar pela Lixeira do Windows (arquivos de pen drive ou rede podem ser apagados de vez — o Windows avisa antes).";
        if (MessageBox.Show(Window.GetWindow(this), msg, "Repetidos", MessageBoxButton.YesNo,
                similar > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var n = RecycleBin.Send(sel.Select(x => x.f.File.Path));
        Log.Info($"Repetidos: {n} arquivo(s) para a Lixeira");
        // Some com o que foi para a Lixeira; grupo que ficou com 1 arquivo some também.
        foreach (var g in _dups.ToList())
        {
            g.Files.RemoveAll(f => !File.Exists(f.File.Path));
            if (g.Files.Count < 2) _dups.Remove(g);
        }
        DupList.Items.Refresh();
        _main.Toast($"{n} arquivo(s) na Lixeira.");
        UpdateDupSelection();
    }

    // ---------------- mapa do disco ----------------

    private MapNode? _mapRoot;
    private MapNode? _mapCurrent;

    public static readonly (MapKind Kind, string Label, string Hex)[] MapColors =
    [
        (MapKind.Fotos, "Fotos", "#3B82F6"),
        (MapKind.Videos, "Vídeos", "#EC4899"),
        (MapKind.Audios, "Áudios", "#F59E0B"),
        (MapKind.Pdfs, "PDFs", "#EF4444"),
        (MapKind.Outros, "Outros arquivos", "#94A3B8"),
    ];

    private static System.Windows.Media.Brush KindBrush(MapKind k)
    {
        var hex = MapColors.First(c => c.Kind == k).Hex;
        var b = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private void BuildLegend()
    {
        MapLegend.Children.Clear();
        foreach (var (kind, label, _) in MapColors)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            sp.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = KindBrush(kind), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            var pct = _mapCurrent is { Size: > 0 } n ? $" {n.ByKind[(int)kind] * 100.0 / n.Size:0}%" : "";
            var t = new TextBlock { Text = label + pct, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            sp.Children.Add(t);
            MapLegend.Children.Add(sp);
        }
    }

    /// <summary>Também usado pelo teste automático.</summary>
    public async Task BuildMapAsync(string folder)
    {
        _cts = new CancellationTokenSource();
        ScanBtn.Content = "Parar";
        Status.Text = "Medindo as pastas…";
        var progress = new Progress<MapProgress>(p => Status.Text = $"Medindo… {p.Folders:N0} pastas · {Format.Bytes(p.Bytes)} · {p.Current}");
        try
        {
            var token = _cts.Token;
            _mapRoot = await Task.Run(() => DiskMapper.Build(folder, progress, token));
            ShowMapNode(_mapRoot);
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Medição interrompida.";
        }
        catch (Exception ex)
        {
            Status.Text = "Erro no mapa: " + ex.Message;
            Log.Error("Erro no mapa do disco", ex);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            ScanBtn.Content = "Procurar";
        }
    }

    private void ShowMapNode(MapNode n)
    {
        _mapCurrent = n;
        MapPath.Text = $"{n.Path}  ·  {Format.Bytes(n.Size)} em {n.Files:N0} arquivo(s)";
        MapUpBtn.IsEnabled = n.Parent is not null;
        var media = n.ByKind[0] + n.ByKind[1] + n.ByKind[2] + n.ByKind[3];
        Status.Text = $"Clique numa pasta para entrar. Fotos, vídeos, áudios e PDFs ocupam {Format.Bytes(media)} aqui ({(n.Size > 0 ? media * 100.0 / n.Size : 0):0}%).";
        BuildLegend();
        RenderMap();
    }

    private void OnMapResize(object sender, SizeChangedEventArgs e) => RenderMap();

    private void RenderMap()
    {
        MapCanvas.Children.Clear();
        if (_mapCurrent is not { } n || MapCanvas.ActualWidth < 20 || MapCanvas.ActualHeight < 20) return;
        // Até 60 blocos; o resto vira um bloco só.
        var items = n.Children.Take(60).ToList();
        var rest = n.Children.Skip(60).ToList();
        if (rest.Count > 0)
        {
            var other = new MapNode { Name = $"(mais {rest.Count} pastas pequenas)", Path = n.Path, IsFolder = false, Size = rest.Sum(r => r.Size) };
            foreach (var r in rest) for (var i = 0; i < 5; i++) other.ByKind[i] += r.ByKind[i];
            items.Add(other);
        }
        if (items.Count == 0)
        {
            MapCanvas.Children.Add(new TextBlock { Text = "Pasta vazia.", Margin = new Thickness(16) });
            return;
        }
        var rects = Treemap.Layout(items, c => c.Size, new MapRect(0, 0, MapCanvas.ActualWidth, MapCanvas.ActualHeight));
        foreach (var (node, r) in rects)
        {
            if (r.W < 1 || r.H < 1) continue;
            var block = new Border
            {
                Width = Math.Max(0, r.W - 2),
                Height = Math.Max(0, r.H - 2),
                Background = KindBrush(node.Dominant),
                CornerRadius = new CornerRadius(4),
                Cursor = node.IsFolder && node.Children.Count > 0 ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
                ToolTip = $"{node.Name}\n{Format.Bytes(node.Size)} · {node.Files:N0} arquivo(s)\n" +
                          string.Join("  ", MapColors.Where(c => node.ByKind[(int)c.Kind] > 0).Select(c => $"{c.Label}: {Format.Bytes(node.ByKind[(int)c.Kind])}")),
                Opacity = node.IsFolder ? 0.92 : node.IsFile ? 0.8 : 0.55,
            };
            if (r.W > 70 && r.H > 34)
            {
                var sp = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
                sp.Children.Add(new TextBlock { Text = node.Name, Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
                sp.Children.Add(new TextBlock { Text = Format.Bytes(node.Size), Foreground = System.Windows.Media.Brushes.White, FontSize = 12 });
                block.Child = sp;
            }
            var target = node;
            if (node.IsFile) block.Cursor = System.Windows.Input.Cursors.Hand;
            block.MouseLeftButtonUp += (_, _) =>
            {
                if (target.IsFolder && target.Children.Count > 0) ShowMapNode(target);
                else if (target.IsFile) ShowFileMenu(block, target);
            };
            Canvas.SetLeft(block, r.X + 1);
            Canvas.SetTop(block, r.Y + 1);
            MapCanvas.Children.Add(block);
        }
    }

    /// <summary>Clique num arquivo do mapa: reduzir na hora ou mostrar no Explorer.</summary>
    private void ShowFileMenu(FrameworkElement anchor, MapNode file)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var canReduce = Core.Model.FileTypes.KindOf(file.Path) is not null;
        var reduce = new MenuItem { Header = canReduce ? $"Reduzir este arquivo ({Format.Bytes(file.Size)})" : "Este tipo de arquivo não é reduzido pelo app", IsEnabled = canReduce };
        reduce.Click += (_, _) =>
        {
            var item = _s.Queue.Add(file.Path, _s.CurrentOptions(), null, JobOrigin.Scan);
            if (item is null) return;
            _batch.RemoveAll(i => i.IsFinished);
            _batch.Add(item);
            _s.Queue.Schedule();
            _main.Toast($"Reduzindo {file.Name}… Acompanhe na tela Reduzir.");
        };
        var show = new MenuItem { Header = "Mostrar no Explorer" };
        show.Click += (_, _) => Explorer.ShowInFolder(file.Path);
        var open = new MenuItem { Header = "Abrir" };
        open.Click += (_, _) => Explorer.Open(file.Path);
        menu.Items.Add(reduce);
        menu.Items.Add(open);
        menu.Items.Add(show);
        menu.IsOpen = true;
    }

    private void OnMapUp(object sender, RoutedEventArgs e)
    {
        if (_mapCurrent?.Parent is { } p) ShowMapNode(p);
    }

    private void OnMapOpen(object sender, RoutedEventArgs e)
    {
        if (_mapCurrent is { } n) Explorer.ShowInFolder(n.Path);
    }

    private void OnMapHeavy(object sender, RoutedEventArgs e)
    {
        if (_mapCurrent is not { } n) return;
        FolderText.Text = n.Path;
        ModeHeavy.IsChecked = true;
        OnScan(sender, e);
    }

    /// <summary>Abre esta tela já procurando (usado pelas sugestões do Início).</summary>
    public void StartScan(string folder, bool map)
    {
        FolderText.Text = folder;
        if (map) ModeMap.IsChecked = true; else ModeHeavy.IsChecked = true;
        OnScan(this, new RoutedEventArgs());
    }
}
