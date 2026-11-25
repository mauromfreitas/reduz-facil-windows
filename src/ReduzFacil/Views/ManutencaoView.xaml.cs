using System.Windows;
using System.Windows.Controls;
using ReduzFacil.Core.Maintenance;
using ReduzFacil.Core.Util;
using ReduzFacil.Infra;

namespace ReduzFacil.Views;

/// <summary>Manutenção: programas (winget), drivers (Windows Update), limpeza, inicialização e atalhos.</summary>
public partial class ManutencaoView : UserControl
{
    private sealed class Row<T>(T item, CheckBox box, TextBlock status)
    {
        public T Item { get; } = item;
        public CheckBox Box { get; } = box;
        public TextBlock Status { get; } = status;
        public long Bytes { get; set; }
    }

    private static readonly TimeSpan Cache = TimeSpan.FromMinutes(10);

    private readonly AppServices _s;
    private readonly MainWindow _main;
    private readonly List<Row<AppUpgrade>> _apps = [];
    private readonly List<Row<DriverUpdate>> _drivers = [];
    private readonly List<Row<CleanTarget?>> _clean = [];
    private DateTime _appsAt = DateTime.MinValue, _drvAt = DateTime.MinValue;
    private bool _appsBusy, _drvBusy, _cleanBusy, _startupLoading;
    private string? _winget;

    /// <summary>Para o teste automático esperar as buscas.</summary>
    public Task AppsTask { get; private set; } = Task.CompletedTask;
    public Task DriversTask { get; private set; } = Task.CompletedTask;
    public Task CleanTask { get; private set; } = Task.CompletedTask;

    public ManutencaoView(AppServices services, MainWindow main)
    {
        _s = services;
        _main = main;
        InitializeComponent();
    }

    /// <summary>Ao abrir a página: procura atualizações (no máximo a cada 10 min), mede a limpeza e lista a inicialização.</summary>
    public void Reload()
    {
        if (!_appsBusy && DateTime.Now - _appsAt > Cache) AppsTask = CheckAppsAsync();
        if (!_drvBusy && DateTime.Now - _drvAt > Cache) DriversTask = CheckDriversAsync();
        if (!_cleanBusy) CleanTask = MeasureCleanAsync();
        LoadStartup();
    }

    // ---------------- linhas ----------------

    private static TextBlock Txt(string text, string brush = "Text", double size = 14, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static void SetStatus(TextBlock t, string text, string brush)
    {
        t.Text = text;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    /// <summary>Linha: [caixa] título / detalhe ......... coluna da direita / situação</summary>
    private static (Border Row, CheckBox Box, TextBlock Right, TextBlock Status) MakeRow(string title, string detail, string right, bool check = true)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        var box = new CheckBox { IsChecked = check, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(box);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Txt(title, bold: true));
        if (!string.IsNullOrWhiteSpace(detail)) text.Children.Add(Txt(detail, "Muted", 12));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var r = Txt(right, "Text", 13);
        r.Margin = new Thickness(12, 0, 12, 0);
        r.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(r, 2);
        g.Children.Add(r);
        var status = Txt("", "Muted", 13);
        Grid.SetColumn(status, 3);
        g.Children.Add(status);
        var b = new Border { Child = g, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 2, 0, 2) };
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return (b, box, r, status);
    }

    // ---------------- programas ----------------

    private void OnAppsCheck(object sender, RoutedEventArgs e)
    {
        if (!_appsBusy) AppsTask = CheckAppsAsync();
    }

