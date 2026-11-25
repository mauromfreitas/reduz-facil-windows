using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReduzFacil.Core.Compress;
using ReduzFacil.Core.Media;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Views;

/// <summary>Janelas pequenas montadas em código (sem XAML): menos coisa para dar errado.</summary>
internal static class Ui
{
    public static Window NewDialog(Window? owner, string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
            Background = (Brush)Application.Current.Resources["Bg"],
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            FontSize = 14,
        };
        return w;
    }

    public static TextBlock Text(string text, bool muted = false, double size = 14, bool bold = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = muted ? (Brush)Application.Current.Resources["Muted"] : (Brush)Application.Current.Resources["Text"],
    };

    public static Button Btn(string text, bool primary = false)
    {
        var b = new Button { Content = text, MinWidth = 90 };
        if (primary) b.Style = (Style)Application.Current.Resources["Primary"];
        return b;
    }

    public static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        if (buttons.Length > 0) buttons[^1].Margin = new Thickness(0);
        return p;
    }
}

/// <summary>Pergunta um texto (nome do perfil).</summary>
public static class InputDialog
{
    public static string? Ask(Window? owner, string title, string question, string initial)
    {
        var w = Ui.NewDialog(owner, title, 460);
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6, 5, 6, 5) };
        var ok = Ui.Btn("Salvar", primary: true);
        var cancel = Ui.Btn("Cancelar");
        ok.IsDefault = true;
        cancel.IsCancel = true;
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; w.DialogResult = true; };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Ui.Text(question));
        root.Children.Add(box);
        root.Children.Add(Ui.Buttons(cancel, ok));
        w.Content = root;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }
}

/// <summary>Ajustes de um vídeo antes de começar: cortar, girar, tirar o som, só o áudio, resolução.</summary>
public sealed class VideoEditWindow : Window
{
    private readonly TextBox _start = new() { Width = 90 };
    private readonly TextBox _end = new() { Width = 90 };
    private readonly ComboBox _rotate = new() { MinWidth = 220 };
    private readonly ComboBox _res = new() { MinWidth = 220 };
    private readonly CheckBox _noAudio = new() { Content = "Tirar o som" };
    private readonly CheckBox _audioOnly = new() { Content = "Extrair só o áudio (MP3)" };
    private readonly TextBlock _error = Ui.Text("", size: 13);
    private readonly TextBlock _info = Ui.Text("Lendo o vídeo…", muted: true, size: 13);
    private readonly QueueItem _item;

    public CompressOptions? Result { get; private set; }

    public VideoEditWindow(AppServices s, QueueItem item)
    {
        _item = item;
        Title = "Ajustes do vídeo";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.Resources["Bg"];
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 14;
        _error.Foreground = (Brush)Application.Current.Resources["Danger"];

        var o = item.Options;
        _start.Text = o.TrimStart is { } a ? Format.Duration(a) : "";
        _end.Text = o.TrimEnd is { } b ? Format.Duration(b) : "";
        foreach (var t in new[] { "Não girar", "Girar 90° para a direita", "Girar 180°", "Girar 90° para a esquerda" }) _rotate.Items.Add(t);
        _rotate.SelectedIndex = o.Rotate switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 };
        var resValues = new[] { 0, 100000, 1080, 720, 480 };
        foreach (var t in new[] { "A do nível escolhido", "Original (sem reduzir)", "1080p (Full HD)", "720p (HD)", "480p" }) _res.Items.Add(t);
        _res.SelectedIndex = Math.Max(0, Array.IndexOf(resValues, o.VideoMaxShortSide));
        _noAudio.IsChecked = o.RemoveAudio;
        _audioOnly.IsChecked = o.ExtractAudioOnly;
        _audioOnly.Checked += (_, _) => _noAudio.IsChecked = false;
        _noAudio.Checked += (_, _) => _audioOnly.IsChecked = false;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Ui.Text(item.Name, bold: true, size: 16));
        root.Children.Add(_info);

        root.Children.Add(Section("Cortar um trecho"));
        var trim = new StackPanel { Orientation = Orientation.Horizontal };
        trim.Children.Add(Ui.Text("De "));
        trim.Children.Add(_start);
        trim.Children.Add(new TextBlock { Text = "  até  ", VerticalAlignment = VerticalAlignment.Center });
        trim.Children.Add(_end);
        root.Children.Add(trim);
        root.Children.Add(Ui.Text("Use minutos:segundos (ex.: 1:30). Deixe vazio para começar do início ou ir até o fim.", muted: true, size: 12.5));

        root.Children.Add(Section("Girar"));
        root.Children.Add(_rotate);
        root.Children.Add(Section("Resolução máxima"));
        root.Children.Add(_res);
        root.Children.Add(Section("Som"));
        root.Children.Add(_noAudio);
        root.Children.Add(_audioOnly);
        root.Children.Add(_error);

        var ok = Ui.Btn("Aplicar", primary: true);
        var clear = Ui.Btn("Limpar ajustes");
        var cancel = Ui.Btn("Cancelar");
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) => Apply(resValues);
        clear.Click += (_, _) =>
        {
            _start.Text = _end.Text = "";
            _rotate.SelectedIndex = 0;
            _res.SelectedIndex = 0;
            _noAudio.IsChecked = _audioOnly.IsChecked = false;
        };
        root.Children.Add(Ui.Buttons(clear, cancel, ok));
        Content = root;

        Loaded += async (_, _) =>
        {
            try
            {
                var info = await MediaProbe.ProbeAsync(s.Tools, item.Path, CancellationToken.None);
                _info.Text = $"{info.DisplayWidth}×{info.DisplayHeight} · {Format.Duration(TimeSpan.FromSeconds(info.DurationSec))}" + (info.IsHdr ? " · HDR (será convertido para cores normais)" : "");
            }
            catch (Exception ex)
            {
                _info.Text = ex.Message;
            }
        };
    }

    private static TextBlock Section(string t)
    {
        var tb = Ui.Text(t, bold: true);
        tb.Margin = new Thickness(0, 14, 0, 6);
        return tb;
    }

    private void Apply(int[] resValues)
    {
        if (!Format.TryParseTime(_start.Text, out var start) || !Format.TryParseTime(_end.Text, out var end))
        {
            _error.Text = "Tempo inválido. Use minutos:segundos, por exemplo 0:45 ou 1:30.";
            return;
        }
        if (start is { } a && end is { } b && b <= a)
        {
            _error.Text = "O fim precisa ser depois do início.";
            return;
        }
        var o = _item.Options.Clone();
        o.TrimStart = start is { TotalSeconds: > 0 } ? start : null;
        o.TrimEnd = end;
        o.Rotate = _rotate.SelectedIndex switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
        o.VideoMaxShortSide = resValues[Math.Max(0, _res.SelectedIndex)];
        o.RemoveAudio = _noAudio.IsChecked == true;
        o.ExtractAudioOnly = _audioOnly.IsChecked == true;
        Result = o;
        DialogResult = true;
    }
}

