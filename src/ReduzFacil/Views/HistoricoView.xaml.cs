using System.Windows;
using System.Windows.Controls;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Views;

public partial class HistoricoView : UserControl
{
    private readonly AppServices _s;

    public HistoricoView(AppServices services, MainWindow main)
    {
        _s = services;
        InitializeComponent();
        _s.History.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Reload(); });
    }

    public void Reload()
    {
        var t = _s.History.Totals;
        TotSaved.Text = Format.Bytes(t.SavedBytes);
        TotFiles.Text = t.Files.ToString("N0");
        TotPct.Text = t.OriginalBytes > 0 ? $"−{Format.SavingPercent(t.OriginalBytes, t.OriginalBytes - t.SavedBytes)}%" : "—";
        HistList.ItemsSource = _s.History.Entries.Select(e => new HistoryVm(e)).ToList();
    }

    private static HistoryVm? Vm(object sender) => (sender as FrameworkElement)?.Tag as HistoryVm;

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Vm(sender)?.Entry.Output is { } p) Explorer.Open(p);
    }

    private void OnFolder(object sender, RoutedEventArgs e)
    {
        if (Vm(sender)?.Entry.Output is { } p) Explorer.ShowInFolder(p);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Apagar a lista do histórico? Os arquivos não são apagados e o total economizado continua.",
                "Reduz Fácil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _s.History.Clear();
        Reload();
    }
}
