using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Scan;
using ReduzFacil.Core.Stats;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Views;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public static class KindIcons
{
    public static string Glyph(FileKind k) => k switch
    {
        FileKind.Image => "🖼",
        FileKind.Video => "🎬",
        FileKind.Audio => "🎵",
        FileKind.Pdf => "📄",
        _ => "📁",
    };

    public static Brush Tint(FileKind k) => (Brush)System.Windows.Application.Current.Resources[k switch
    {
        FileKind.Image => "TintImage",
        FileKind.Video => "TintVideo",
        FileKind.Audio => "TintAudio",
        _ => "TintPdf",
    }];
}

/// <summary>Uma linha da fila na tela.</summary>
public sealed class QueueItemVm(QueueItem item) : Observable
{
    public QueueItem Item { get; } = item;
    public string Name => Item.Name;
    public string Folder => Path.GetDirectoryName(Item.Path) ?? "";
    public string Glyph => KindIcons.Glyph(Item.Kind);
    public Brush Tint => KindIcons.Tint(Item.Kind);
    public double Percent => Item.Progress * 100;
    public bool IsRunning => Item.Status == JobStatus.Running;
    public bool IsWaiting => Item.Status == JobStatus.Waiting;
    public bool IsFinished => Item.IsFinished;
    public bool CanOpen => Item.Status == JobStatus.Done && Item.Result?.OutputPath is not null;
    public bool CanRetry => Item.Status is JobStatus.Failed or JobStatus.Canceled;
    public bool CanEdit => Item.Status == JobStatus.Waiting && Item.Kind == FileKind.Video;
    public bool CanCompare => Item.Kind is FileKind.Image or FileKind.Video or FileKind.Pdf && Item.Status is not JobStatus.Running;
    public bool HasEdits => Item.Options.HasVideoEdits;

    public string Sizes
    {
        get
        {
            var r = Item.Result;
            if (r is null) return Format.Bytes(Item.SizeBytes);
            if (r.KeptOriginal) return Format.Bytes(r.OriginalBytes);
            return $"{Format.Bytes(r.OriginalBytes)} → {Format.Bytes(r.FinalBytes)}";
        }
    }

    public string Saving
    {
        get
        {
            var r = Item.Result;
            if (r is null || r.KeptOriginal) return "";
            var p = Format.SavingPercent(r.OriginalBytes, r.FinalBytes);
            return r.FinalBytes > r.OriginalBytes ? "maior" : $"−{p}%";
        }
    }

    public string StatusText => Item.Status switch
    {
        JobStatus.Waiting => Item.Options.SplitPartBytes is { } sp ? $"Esperando · dividir em partes de {Format.Bytes(sp)}"
            : Item.Options.HasVideoEdits ? "Esperando · com ajustes" : "Esperando",
        JobStatus.Running => $"Reduzindo… {Item.Progress * 100:0}%",
        JobStatus.Done when Item.Result is { ExtraOutputs.Count: > 0 } r => $"Pronto · {r.ExtraOutputs.Count + 1} partes" + (r.Warnings.Count > 0 ? " · " + r.Warnings[0] : ""),
        JobStatus.Done => Item.Result?.Warnings.Count > 0 ? "Pronto · " + Item.Result.Warnings[0] : "Pronto",
        JobStatus.KeptOriginal => Item.Result?.Warnings.FirstOrDefault() ?? "Já estava otimizado",
        JobStatus.Failed => Item.Error ?? "Falhou",
        JobStatus.Canceled => "Cancelado",
        _ => "",
    };

    public Brush StatusBrush => Item.Status switch
    {
        JobStatus.Done => Res("AccentDark"),
        JobStatus.Failed => Res("Danger"),
        JobStatus.KeptOriginal => Res("Warn"),
        _ => Res("Muted"),
    };

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Item.Path };
            if (Item.Result is { } res)
                foreach (var o in res.AllOutputs) lines.Add("Salvo em: " + o);
            if (Item.Result?.EngineNote is { } n) lines.Add("Como: " + n);
            if (Item.Result?.Warnings is { Count: > 0 } w) lines.AddRange(w);
            return string.Join("\n", lines);
        }
    }

    private static Brush Res(string key) => (Brush)System.Windows.Application.Current.Resources[key];

    public void Refresh() => RaiseAll();
}

/// <summary>Uma linha do "Onde está o peso".</summary>
public sealed class HitVm(ScanHit hit) : Observable
{
    private bool _selected = true;
    private string _state = "";