/// <summary>Antes e depois, lado a lado, com zoom igual nos dois.</summary>
public sealed class CompareWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Image _left = new() { Stretch = Stretch.Uniform };
    private readonly Image _right = new() { Stretch = Stretch.Uniform };
    private readonly ScrollViewer _lsv;
    private readonly ScrollViewer _rsv;
    private readonly TextBlock _lTitle = Ui.Text("Antes", bold: true);
    private readonly TextBlock _rTitle = Ui.Text("Depois", bold: true);
    private readonly TextBlock _status = Ui.Text("Gerando a prévia…", muted: true);
    private readonly CheckBox _zoom = new() { Content = "Tamanho real (100%)", Margin = new Thickness(0) };
    private bool _syncing;

    public CompareWindow(AppServices s, string input, CompressOptions options, string? output)
    {
        Title = "Antes e depois — " + Path.GetFileName(input);
        Width = 1200;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["Bg"];
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 14;

        _lsv = Viewer(_left);
        _rsv = Viewer(_right);
        _lsv.ScrollChanged += (_, e) => Sync(_lsv, _rsv);
        _rsv.ScrollChanged += (_, e) => Sync(_rsv, _lsv);
        _zoom.Checked += (_, _) => SetZoom(true);
        _zoom.Unchecked += (_, _) => SetZoom(false);

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_zoom, Dock.Right);
        top.Children.Add(_zoom);
        top.Children.Add(_status);
        Grid.SetColumnSpan(top, 3);
        grid.Children.Add(top);

        Grid.SetRow(_lTitle, 1);
        Grid.SetRow(_rTitle, 1);
        Grid.SetColumn(_rTitle, 2);
        _lTitle.Margin = _rTitle.Margin = new Thickness(0, 0, 0, 6);
        grid.Children.Add(_lTitle);
        grid.Children.Add(_rTitle);
        var lb = Frame(_lsv);
        var rb = Frame(_rsv);
        Grid.SetRow(lb, 2);
        Grid.SetRow(rb, 2);
        Grid.SetColumn(rb, 2);
        grid.Children.Add(lb);
        grid.Children.Add(rb);
        Content = grid;

        Closed += (_, _) => _cts.Cancel();
        Loaded += async (_, _) => await LoadAsync(s, input, options, output);
    }

    private static ScrollViewer Viewer(Image img) => new()
    {
        Content = img,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
    };

    private static Border Frame(UIElement child) => new()
    {
        Child = child,
        BorderBrush = (Brush)Application.Current.Resources["Line"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        ClipToBounds = true,
    };

    private void SetZoom(bool real)
    {
        foreach (var (sv, img) in new[] { (_lsv, _left), (_rsv, _right) })
        {
            img.Stretch = real ? Stretch.None : Stretch.Uniform;
            sv.HorizontalScrollBarVisibility = real ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            sv.VerticalScrollBarVisibility = real ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        }
    }

    private void Sync(ScrollViewer from, ScrollViewer to)
    {
        if (_syncing) return;
        _syncing = true;
        to.ScrollToHorizontalOffset(from.HorizontalOffset);
        to.ScrollToVerticalOffset(from.VerticalOffset);
        _syncing = false;
    }

    private async Task LoadAsync(AppServices s, string input, CompressOptions options, string? output)
    {
        try
        {
            // Contexto próprio: a prévia funciona mesmo com a fila pausada.
            var pm = new PreviewMaker(new CompressContext { Tools = s.Tools, Hardware = s.Hardware });
            var r = await Task.Run(() => pm.MakeAsync(input, options, output, _cts.Token));
            _left.Source = Load(r.BeforeImage);
            _right.Source = Load(r.AfterImage);
            _lTitle.Text = $"Antes · {Format.Bytes(r.OriginalBytes)}";
            _rTitle.Text = r.KeptOriginal
                ? "Depois · sem ganho (o original seria mantido)"
                : $"Depois · {(r.Estimated ? "~" : "")}{Format.Bytes(r.FinalBytes)} (−{Format.SavingPercent(r.OriginalBytes, r.FinalBytes)}%)";
            _status.Text = r.Note;
        }
        catch (OperationCanceledException)
        {
            // janela fechada
        }
        catch (Exception ex)
        {
            _status.Text = JobQueue.Friendly(ex);
            _status.Foreground = (Brush)Application.Current.Resources["Danger"];
        }
    }

    /// <summary>Carrega sem travar o arquivo (para poder apagar a prévia depois).</summary>
    private static BitmapImage Load(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
