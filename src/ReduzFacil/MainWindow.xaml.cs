using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ReduzFacil.Core.Util;
using ReduzFacil.Infra;
using ReduzFacil.Views;

namespace ReduzFacil;

public partial class MainWindow : Window
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public ReduzirView Reduzir { get; }
    public PesoView Peso { get; }
    public HistoricoView Historico { get; }
    public ConfigView Config { get; }

    public MainWindow(AppServices services)
    {
        _s = services;
        InitializeComponent();
        Reduzir = new ReduzirView(services, this);
        Peso = new PesoView(services, this);
        Historico = new HistoricoView(services, this);
        Config = new ConfigView(services, this);
        Host.Content = Reduzir;
        VersionText.Text = "versão " + AppInfo.Version;

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBox.Visibility = Visibility.Collapsed;
        };

        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropOverlay.Visibility = Visibility.Visible; };
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                ShowReduzir();
                Reduzir.AddPaths(files);
            }
        };

        _s.History.Changed += () => Dispatcher.BeginInvoke(UpdateSidebar);
        _s.WatchStateChanged += () => Dispatcher.BeginInvoke(UpdateSidebar);
        UpdateSidebar();
    }

    public void ShowReduzir() => NavReduzir.IsChecked = true;
    public void ShowPage(string name)
    {
        switch (name)
        {
            case "peso": NavPeso.IsChecked = true; break;
            case "historico": NavHistorico.IsChecked = true; break;
            case "config": NavConfig.IsChecked = true; break;
            default: NavReduzir.IsChecked = true; break;
        }
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (Host is null) return; // durante o InitializeComponent
        Host.Content = sender == NavPeso ? Peso
            : sender == NavHistorico ? Historico
            : sender == NavConfig ? Config
            : Reduzir;
        if (Host.Content == Historico) Historico.Reload();
        if (Host.Content == Config) Config.Reload();
    }

    public void UpdateSidebar()
    {
        var t = _s.History.Totals;
        SavedTotal.Text = t.SavedBytes > 0 ? $"Você já economizou {Format.Bytes(t.SavedBytes)} em {t.Files:N0} arquivo(s)" : "";
        WatchBadge.Text = _s.WatchRunning
            ? "👁  Vigiando " + Path.GetFileName(_s.Settings.Watch.Folder?.TrimEnd('\\'))
            : _s.WatchError is { } err ? "⚠  Pasta vigiada: " + err : "";
    }

    public void Toast(string text)
    {
        ToastText.Text = text;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

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