    private async Task CheckAppsAsync()
    {
        _appsBusy = true;
        AppsCheckBtn.IsEnabled = false;
        AppsButtons.Visibility = Visibility.Collapsed;
        AppsList.Children.Clear();
        _apps.Clear();
        AppsStatus.Text = "Procurando versões novas dos seus programas…";
        try
        {
            _winget ??= Winget.FindExe();
            if (_winget is null)
            {
                AppsStatus.Text = "O instalador de programas do Windows (winget) não foi encontrado. Instale o \"Instalador de Aplicativo\" pela Microsoft Store e volte aqui.";
                var store = Ui.Btn("Abrir na Microsoft Store");
                store.HorizontalAlignment = HorizontalAlignment.Left;
                store.Margin = new Thickness(0, 8, 0, 0);
                store.Click += (_, _) => Explorer.Open("ms-windows-store://pdp/?productid=9NBLGGH4NNS1");
                AppsList.Children.Add(store);
                return;
            }
            var list = await Winget.ListAsync(_winget, CancellationToken.None);
            _appsAt = DateTime.Now;
            if (list.Count == 0)
            {
                SetStatus(AppsStatus, "✔  Todos os programas estão atualizados.", "AccentDark");
                return;
            }
            SetStatus(AppsStatus, $"{list.Count} programa(s) com versão nova.", "Muted");
            foreach (var a in list)
            {
                var (row, box, _, status) = MakeRow(a.Name, a.Id, $"{a.Current}  →  {a.Available}");
                AppsList.Children.Add(row);
                _apps.Add(new Row<AppUpgrade>(a, box, status));
            }
            AppsButtons.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Warn("Procurar programas: " + ex.Message);
            SetStatus(AppsStatus, "Não consegui procurar atualizações: " + ex.Message, "Danger");
        }
        finally
        {
            _appsBusy = false;
            AppsCheckBtn.IsEnabled = true;
        }
    }

    private void OnAppsAll(object sender, RoutedEventArgs e) => _ = UpdateAppsAsync(_apps.Where(r => r.Box.IsEnabled).ToList());
    private void OnAppsSelected(object sender, RoutedEventArgs e) => _ = UpdateAppsAsync(_apps.Where(r => r.Box.IsEnabled && r.Box.IsChecked == true).ToList());