    public ScanHit Hit { get; } = hit;
    public string Name => Path.GetFileName(Hit.Path);
    public string Folder => Path.GetDirectoryName(Hit.Path) ?? "";
    public string Glyph => KindIcons.Glyph(Hit.Kind);
    public Brush Tint => KindIcons.Tint(Hit.Kind);
    public string Size => Format.Bytes(Hit.SizeBytes);
    public string Estimate => $"~{Format.Bytes(Hit.EstimatedBytes)}";
    public string Saving => $"−{Hit.SavingPercent}% (~{Format.Bytes(Hit.SavingBytes)})";
    public string Detail => Hit.Detail + " · " + Hit.Modified.ToString("dd/MM/yyyy");

    public void Refresh() => RaiseAll();

    public QueueItem? Queued { get; set; }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Raise(); }
    }

    public string State
    {
        get => _state;
        set { _state = value; Raise(); }
    }
}

public sealed class HistoryVm(HistoryEntry e) : Observable
{
    public HistoryEntry Entry { get; } = e;
    public string Name => Path.GetFileName(Entry.Input);
    public string Glyph => KindIcons.Glyph(Entry.Kind);
    public Brush Tint => KindIcons.Tint(Entry.Kind);
    public string When => Entry.When.ToString("dd/MM/yyyy HH:mm");
    public string Sizes => Entry.KeptOriginal ? $"{Format.Bytes(Entry.OriginalBytes)} · mantido" : $"{Format.Bytes(Entry.OriginalBytes)} → {Format.Bytes(Entry.FinalBytes)}";
    public string Saving => Entry.KeptOriginal ? "" : $"−{Format.SavingPercent(Entry.OriginalBytes, Entry.FinalBytes)}%";
    public bool CanOpen => Entry.Output is not null && File.Exists(Entry.Output);
    public string Tooltip => Entry.Input + (Entry.Output is null ? "" : "\nSalvo em: " + Entry.Output) + (Entry.Note is null ? "" : "\n" + Entry.Note);
}

/// <summary>Um grupo de repetidos (idênticos ou fotos parecidas).</summary>
public sealed class DupGroupVm : Observable
{
    public DupGroupVm(DupGroup g, Action onChange)
    {
        Group = g;
        Files = g.Files.Select(f => new DupFileVm(f, g.Exact, onChange)).ToList();
    }

    public DupGroup Group { get; }
    public List<DupFileVm> Files { get; }

    public string Title => Group.Exact
        ? $"Idênticos · {Files.Count} arquivos iguais"
        : $"⚠  Parecidas — confira antes · {Files.Count} fotos";

    public string Subtitle => Group.Exact
        ? $"Mesmo conteúdo, byte a byte. Deixando só um, libera {Format.Bytes(Group.Wasted)}."
        : "Fotos muito parecidas (sequência, mesma foto em outro tamanho). Nada vem marcado: abra e decida.";

    public Brush TitleBrush => (Brush)System.Windows.Application.Current.Resources[Group.Exact ? "Text" : "Warn"];
    public Brush Frame => (Brush)System.Windows.Application.Current.Resources[Group.Exact ? "Line" : "Warn"];
}

public sealed class DupFileVm : Observable
{
    private static readonly string[] Thumbable = ["jpg", "jpeg", "jfif", "png", "bmp"];
    private readonly Action _onChange;
    private bool _selected;
    private System.Windows.Media.ImageSource? _thumb;
    private bool _thumbTried;

    public DupFileVm(DupFile f, bool exact, Action onChange)
    {
        File = f;
        _onChange = onChange;
        // Idênticos: as cópias já vêm marcadas. Parecidas: nunca.
        _selected = exact && !f.Keep;
    }

    public DupFile File { get; }
    public string Name => Path.GetFileName(File.Path);
    public string Folder => Path.GetDirectoryName(File.Path) ?? "";
    public FileKind Kind => FileTypes.KindOf(File.Path) ?? FileKind.Image;
    public string Glyph => KindIcons.Glyph(Kind);
    public Brush Tint => KindIcons.Tint(Kind);
    public string Size => Format.Bytes(File.SizeBytes) + (File.Width > 0 ? $" · {File.Width}×{File.Height}" : "") + " · " + File.Modified.ToString("dd/MM/yyyy");
    public string KeepLabel => File.Keep ? "✓ sugerido manter" : "";

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Raise();
            _onChange();
        }
    }

    /// <summary>Miniatura pequena, carregada só quando aparece na tela.</summary>
    public System.Windows.Media.ImageSource? Thumb
    {
        get
        {
            if (_thumbTried) return _thumb;
            _thumbTried = true;
            if (!Thumbable.Contains(FileTypes.Ext(File.Path))) return null;
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                b.DecodePixelWidth = 120;
                b.UriSource = new Uri(File.Path);
                b.EndInit();
                b.Freeze();
                _thumb = b;
            }
            catch
            {
                _thumb = null;
            }
            return _thumb;
        }
    }

    public void SetSelectedSilently(bool v)
    {
        _selected = v;
        Raise(nameof(Selected));
    }
}
