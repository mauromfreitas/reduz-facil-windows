using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReduzFacil.Core.Compress;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Views;

public partial class FerramentasView : UserControl
{
    private readonly AppServices _s;
    private readonly MainWindow _main;
    private readonly ObservableCollection<PhotoEntry> _photos = [];
    private string? _pdf;
    private string? _lastSplitDir;
    private bool _busy;

    public sealed record PhotoEntry(string Path)
    {
        public override string ToString() => System.IO.Path.GetFileName(Path) + "   —   " + System.IO.Path.GetDirectoryName(Path);
    }

    public FerramentasView(AppServices services, MainWindow main)
    {
        _s = services;
        _main = main;
        InitializeComponent();
        PhotoList.ItemsSource = _photos;
        UpdateJoin();
    }

    /// <summary>Arquivos arrastados ou colados nesta tela: fotos vão para "Juntar", PDF vai para "Separar".</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var all = FileCollector.Collect(paths, null).Select(f => f.Path).ToList();
        var pdf = all.FirstOrDefault(p => FileTypes.KindOf(p) == FileKind.Pdf);
        if (pdf is not null) SetPdf(pdf);
        var n = 0;
        foreach (var p in all.Where(p => FileTypes.KindOf(p) == FileKind.Image))
        {
            if (_photos.Any(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            _photos.Add(new PhotoEntry(p));
            n++;
        }
        if (n > 0) _main.Toast($"{n} foto(s) na lista para juntar.");
        UpdateJoin();
    }

    private void OnPhotoDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
        e.Handled = true;
    }

    private void OnAddPhotos(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Escolha as fotos",
            Filter = "Fotos|" + string.Join(";", FileTypes.ImageExt.Select(x => "*." + x)),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddFiles(dlg.FileNames);
    }

