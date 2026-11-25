using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Settings;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Views;

public partial class ReduzirView : UserControl
{
    private readonly AppServices _s;
    private readonly MainWindow _main;
    private readonly ObservableCollection<QueueItemVm> _items = [];
    private readonly Dictionary<long, QueueItemVm> _byId = [];
    private bool _loading;

    public ObservableCollection<QueueItemVm> Items => _items;

    public ReduzirView(AppServices services, MainWindow main)
    {
        _s = services;
        _main = main;
        InitializeComponent();
        foreach (var (mb, label) in Preset.DiscordPlans) DiscordBox2.Items.Add(new ComboBoxItem { Content = label, Tag = mb });
        QueueList.ItemsSource = _items;
        LoadFromSettings();

        _s.Queue.ListChanged += () => Dispatcher.BeginInvoke(SyncList);
        _s.Queue.ItemChanged += i => Dispatcher.BeginInvoke(() => OnItemChanged(i));
        Infra.ThemeManager.Changed += () => { foreach (var vm in _items) vm.Refresh(); };
        SyncList();
    }

    // ---------------- ajustes ----------------

    public void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var st = _s.Settings;
            BuildPresetChips();
            BuildProfileRow();
            var o = st.Options;
            LvlMax.IsChecked = o.Level == Level.Maxima;
            LvlEq.IsChecked = o.Level == Level.Equilibrado;
            LvlMin.IsChecked = o.Level == Level.Menor;
            MetaText.Text = Format.Number(st.CustomTargetMb);
            CodecBox.SelectedIndex = o.VideoCodec == VideoCodec.H265 ? 1 : 0;
            SelectByTag(VideoResBox, o.VideoMaxShortSide);
            HwCheck.IsChecked = o.UseHardware;
            GrayCheck.IsChecked = o.PdfGrayscale;
            AudioBox.SelectedIndex = (int)o.AudioFormat;
            ImgFmtBox.SelectedIndex = (int)o.ImageFormat;
            SelectByTag(ImgSizeBox, o.ImageMaxSide);
            PrivacyCheck.IsChecked = o.RemoveMetadata;
            AutoStartCheck.IsChecked = st.AutoStart;
            SplitCheck.IsChecked = st.SplitVideos;
            DiscordBox2.SelectedItem = DiscordBox2.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == st.DiscordMb) ?? DiscordBox2.Items[0];
            UpdatePresetUi();
        }
        finally
        {
            _loading = false;
        }
        UpdateFooter();
    }

    private static void SelectByTag(ComboBox box, int value)
    {
        foreach (var obj in box.Items)
            if (obj is ComboBoxItem it && int.TryParse(it.Tag?.ToString(), out var v) && v == value)
            {
                box.SelectedItem = it;
                return;
            }
        box.SelectedIndex = 0;
    }

    private static int TagOf(ComboBox box) =>
        box.SelectedItem is ComboBoxItem it && int.TryParse(it.Tag?.ToString(), out var v) ? v : 0;

    private void BuildPresetChips()
    {
        PresetPanel.Children.Clear();
        foreach (var p in Preset.BuiltIn)
        {
            var rb = new RadioButton
            {
                Content = p.Id == Preset.Discord ? $"{p.Label} · {_s.Settings.DiscordMb} MB"
                    : p.TargetMb is { } mb ? $"{p.Label} · {mb} MB" : p.Label,
                Style = (Style)FindResource("Chip"),
                GroupName = "preset",
                Tag = p.Id,
                ToolTip = p.Hint,
                IsChecked = p.Id == _s.Settings.PresetId,
            };
            rb.Checked += OnPreset;
            PresetPanel.Children.Add(rb);
        }
    }

    private void BuildProfileRow()
    {
        ProfileRow.Children.Clear();
        if (_s.Settings.Profiles.Count == 0)
        {
            ProfileRow.Visibility = Visibility.Collapsed;
            return;
        }
        ProfileRow.Visibility = Visibility.Visible;
        ProfileRow.Children.Add(new TextBlock { Text = "Meus perfis:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8), Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        foreach (var prof in _s.Settings.Profiles)
        {
            var b = new Button { Content = "📌 " + prof.Name, Style = (Style)FindResource("Small"), Margin = new Thickness(0, 0, 8, 8), Tag = prof, ToolTip = "Usar os ajustes deste perfil" };
            b.Click += (_, _) => ApplyProfile(prof);
            ProfileRow.Children.Add(b);
        }
    }

    private void ApplyProfile(SavedProfile p)
    {
        var st = _s.Settings;
        st.Options = p.Options.Clone();
        st.PresetId = p.PresetId;
        if (p.TargetMb is { } mb && mb > 0) st.CustomTargetMb = mb;
        _s.Save();
        LoadFromSettings();
        _main.Toast($"Perfil \"{p.Name}\" aplicado.");
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string id }) return;
        _s.Settings.PresetId = id;
        _s.Save();
        UpdatePresetUi();
    }

    private void UpdatePresetUi()
    {
        var p = Preset.Find(_s.Settings.PresetId) ?? Preset.BuiltIn[0];
        PresetHint.Text = p.Hint + (p.TargetMb is not null ? ". Arquivos que já cabem são só otimizados." : "");
        MetaBox.Visibility = p.Id == Preset.Meta ? Visibility.Visible : Visibility.Collapsed;
        DiscordBox.Visibility = p.Id == Preset.Discord ? Visibility.Visible : Visibility.Collapsed;
        SplitCheck.Visibility = p.TargetMb is not null || p.Id == Preset.Meta ? Visibility.Visible : Visibility.Collapsed;
        LevelRow.Visibility = p.ConvertOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDiscordPlan(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DiscordBox2.SelectedItem is not ComboBoxItem { Tag: int mb }) return;
        _s.Settings.DiscordMb = mb;
        _s.Save();
        foreach (var rb in PresetPanel.Children.OfType<RadioButton>())
            if (rb.Tag as string == Preset.Discord) rb.Content = $"Discord · {mb} MB";
    }

    private void OnSplit(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.Settings.SplitVideos = SplitCheck.IsChecked == true;
        _s.Save();
    }

    private void OnLevel(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.Settings.Options.Level = LvlMax.IsChecked == true ? Level.Maxima : LvlMin.IsChecked == true ? Level.Menor : Level.Equilibrado;
        _s.Save();
    }

    private void OnMetaChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (Format.TryParseNumber(MetaText.Text, out var mb) && mb > 0 && mb < 100_000)
        {
            _s.Settings.CustomTargetMb = mb;
            MetaText.ClearValue(BorderBrushProperty);
            _s.Save();
        }
        else MetaText.BorderBrush = (System.Windows.Media.Brush)FindResource("Danger");
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var o = _s.Settings.Options;
        o.VideoCodec = CodecBox.SelectedIndex == 1 ? VideoCodec.H265 : VideoCodec.H264;
        o.VideoMaxShortSide = TagOf(VideoResBox);
        o.UseHardware = HwCheck.IsChecked == true;
        o.PdfGrayscale = GrayCheck.IsChecked == true;
        o.AudioFormat = (AudioFormat)Math.Max(0, AudioBox.SelectedIndex);
        o.ImageFormat = (ImageFormat)Math.Max(0, ImgFmtBox.SelectedIndex);
        o.ImageMaxSide = TagOf(ImgSizeBox);
        o.RemoveMetadata = PrivacyCheck.IsChecked == true;
        _s.Save();
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(Window.GetWindow(this), "Salvar perfil", "Nome do perfil (ex.: Fotos da família, Vídeos para o YouTube):", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        var st = _s.Settings;
        st.Profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        st.Profiles.Add(new SavedProfile
        {
            Name = name,
            PresetId = st.PresetId,
            TargetMb = st.PresetId == Preset.Meta ? st.CustomTargetMb : null,
            Options = st.Options.Clone(),
        });
        _s.Save();
        BuildProfileRow();
        _main.Toast($"Perfil \"{name}\" salvo. Ele também aparece na pasta vigiada.");
    }

    // ---------------- entrada de arquivos ----------------

    public void AddPaths(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var n = _s.Enqueue(list, _s.CurrentOptions());
        if (n == 0) _main.Toast("Nenhum arquivo suportado: fotos (JPG, PNG, HEIC, WebP…), vídeos, áudios e PDFs.");
        else if (n > 1) _main.Toast($"{n} arquivos na fila.");
    }

    private void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var exts = string.Join(";", FileTypes.AllExtensions.Select(x => "*." + x));
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Escolha os arquivos para reduzir",
            Filter = $"Fotos, vídeos, áudios e PDFs|{exts}|Fotos|{string.Join(";", FileTypes.ImageExt.Select(x => "*." + x))}|" +
                     $"Vídeos|{string.Join(";", FileTypes.VideoExt.Select(x => "*." + x))}|Áudios|{string.Join(";", FileTypes.AudioExt.Select(x => "*." + x))}|PDF|*.pdf",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddPaths(dlg.FileNames);
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Escolha a pasta (as subpastas também entram)", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddPaths(dlg.FolderNames);
    }

    // ---------------- fila ----------------

    private void SyncList()
    {
        var items = _s.Queue.Items;
        var ids = items.Select(i => i.Id).ToHashSet();
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(_items[i].Item.Id))
            {
                _byId.Remove(_items[i].Item.Id);
                _items.RemoveAt(i);
            }
        }
        foreach (var it in items)
        {
            if (_byId.ContainsKey(it.Id)) continue;
            var vm = new QueueItemVm(it);
            _byId[it.Id] = vm;
            _items.Add(vm);
        }
        UpdateFooter();
    }

    private void OnItemChanged(QueueItem i)
    {
        if (_byId.TryGetValue(i.Id, out var vm)) vm.Refresh();
        else SyncList();
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        var items = _s.Queue.Items;
        var waiting = items.Count(i => i.Status == JobStatus.Waiting);
        var running = items.Count(i => i.Status == JobStatus.Running);
        var done = items.Where(i => i.Result is not null).ToList();
        var failed = items.Count(i => i.Status == JobStatus.Failed);
        long before = done.Sum(i => i.Result!.OriginalBytes);
        long after = done.Sum(i => i.Result!.KeptOriginal ? i.Result.OriginalBytes : i.Result.FinalBytes);

        var parts = new List<string>();
        if (items.Count == 0) parts.Add("Nenhum arquivo na fila.");
        else
        {
            if (running + waiting > 0) parts.Add($"{running + waiting} para fazer");
            if (done.Count > 0) parts.Add($"{done.Count} pronto(s): {Format.Bytes(before)} → {Format.Bytes(after)} (−{Format.SavingPercent(before, after)}%)");
            if (failed > 0) parts.Add($"{failed} com erro");
        }
        if (_s.Queue.IsPaused) parts.Add("⏸ pausado");
        Summary.Text = string.Join(" · ", parts);

        PauseBtn.Content = _s.Queue.IsPaused ? "Continuar" : "Pausar";
        PauseBtn.IsEnabled = running + waiting > 0 || _s.Queue.IsPaused;
        StartBtn.Visibility = _s.Queue.HasUnreleased ? Visibility.Visible : Visibility.Collapsed;
        CancelAllBtn.IsEnabled = running + waiting > 0;
        ClearBtn.IsEnabled = items.Any(i => i.IsFinished);
        DropSub.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static QueueItemVm? Vm(object sender) => (sender as FrameworkElement)?.Tag as QueueItemVm;

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Vm(sender)?.Item.Result?.OutputPath is { } p) Explorer.Open(p);
    }

    private void OnFolder(object sender, RoutedEventArgs e)
    {
        if (Vm(sender)?.Item.Result?.OutputPath is { } p) Explorer.ShowInFolder(p);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Vm(sender)?.Item.Result is not { } r) return;
        var files = r.AllOutputs.ToList();
        _main.Toast(MainWindow.CopyFiles(files)
            ? files.Count == 1 ? "Copiado! É só colar (Ctrl+V) no WhatsApp, e-mail ou onde quiser." : $"{files.Count} partes copiadas! É só colar."
            : "Não consegui copiar (o arquivo sumiu?).");
    }

    private void OnCompare(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is not { } vm) return;
        new CompareWindow(_s, vm.Item.Path, vm.Item.Options, vm.Item.Result?.OutputPath) { Owner = Window.GetWindow(this) }.Show();
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is not { } vm) return;
        var dlg = new VideoEditWindow(_s, vm.Item) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result is { } o)
        {
            if (!_s.Queue.UpdateOptions(vm.Item, o)) _main.Toast("Este vídeo já começou; os ajustes não foram aplicados.");
        }
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } vm) _s.Queue.Retry(vm.Item);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } vm) _s.Queue.Remove(vm.Item);
    }

    private void OnAutoStart(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.Settings.AutoStart = AutoStartCheck.IsChecked == true;
        _s.ApplyQueueSettings();
        _s.Save();
        if (_s.Settings.AutoStart) _s.Queue.StartAll();
        UpdateFooter();
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        _s.Queue.StartAll();
        UpdateFooter();
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (_s.Queue.IsPaused) _s.Queue.ResumeAll();
        else _s.Queue.PauseAll();
        UpdateFooter();
    }

    private void OnCancelAll(object sender, RoutedEventArgs e)
    {
        var n = _s.Queue.Items.Count(i => !i.IsFinished);
        if (n == 0) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Cancelar os {n} arquivo(s) que ainda não terminaram?", "Reduz Fácil",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _s.Queue.CancelAll();
    }

    private void OnClear(object sender, RoutedEventArgs e) => _s.Queue.ClearFinished();
}

/// <summary>Abrir arquivo/pasta no Windows.</summary>
public static class Explorer
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Não consegui abrir " + path + ": " + ex.Message); }
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Warn("Não consegui abrir a pasta: " + ex.Message);
        }
    }
}
