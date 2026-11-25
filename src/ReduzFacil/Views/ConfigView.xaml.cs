using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Output;
using ReduzFacil.Core.Util;
using ReduzFacil.Infra;

namespace ReduzFacil.Views;

public partial class ConfigView : UserControl
{
    private readonly AppServices _s;
    private readonly MainWindow _main;
    private bool _loading;

    public ConfigView(AppServices services, MainWindow main)
    {
        _s = services;
        _main = main;
        InitializeComponent();
        for (var i = 1; i <= 4; i++) VideosBox.Items.Add(i.ToString());
        for (var i = 1; i <= 8; i++) OthersBox.Items.Add(i.ToString());
        _s.WatchStateChanged += () => Dispatcher.BeginInvoke(UpdateWatchStatus);
        Reload();
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            var st = _s.Settings;
            OutNext.IsChecked = st.Output.Mode == OutputMode.NextToOriginal;
            OutFolder.IsChecked = st.Output.Mode == OutputMode.Folder;
            OutFolderText.Text = st.Output.Folder ?? "";
            SuffixText.Text = st.Output.Suffix;
            KeepDatesCheck.IsChecked = st.Output.KeepDates;
            UpdateSuffixExample();

            VideosBox.SelectedIndex = Math.Clamp(st.VideosAtOnce, 1, 4) - 1;
            OthersBox.SelectedIndex = Math.Clamp(st.OthersAtOnce, 1, 8) - 1;
            UpdateHw();

            ShellCheck.IsChecked = st.ShellMenu;
            StartupCheck.IsChecked = st.StartWithWindows;
            TrayCheck.IsChecked = st.TrayOnClose;
            NotifyCheck.IsChecked = st.NotifyWhenDone;

            WatchCheck.IsChecked = st.Watch.Enabled;
            WatchFolderText.Text = st.Watch.Folder ?? "";
            WatchSubCheck.IsChecked = st.Watch.IncludeSubfolders;
            WatchProfileBox.Items.Clear();
            foreach (var p in Preset.BuiltIn.Where(p => p.Id != Preset.Meta))
                WatchProfileBox.Items.Add(new ComboBoxItem { Content = p.TargetMb is { } mb ? $"{p.Label} ({mb} MB)" : p.Label, Tag = p.Id });
            foreach (var prof in st.Profiles)
                WatchProfileBox.Items.Add(new ComboBoxItem { Content = "📌 " + prof.Name, Tag = prof.Name });
            WatchProfileBox.SelectedItem = WatchProfileBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, st.Watch.Profile, StringComparison.OrdinalIgnoreCase))
                ?? WatchProfileBox.Items[0];
            UpdateWatchStatus();

            BuildProfiles();
            AboutText.Text = $"Reduz Fácil para Windows, versão {AppInfo.Version}.\n" +
                             $"Motores: FFmpeg (vídeo e áudio), ImageMagick (fotos) e Ghostscript (PDF), em {_s.Data.EngineDir}.\n" +
                             "Tudo roda neste computador; nenhum arquivo é enviado para a internet.";
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateHw()
    {
        if (!_s.Hardware.Detected)
        {
            HwText.Text = "Verificando…";
            _ = _s.Hardware.DetectAsync(_s.Tools).ContinueWith(_ => Dispatcher.BeginInvoke(UpdateHw));
            return;
        }
        var w = _s.Hardware.Working;
        HwText.Text = w.Count == 0
            ? "Nenhuma disponível: os vídeos são feitos pelo processador (funciona, só demora mais)."
            : string.Join(", ", w.Select(e => e.Label)) + " — os vídeos usam a placa automaticamente.";
    }

    private async void OnRedetect(object sender, RoutedEventArgs e)
    {
        HwText.Text = "Verificando…";
        await _s.Hardware.RedetectAsync(_s.Tools);
        UpdateHw();
    }

    private void UpdateSuffixExample()
    {
        var s = SuffixText.Text.Trim();
        if (s.Length == 0 && OutNext.IsChecked == true) s = "-reduzido";
        SuffixExample.Text = $"ex.: foto{s}.jpg";
    }

    private void OnOutput(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var o = _s.Settings.Output;
        if (OutFolder.IsChecked == true && string.IsNullOrWhiteSpace(OutFolderText.Text))
        {
            // Pasta ainda não escolhida: pede agora.
            if (!PickFolder(OutFolderText, "Onde salvar os arquivos reduzidos?"))
            {
                _loading = true;
                OutNext.IsChecked = true;
                _loading = false;
            }
        }
        o.Mode = OutFolder.IsChecked == true && !string.IsNullOrWhiteSpace(OutFolderText.Text) ? OutputMode.Folder : OutputMode.NextToOriginal;
        o.Folder = string.IsNullOrWhiteSpace(OutFolderText.Text) ? null : OutFolderText.Text.Trim();
        var suffix = SuffixText.Text.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) suffix = suffix.Replace(c.ToString(), "");
        o.Suffix = suffix;
        o.KeepDates = KeepDatesCheck.IsChecked == true;
        UpdateSuffixExample();
        _s.Save();
        _s.RestartWatcher(); // a pasta de saída pode ter entrado/saído da pasta vigiada
    }

    private void OnPickOutput(object sender, RoutedEventArgs e)
    {
        if (!PickFolder(OutFolderText, "Onde salvar os arquivos reduzidos?")) return;
        OutFolder.IsChecked = true;
        OnOutput(sender, e);
    }

    private bool PickFolder(TextBox target, string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (Directory.Exists(target.Text)) dlg.InitialDirectory = target.Text;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return false;
        target.Text = dlg.FolderName;
        return true;
    }

    private void OnPerf(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _s.Settings.VideosAtOnce = VideosBox.SelectedIndex + 1;
        _s.Settings.OthersAtOnce = OthersBox.SelectedIndex + 1;
        _s.ApplyQueueSettings();
        _s.Save();
        _s.Queue.Schedule();
    }

    private void OnWindows(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var st = _s.Settings;
        try
        {
            if (ShellCheck.IsChecked == true != st.ShellMenu)
            {
                if (ShellCheck.IsChecked == true) WindowsIntegration.InstallShellMenu();
                else WindowsIntegration.RemoveShellMenu();
                st.ShellMenu = ShellCheck.IsChecked == true;
                _main.Toast(st.ShellMenu ? "Pronto! Clique com o botão direito numa foto, vídeo, PDF ou pasta." : "Menu do botão direito removido.");
            }
            if (StartupCheck.IsChecked == true != st.StartWithWindows)
            {
                WindowsIntegration.SetStartWithWindows(StartupCheck.IsChecked == true);
                st.StartWithWindows = StartupCheck.IsChecked == true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Integração com o Windows falhou", ex);
            MessageBox.Show(Window.GetWindow(this), "Não consegui mudar essa opção no Windows: " + ex.Message, "Reduz Fácil", MessageBoxButton.OK, MessageBoxImage.Warning);
            Reload();
            return;
        }
        st.TrayOnClose = TrayCheck.IsChecked == true;
        st.NotifyWhenDone = NotifyCheck.IsChecked == true;
        _s.Save();
    }

    private void OnPickWatch(object sender, RoutedEventArgs e)
    {
        if (!PickFolder(WatchFolderText, "Qual pasta vigiar?")) return;
        WatchCheck.IsChecked = true;
        OnWatch(sender, e);
    }

    private void OnWatch(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var w = _s.Settings.Watch;
        var folder = WatchFolderText.Text.Trim().Trim('"');
        if (WatchCheck.IsChecked == true && string.IsNullOrWhiteSpace(folder))
        {
            if (!PickFolder(WatchFolderText, "Qual pasta vigiar?"))
            {
                _loading = true;
                WatchCheck.IsChecked = false;
                _loading = false;
            }
            folder = WatchFolderText.Text.Trim();
        }
        w.Enabled = WatchCheck.IsChecked == true;
        w.Folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        w.IncludeSubfolders = WatchSubCheck.IsChecked == true;
        w.Profile = (WatchProfileBox.SelectedItem as ComboBoxItem)?.Tag as string ?? Preset.Qualidade;
        _s.Save();
        _s.RestartWatcher();
    }

    private void UpdateWatchStatus()
    {
        if (_s.WatchRunning)
        {
            WatchStatus.Text = "👁  Vigiando agora. Arquivos que já estavam na pasta não entram; só os novos.";
            WatchStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentDark");
        }
        else if (_s.WatchError is { } err)
        {
            WatchStatus.Text = "⚠  " + err;
            WatchStatus.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
        }
        else
        {
            WatchStatus.Text = "Desligada.";
            WatchStatus.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
        }
    }

    private void BuildProfiles()
    {
        ProfilesPanel.Children.Clear();
        if (_s.Settings.Profiles.Count == 0)
        {
            ProfilesPanel.Children.Add(new TextBlock { Text = "Nenhum perfil salvo.", Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
            return;
        }
        foreach (var p in _s.Settings.Profiles.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var del = new Button { Content = "Excluir", Style = (Style)FindResource("DangerSmall") };
            DockPanel.SetDock(del, Dock.Right);
            del.Click += (_, _) =>
            {
                _s.Settings.Profiles.Remove(p);
                if (string.Equals(_s.Settings.Watch.Profile, p.Name, StringComparison.OrdinalIgnoreCase)) _s.Settings.Watch.Profile = Preset.Qualidade;
                _s.Save();
                Reload();
                _main.Reduzir.LoadFromSettings();
            };
            var preset = Preset.Find(p.PresetId);
            var desc = preset?.Id == Preset.Meta && p.TargetMb is { } mb ? $"meta de {Format.Number(mb)} MB" : preset?.Label ?? p.PresetId;
            row.Children.Add(del);
            row.Children.Add(new TextBlock { Text = $"📌 {p.Name} — {desc}, nível {LevelName(p.Options.Level)}", VerticalAlignment = VerticalAlignment.Center });
            ProfilesPanel.Children.Add(row);
        }
    }

    private static string LevelName(Level l) => l switch
    {
        Level.Maxima => "máxima qualidade",
        Level.Menor => "menor tamanho",
        _ => "equilibrado",
    };

    private void OnOpenData(object sender, RoutedEventArgs e) => Explorer.ShowInFolder(_s.Data.Root);

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (Log.FilePath is { } p && File.Exists(p)) Explorer.Open(p);
    }
}