    private void OnUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int dir)
    {
        var sel = PhotoList.SelectedItems.Cast<PhotoEntry>().ToList();
        if (sel.Count == 0) return;
        var idx = sel.Select(p => _photos.IndexOf(p)).OrderBy(i => i).ToList();
        if (dir > 0) idx.Reverse();
        foreach (var i in idx)
        {
            var j = i + dir;
            if (j < 0 || j >= _photos.Count || sel.Contains(_photos[j])) continue;
            _photos.Move(i, j);
        }
        PhotoList.SelectedItems.Clear();
        foreach (var p in sel) PhotoList.SelectedItems.Add(p);
    }

    private void OnSortName(object sender, RoutedEventArgs e) => Resort(_photos.OrderBy(p => System.IO.Path.GetFileName(p.Path), StringComparer.CurrentCultureIgnoreCase));

    private void OnSortDate(object sender, RoutedEventArgs e) => Resort(_photos.OrderBy(p => SafeDate(p.Path)));

    private static DateTime SafeDate(string p)
    {
        try { return File.GetLastWriteTime(p); } catch { return DateTime.MaxValue; }
    }

    private void Resort(IEnumerable<PhotoEntry> order)
    {
        var list = order.ToList();
        _photos.Clear();
        foreach (var p in list) _photos.Add(p);
    }

    private void OnRemovePhotos(object sender, RoutedEventArgs e)
    {
        foreach (var p in PhotoList.SelectedItems.Cast<PhotoEntry>().ToList()) _photos.Remove(p);
        UpdateJoin();
    }

    private void UpdateJoin()
    {
        JoinBtn.IsEnabled = _photos.Count > 0 && !_busy;
        if (!_busy) JoinStatus.Text = _photos.Count == 0 ? "Adicione as fotos na ordem das páginas." : $"{_photos.Count} foto(s) = {_photos.Count} página(s).";
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (_photos.Count == 0) return;
        var first = _photos[0].Path;
        var dlg = new SaveFileDialog
        {
            Title = "Salvar o PDF",
            Filter = "PDF|*.pdf",
            FileName = "Fotos juntas.pdf",
            InitialDirectory = System.IO.Path.GetDirectoryName(first),
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var target = dlg.FileName;
        // O Windows já perguntou se pode substituir; o motor nunca sobrescreve, então liberamos o nome.
        if (File.Exists(target))
        {
            try { File.Delete(target); }
            catch (Exception ex) { _main.Toast("Não consegui substituir o arquivo: " + ex.Message); return; }
        }
        await JoinToAsync(target);
    }

    /// <summary>Também usado pelo teste automático.</summary>
    public async Task<string?> JoinToAsync(string target)
    {
        _busy = true;
        JoinBtn.IsEnabled = false;
        JoinProgress.Visibility = Visibility.Visible;
        JoinProgress.Value = 0;
        JoinStatus.Text = "Montando o PDF…";
        try
        {
            var level = PdfMax.IsChecked == true ? Level.Maxima : PdfMin.IsChecked == true ? Level.Menor : Level.Equilibrado;
            var a4 = PageA4.IsChecked == true;
            var files = _photos.Select(p => p.Path).ToList();
            var tools = new PdfTools(_s.Tools);
            var progress = new Progress<double>(v => JoinProgress.Value = v);
            var path = await Task.Run(() => tools.ImagesToPdfAsync(files, target, a4, level, v => ((IProgress<double>)progress).Report(v), CancellationToken.None));
            JoinStatus.Text = $"Pronto: {System.IO.Path.GetFileName(path)} ({Format.Bytes(new FileInfo(path).Length)}, {files.Count} página(s)).";
            Log.Info($"Fotos juntas em PDF: {path}");
            Explorer.ShowInFolder(path);
            return path;
        }
        catch (Exception ex)
        {
            JoinStatus.Text = JobQueue.Friendly(ex);
            Log.Warn("Juntar fotos falhou: " + ex.Message);
            return null;
        }
        finally
        {
            _busy = false;
            JoinProgress.Visibility = Visibility.Collapsed;
            JoinBtn.IsEnabled = _photos.Count > 0;
        }
    }

    // ---------------- separar ----------------

    private void SetPdf(string path)
    {
        _pdf = path;
        PdfName.Text = path;
        PdfName.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        SplitStatus.Text = "";
        SplitFolderBtn.Visibility = Visibility.Collapsed;
    }

    private void OnPickPdf(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Escolha o PDF", Filter = "PDF|*.pdf" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) SetPdf(dlg.FileName);
    }

    private void OnRangesFocus(object sender, RoutedEventArgs e) => SplitRanges.IsChecked = true;

    private async void OnSplit(object sender, RoutedEventArgs e) => await SplitAsync(SplitEach.IsChecked == true ? null : RangesText.Text);

    /// <summary>Também usado pelo teste automático. <paramref name="ranges"/> null = uma página por arquivo.</summary>
    public async Task<List<string>> SplitAsync(string? ranges, string? pdf = null)
    {
        if (pdf is not null) SetPdf(pdf);
        if (_pdf is null)
        {
            _main.Toast("Escolha um PDF primeiro.");
            return [];
        }
        SplitBtn.IsEnabled = false;
        SplitStatus.Text = "Separando…";
        try
        {
            var tools = new PdfTools(_s.Tools);
            var src = _pdf;
            List<PdfTools.PageRange>? list = null;
            if (ranges is not null)
            {
                var total = await Task.Run(() => tools.PageCountAsync(src, CancellationToken.None));
                list = PdfTools.ParseRanges(ranges, total);
            }
            var dest = System.IO.Path.GetDirectoryName(src)!;
            var created = await Task.Run(() => tools.SplitAsync(src, list, dest, null, CancellationToken.None));
            _lastSplitDir = dest;
            SplitStatus.Text = $"Pronto: {created.Count} arquivo(s) na mesma pasta do PDF.";
            SplitFolderBtn.Visibility = Visibility.Visible;
            Log.Info($"PDF separado em {created.Count} arquivo(s): {src}");
            return created;
        }
        catch (Exception ex)
        {
            SplitStatus.Text = JobQueue.Friendly(ex);
            Log.Warn("Separar PDF falhou: " + ex.Message);
            return [];
        }
        finally
        {
            SplitBtn.IsEnabled = true;
        }
    }

    private void OnSplitFolder(object sender, RoutedEventArgs e)
    {
        if (_lastSplitDir is not null) Explorer.ShowInFolder(_lastSplitDir);
    }
}
