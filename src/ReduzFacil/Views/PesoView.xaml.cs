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
    private CancellationTokenSource? _cts;
    private bool _bulk;

    public PesoView(AppServices services, MainWindow main)
    {
        _s = services;
        _main = main;
        InitializeComponent();
        HitList.ItemsSource = _hits;
        FolderText.Text = _s.Settings.ScanLastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        MinText.Text = Format.Number(_s.Settings.ScanMinMb);
        BuildQuick();
        _s.Queue.ItemChanged += i => Dispatcher.BeginInvoke(() => OnQueueItem(i));
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
        await ScanAsync(folder, minMb);
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

    private void OnReduce(object sender, RoutedEventArgs e)
    {
        var opts = _s.CurrentOptions();
        var n = 0;
        foreach (var h in _hits.Where(h => h.Selected && (h.Queued is null || h.Queued.IsFinished && h.Queued.Status is not (JobStatus.Done or JobStatus.KeptOriginal))))
        {
            var item = _s.Queue.Add(h.Hit.Path, opts, null, JobOrigin.Scan);
            if (item is null) continue;
            h.Queued = item;
            h.State = "Na fila…";
            n++;
        }
        if (n > 0) _main.Toast($"{n} arquivo(s) na fila. Acompanhe aqui ou na tela Reduzir.");
        UpdateSelection();
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
}