    private async Task UpdateAppsAsync(List<Row<AppUpgrade>> rows)
    {
        if (_appsBusy || _winget is null) return;
        if (rows.Count == 0)
        {
            _main.Toast("Marque pelo menos um programa.");
            return;
        }
        _appsBusy = true;
        AppsCheckBtn.IsEnabled = AppsAllBtn.IsEnabled = AppsSelBtn.IsEnabled = false;
        int ok = 0, fail = 0;
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                SetStatus(r.Status, "Atualizando…", "Text");
                SetStatus(AppsStatus, $"Atualizando {i + 1} de {rows.Count}: {r.Item.Name}… Se o Windows pedir permissão, clique em Sim.", "Muted");
                int code;
                try { code = await Winget.UpgradeAsync(_winget, r.Item.Id, CancellationToken.None); }
                catch (Exception ex) { Log.Warn("winget: " + ex.Message); code = -1; }
                if (code == 0 || WingetParser.IsNothingToDo(code))
                {
                    ok++;
                    SetStatus(r.Status, "✔  Atualizado", "AccentDark");
                    r.Box.IsChecked = false;
                    r.Box.IsEnabled = false;
                }
                else
                {
                    fail++;
                    SetStatus(r.Status, $"✖  Não deu (0x{code:X8})", "Danger");
                }
            }
            SetStatus(AppsStatus, fail == 0
                ? $"✔  {ok} programa(s) atualizado(s)."
                : $"{ok} atualizado(s), {fail} não deu. Alguns programas só atualizam fechados: feche-os e tente de novo.",
                fail == 0 ? "AccentDark" : "Warn");
        }
        finally
        {
            _appsBusy = false;
            AppsCheckBtn.IsEnabled = AppsAllBtn.IsEnabled = AppsSelBtn.IsEnabled = true;
            _appsAt = DateTime.MinValue; // na próxima visita procura de novo
        }
    }

    // ---------------- drivers ----------------

    private void OnDrvCheck(object sender, RoutedEventArgs e)
    {
        if (!_drvBusy) DriversTask = CheckDriversAsync();
    }

    private async Task CheckDriversAsync()
    {
        _drvBusy = true;
        DrvCheckBtn.IsEnabled = false;
        DrvButtons.Visibility = Visibility.Collapsed;
        DrvList.Children.Clear();
        _drivers.Clear();
        SetStatus(DrvStatus, "Procurando drivers novos no Windows Update… (pode levar alguns minutos)", "Muted");
        try
        {
            var list = await WindowsUpdateDrivers.SearchAsync();
            _drvAt = DateTime.Now;
            if (list.Count == 0)
            {
                SetStatus(DrvStatus, "✔  Nenhum driver novo no Windows Update.", "AccentDark");
                return;
            }
            SetStatus(DrvStatus, $"{list.Count} driver(s) novo(s).", "Muted");
            foreach (var d in list)
            {
                var detail = string.Join(" · ", new[] { d.Provider, d.Class, d.Date?.ToString("dd/MM/yyyy") }.Where(x => !string.IsNullOrWhiteSpace(x)));
                var (row, box, _, status) = MakeRow(d.Title, detail, d.Size > 0 ? Format.Bytes(d.Size) : "");
                DrvList.Children.Add(row);
                _drivers.Add(new Row<DriverUpdate>(d, box, status));
            }
            DrvButtons.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            var msg = (ex as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            Log.Warn("Procurar drivers: " + msg);
            SetStatus(DrvStatus, "Não consegui falar com o Windows Update agora. Tente de novo mais tarde ou use o atalho do Windows Update abaixo.", "Danger");
        }
        finally
        {
            _drvBusy = false;
            DrvCheckBtn.IsEnabled = true;
        }
    }

    private void OnDrvAll(object sender, RoutedEventArgs e) => _ = InstallDriversAsync(_drivers.Where(r => r.Box.IsEnabled).ToList());
    private void OnDrvSelected(object sender, RoutedEventArgs e) => _ = InstallDriversAsync(_drivers.Where(r => r.Box.IsEnabled && r.Box.IsChecked == true).ToList());

    private async Task InstallDriversAsync(List<Row<DriverUpdate>> rows)
    {
        if (_drvBusy) return;
        if (rows.Count == 0)
        {
            _main.Toast("Marque pelo menos um driver.");
            return;
        }
        _drvBusy = true;
        DrvCheckBtn.IsEnabled = DrvAllBtn.IsEnabled = DrvSelBtn.IsEnabled = false;
        foreach (var r in rows) SetStatus(r.Status, "Na fila…", "Muted");
        SetStatus(DrvStatus, "Baixando e instalando… O Windows vai pedir permissão: clique em Sim.", "Muted");
        try
        {
            var res = await AdminTasks.RunAsync(new AdminJob { Drivers = rows.Select(r => r.Item.Id).ToList() });
            if (res is null)
            {
                foreach (var r in rows) SetStatus(r.Status, "", "Muted");
                SetStatus(DrvStatus, "Instalação cancelada (a permissão não foi dada).", "Warn");
                return;
            }
            int ok = 0, fail = 0;
            foreach (var r in rows)
            {
                if (res.DriverCodes.TryGetValue(r.Item.Id, out var c) && c is 2 or 3)
                {
                    ok++;
                    SetStatus(r.Status, "✔  Instalado", "AccentDark");
                    r.Box.IsChecked = false;
                    r.Box.IsEnabled = false;
                }
                else
                {
                    fail++;
                    SetStatus(r.Status, "✖  Não deu", "Danger");
                }
            }
            var text = fail == 0 ? $"✔  {ok} driver(s) instalado(s)." : $"{ok} instalado(s), {fail} não deu.";
            if (res.Reboot) text += " Reinicie o computador para terminar.";
            if (res.Error is { } err) Log.Warn("Drivers: " + err);
            SetStatus(DrvStatus, text, fail == 0 ? "AccentDark" : "Warn");
        }
        finally
        {
            _drvBusy = false;
            DrvCheckBtn.IsEnabled = DrvAllBtn.IsEnabled = DrvSelBtn.IsEnabled = true;
            _drvAt = DateTime.MinValue;
        }
    }

    // ---------------- limpeza ----------------

    private void BuildCleanRows()
    {
        if (_clean.Count > 0) return;
        var (bin, binBox, _, binStatus) = MakeRow("Lixeira", "Arquivos que você já apagou. Depois de esvaziar, não dá para recuperar.", "");
        CleanList.Children.Add(bin);
        _clean.Add(new Row<CleanTarget?>(null, binBox, binStatus));
        foreach (var t in CleanCatalog.All)
        {
            var (row, box, right, status) = MakeRow(t.Title, t.Description, t.NeedsAdmin ? "🛡 pede permissão" : "");
            right.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            right.FontSize = 12;
            CleanList.Children.Add(row);
            _clean.Add(new Row<CleanTarget?>(t, box, status));
        }
    }

    private async Task MeasureCleanAsync()
    {
        _cleanBusy = true;
        BuildCleanRows();
        CleanBtn.IsEnabled = false;
        SetStatus(CleanStatus, "Calculando quanto dá para liberar…", "Muted");
        try
        {
            foreach (var r in _clean) SetStatus(r.Status, "calculando…", "Muted");
            var tasks = _clean.Select(async r =>
            {
                if (r.Item is null)
                {
                    var (bytes, items) = await Task.Run(() => RecycleBin.Query());
                    r.Bytes = bytes;
                    SetStatus(r.Status, items == 0 ? "vazia" : $"{Format.Bytes(bytes)} ({items:N0} itens)", "Text");
                    return;
                }
                var size = await Task.Run(() => Cleaner.Measure(r.Item));
                r.Bytes = size.Bytes;
                SetStatus(r.Status,
                    size.Bytes > 0 ? Format.Bytes(size.Bytes) : size.Denied ? "só com permissão" : "nada",
                    size.Bytes > 0 ? "Text" : "Muted");
            });
            await Task.WhenAll(tasks);
            var total = _clean.Sum(r => r.Bytes);
            SetStatus(CleanStatus, total > 0 ? $"Dá para liberar cerca de {Format.Bytes(total)}." : "Está tudo limpo.", "Muted");
        }
        catch (Exception ex)
        {
            Log.Warn("Medir limpeza: " + ex.Message);
            SetStatus(CleanStatus, "Não consegui calcular tudo: " + ex.Message, "Warn");
        }
        finally
        {
            _cleanBusy = false;
            CleanBtn.IsEnabled = true;
        }
    }

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        if (_cleanBusy) return;
        var sel = _clean.Where(r => r.Box.IsChecked == true).ToList();
        if (sel.Count == 0)
        {
            _main.Toast("Marque pelo menos um item.");
            return;
        }
        var total = sel.Sum(r => r.Bytes);
        var msg = $"Apagar de vez {(total > 0 ? "cerca de " + Format.Bytes(total) : "os itens marcados")}?\n\n" +
                  string.Join("\n", sel.Select(r => "• " + (r.Item?.Title ?? "Lixeira"))) +
                  (sel.Any(r => r.Item is null) ? "\n\nO que está na Lixeira não poderá ser recuperado." : "") +
                  (sel.Any(r => r.Item?.NeedsAdmin == true) ? "\n\nO Windows vai pedir permissão para os itens do sistema." : "");
        if (MessageBox.Show(Window.GetWindow(this), msg, "Limpeza", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _cleanBusy = true;
        CleanBtn.IsEnabled = false;
        SetStatus(CleanStatus, "Limpando…", "Muted");
        long freed = 0;
        int skipped = 0;
        var adminCanceled = false;
        try
        {
            foreach (var r in sel.Where(r => r.Item is null || !r.Item.NeedsAdmin))
            {
                SetStatus(r.Status, "limpando…", "Muted");
                if (r.Item is null)
                {
                    var before = r.Bytes;
                    await Task.Run(() => RecycleBin.Empty());
                    freed += before;
                }
                else
                {
                    var res = await Task.Run(() => Cleaner.Clean(r.Item));
                    freed += res.Freed;
                    skipped += res.Skipped;
                }
            }
            var admin = sel.Where(r => r.Item?.NeedsAdmin == true).ToList();
            if (admin.Count > 0)
            {
                foreach (var r in admin) SetStatus(r.Status, "limpando…", "Muted");
                var res = await AdminTasks.RunAsync(new AdminJob { Clean = admin.Select(r => r.Item!.Id).ToList() });
                if (res is null) adminCanceled = true;
                else
                {
                    freed += res.Freed;
                    skipped += res.Skipped;
                    if (res.Error is { } err) Log.Warn("Limpeza (admin): " + err);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Limpeza: " + ex.Message);
        }
        finally
        {
            _cleanBusy = false;
        }

        await MeasureCleanAsync();
        var text = $"✔  Liberados {Format.Bytes(freed)}.";
        if (skipped > 0) text += $" {skipped:N0} arquivo(s) em uso ficaram (para limpar o cache dos navegadores, feche-os antes).";
        if (adminCanceled) text += " Os itens do sistema ficaram de fora porque a permissão não foi dada.";
        SetStatus(CleanStatus, text, "AccentDark");
        if (freed > 0) _main.Toast($"Liberados {Format.Bytes(freed)}.");
    }

    // ---------------- inicialização ----------------

    private void LoadStartup()
    {
        _startupLoading = true;
        StartupList.Children.Clear();
        List<StartupApp> apps;
        try { apps = StartupApps.List(); }
        catch (Exception ex)
        {
            Log.Warn("Inicialização: " + ex.Message);
            apps = [];
        }
        if (apps.Count == 0) StartupList.Children.Add(Txt("Nenhum programa encontrado.", "Muted", 13));
        foreach (var a in apps)
        {
            var cmd = a.Command.Length > 90 ? a.Command[..87] + "…" : a.Command;
            var (row, box, right, status) = MakeRow(a.DisplayName, cmd, a.NeedsAdmin ? "🛡 todos os usuários" : "", a.Enabled);
            right.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            right.FontSize = 12;
            SetStatus(status, a.Enabled ? "Abre com o Windows" : "Desligado", a.Enabled ? "Text" : "Muted");
            var app = a;
            box.Checked += (_, _) => _ = ToggleStartupAsync(app, box, status, true);
            box.Unchecked += (_, _) => _ = ToggleStartupAsync(app, box, status, false);
            StartupList.Children.Add(row);
        }
        _startupLoading = false;
    }

    private async Task ToggleStartupAsync(StartupApp app, CheckBox box, TextBlock status, bool enable)
    {
        if (_startupLoading) return;
        var ok = true;
        try
        {
            if (app.NeedsAdmin && !AdminTasks.IsElevated)
            {
                box.IsEnabled = false;
                var res = await AdminTasks.RunAsync(new AdminJob { Startup = [new StartupChange(app.Source, app.Name, enable)] });
                ok = res is { Error: null };
            }
            else
            {
                StartupApps.SetEnabled(app.Source, app.Name, enable);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Inicialização: " + ex.Message);
            ok = false;
        }
        box.IsEnabled = true;
        if (!ok)
        {
            // volta a caixa sem disparar de novo
            _startupLoading = true;
            box.IsChecked = !enable;
            _startupLoading = false;
            _main.Toast("Não deu para mudar " + app.DisplayName + ".");
            return;
        }
        SetStatus(status, enable ? "Abre com o Windows" : "Desligado", enable ? "Text" : "Muted");
        _main.Toast(enable ? $"{app.DisplayName} volta a abrir com o Windows." : $"{app.DisplayName} não abre mais com o Windows.");
    }

    // ---------------- atalhos ----------------

    private void OnShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string target }) Explorer.Open(target);
    }
}
