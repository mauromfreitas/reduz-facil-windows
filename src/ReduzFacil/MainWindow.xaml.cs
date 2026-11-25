using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Util;
using ReduzFacil.Infra;
using ReduzFacil.Views;

namespace ReduzFacil;

public partial class MainWindow : Window
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public HomeView Home { get; }
    public ReduzirView Reduzir { get; }
    public PesoView Peso { get; }
    public FerramentasView Ferramentas { get; }
    public HistoricoView Historico { get; }
    public ConfigView Config { get; }

    public MainWindow(AppServices services)
    {
        _s = services;
        InitializeComponent();
        Reduzir = new ReduzirView(services, this);
        Peso = new PesoView(services, this);
        Ferramentas = new FerramentasView(services, this);
        Historico = new HistoricoView(services, this);
        Config = new ConfigView(services, this);
        Home = new HomeView(services, this);
        Host.Content = Home;
        VersionText.Text = "versão " + AppInfo.Version;
        ThemeManager.TitleBar(this);

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBox.Visibility = Visibility.Collapsed;
        };

        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop) && (Host.Content == Reduzir || Host.Content == Home)) DropOverlay.Visibility = Visibility.Visible; };
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            // Na tela de Ferramentas, os arquivos vão para a ferramenta; nas outras, para a fila.
            if (Host.Content == Ferramentas)
            {
                Ferramentas.AddFiles(files);
                return;
            }
            ShowReduzir();
            Reduzir.AddPaths(files);
        };

        // Ctrl+V: cola arquivos copiados no Explorer, prints e imagens
        // (Dentro de uma caixa de texto o Ctrl+V continua colando texto: a caixa trata antes da janela.)
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, OnPaste, (_, e) => e.CanExecute = true));

        _s.History.Changed += () => Dispatcher.BeginInvoke(UpdateSidebar);
        _s.WatchStateChanged += () => Dispatcher.BeginInvoke(UpdateSidebar);
        _s.Queue.ItemFinished += i => Dispatcher.BeginInvoke(() => OnItemFinished(i));
        UpdateSidebar();
    }

    public void ShowReduzir() => NavReduzir.IsChecked = true;

    public void ShowPage(string name)
    {
        switch (name)
        {
            case "inicio": NavInicio.IsChecked = true; break;
            case "peso": NavPeso.IsChecked = true; break;
            case "ferramentas": NavFerramentas.IsChecked = true; break;
            case "historico": NavHistorico.IsChecked = true; break;
            case "config": NavConfig.IsChecked = true; break;
            default: NavReduzir.IsChecked = true; break;
        }
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (Host is null || Home is null) return; // durante o InitializeComponent e antes de criar as telas
        Host.Content = sender == NavInicio ? Home
            : sender == NavPeso ? Peso
            : sender == NavFerramentas ? Ferramentas
            : sender == NavHistorico ? Historico
            : sender == NavConfig ? Config
            : Reduzir;
        if (Host.Content == Home) Home.Reload();
        if (Host.Content == Historico) Historico.Reload();
        if (Host.Content == Config) Config.Reload();
    }

    public void UpdateSidebar()
    {
        var t = _s.History.Totals;
        SavedTotal.Text = t.SavedBytes > 0 ? $"Você já economizou {Format.Bytes(t.SavedBytes)} em {t.Files:N0} arquivo(s)" : "";
        WatchBadge.Text = _s.WatchCount switch
        {
            0 when _s.WatchError is { } err => "⚠  Pasta vigiada: " + err,
            0 => "",
            1 => "👁  Vigiando " + Path.GetFileName(_s.Settings.Watches.First(w => _s.IsWatching(w)).Folder?.TrimEnd('\\')),
            var n => $"👁  Vigiando {n} pastas",
        };
    }

    public void Toast(string text)
    {
        ToastText.Text = text;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------------- colar e copiar ----------------

    private void OnPaste(object sender, ExecutedRoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (Host.Content == Ferramentas) Ferramentas.AddFiles(files);
                else
                {
                    ShowReduzir();
                    var n = _s.Enqueue(files, _s.CurrentOptions(), JobOrigin.Paste);
                    Toast(n == 0 ? "Nada para reduzir no que foi copiado." : $"{n} arquivo(s) colado(s) na fila.");
                }
                return;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                // Print ou imagem copiada: vira um PNG em ReduzFacil-dados\colados e entra na fila.
                var dir = Path.Combine(_s.Data.Root, "colados");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"colado {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(file)) enc.Save(fs);
                ShowReduzir();
                _s.Enqueue([file], _s.CurrentOptions(), JobOrigin.Paste);
                Toast(_s.Settings.CopyPastedResult
                    ? "Imagem colada. Quando ficar pronta, ela já vai para a área de transferência: é só colar onde quiser."
                    : "Imagem colada na fila.");
                return;
            }
            Toast("Não há arquivos nem imagem copiados.");
        }
        catch (Exception ex)
        {
            Log.Warn("Colar falhou: " + ex.Message);
            Toast("Não consegui ler a área de transferência. Tente copiar de novo.");
        }
    }

    private void OnItemFinished(QueueItem i)
    {
        if (i.Origin != JobOrigin.Paste || !_s.Settings.CopyPastedResult) return;
        if (i.Result is not { } r) return;
        List<string> files = r.KeptOriginal ? new List<string> { i.Path } : r.AllOutputs.ToList();
        if (CopyFiles(files)) Toast("Pronto e copiado! É só colar (Ctrl+V) no WhatsApp, e-mail ou onde quiser.");
    }

    /// <summary>Põe arquivos na área de transferência (como "Copiar" no Explorer).</summary>
    public static bool CopyFiles(IEnumerable<string> files)
    {
        var list = new System.Collections.Specialized.StringCollection();
        foreach (var f in files.Where(File.Exists)) list.Add(f);
        if (list.Count == 0) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetFileDropList(list);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(100); // outro programa está usando a área de transferência
            }
        }
        return false;
    }

    // ---------------- atualização ----------------

    public void ShowUpdate(Core.Update.UpdateInfo info)
    {
        UpdText.Text = $"Versão {info.Version} disponível (você tem a {AppInfo.Version}).";
        UpdProgress.Visibility = Visibility.Collapsed;
        UpdNowBtn.IsEnabled = UpdLaterBtn.IsEnabled = true;
        UpdateBar.Visibility = Visibility.Visible;
    }

    private void OnUpdateNotes(object sender, RoutedEventArgs e)
    {
        if (App.Instance.Updater.Available?.PageUrl is { Length: > 0 } url)
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* sem navegador */ }
    }

    private void OnUpdateLater(object sender, RoutedEventArgs e) => UpdateBar.Visibility = Visibility.Collapsed;

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        var up = App.Instance.Updater;
        if (up.Available is null) return;
        UpdNowBtn.IsEnabled = UpdLaterBtn.IsEnabled = false;
        UpdProgress.Visibility = Visibility.Visible;
        try
        {
            UpdText.Text = $"Baixando a versão {up.Available.Version}…";
            await up.DownloadAsync(new Progress<double>(p => UpdProgress.Value = p));
            if (_s.Queue.IsBusy)
            {
                UpdText.Text = "Baixada. A atualização vai ser instalada quando a fila terminar…";
                UpdProgress.IsIndeterminate = true;
                while (_s.Queue.IsBusy) await Task.Delay(1000);
            }
            UpdText.Text = "Instalando e reabrindo…";
            AppUpdater.SwapAndRestart();
            App.Instance.ExitApp();
        }
        catch (Exception ex)
        {
            Log.Error("Atualização falhou", ex);
            UpdProgress.Visibility = Visibility.Collapsed;
            UpdProgress.IsIndeterminate = false;
            UpdText.Text = "A atualização falhou: " + ex.Message + " Tente de novo mais tarde.";
            UpdNowBtn.IsEnabled = UpdLaterBtn.IsEnabled = true;
        }
    }

    // ---------------- fechar ----------------

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (App.Instance.ShouldHideInsteadOfClose())
        {
            e.Cancel = true;
            Hide();
            App.Instance.OnWindowHidden();
            return;
        }
        var busy = _s.Queue.Items.Count(i => !i.IsFinished);
        if (busy > 0 && App.Instance.Smoke is null)
        {
            var r = MessageBox.Show(this,
                $"Ainda há {busy} arquivo(s) na fila. Se sair agora, eles não serão reduzidos (os originais continuam intactos).\n\nSair mesmo assim?",
                AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        App.Instance.ExitApp();
    }
}
