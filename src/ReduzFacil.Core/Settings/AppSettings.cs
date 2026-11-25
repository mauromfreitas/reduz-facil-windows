using System.Text.Json;
using System.Text.Json.Serialization;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Output;
using ReduzFacil.Core.Util;

namespace ReduzFacil.Core.Settings;

/// <summary>Perfil salvo pelo usuário: atalho + meta + todos os ajustes.</summary>
public sealed class SavedProfile
{
    public string Name { get; set; } = "";
    public string PresetId { get; set; } = Preset.Qualidade;
    public double? TargetMb { get; set; }
    public CompressOptions Options { get; set; } = new();
}

public sealed class WatchSettings
{
    public bool Enabled { get; set; }
    public string? Folder { get; set; }
    public bool IncludeSubfolders { get; set; } = true;
    /// <summary>Atalho embutido (id) ou nome de um perfil salvo. Vazio = o que está escolhido na tela.</summary>
    public string? Profile { get; set; } = Preset.Qualidade;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public OutputSettings Output { get; set; } = new();

    /// <summary>Ajustes da tela principal (copiados para cada arquivo que entra na fila).</summary>
    public CompressOptions Options { get; set; } = new();
    public string PresetId { get; set; } = Preset.Qualidade;
    public double CustomTargetMb { get; set; } = 5;

    public bool AutoStart { get; set; } = true;

    /// <summary>Com meta (WhatsApp, e-mail…): vídeo longo é dividido em partes do tamanho da meta em vez de perder qualidade.</summary>
    public bool SplitVideos { get; set; }
    public int VideosAtOnce { get; set; } = 1;
    public int OthersAtOnce { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public bool ShellMenu { get; set; }
    public bool StartWithWindows { get; set; }
    /// <summary>Ao fechar com a pasta vigiada ligada, continua na bandeja (perto do relógio).</summary>
    public bool TrayOnClose { get; set; } = true;
    public bool NotifyWhenDone { get; set; } = true;

    /// <summary>Pastas vigiadas (cada uma com o seu perfil).</summary>
    public List<WatchSettings> Watches { get; set; } = [];

    /// <summary>Formato antigo (1.0, uma pasta só): lido e convertido para <see cref="Watches"/>.</summary>
    public WatchSettings? Watch { get; set; }

    /// <summary>"claro", "escuro" ou "sistema" (igual ao Windows).</summary>
    public string Theme { get; set; } = "sistema";

    public bool CheckUpdates { get; set; } = true;
    public string? SkippedVersion { get; set; }

    /// <summary>O resultado de um print colado (Ctrl+V) vai direto para a área de transferência.</summary>
    public bool CopyPastedResult { get; set; } = true;
    public List<SavedProfile> Profiles { get; set; } = [];

    public double ScanMinMb { get; set; } = 1;
    public string? ScanLastFolder { get; set; }

    /// <summary>Opções efetivas para um atalho (embutido ou perfil salvo).</summary>
    public CompressOptions OptionsFor(string? presetOrProfile, double? customMb = null)
    {
        var profile = Profiles.FirstOrDefault(p => string.Equals(p.Name, presetOrProfile, StringComparison.OrdinalIgnoreCase));
        var o = profile is not null
            ? Build(profile.Options, profile.PresetId, profile.TargetMb)
            : Build(Options, presetOrProfile ?? PresetId, customMb ?? CustomTargetMb);
        if (SplitVideos && o.TargetBytes is { } t) o.SplitPartBytes = t;
        return o;
    }

    public static CompressOptions Build(CompressOptions baseOptions, string presetId, double? customMb)
    {
        var o = baseOptions.Clone();
        o.TrimStart = o.TrimEnd = null;
        o.Rotate = 0;
        o.RemoveAudio = o.ExtractAudioOnly = false;
        o.SplitPartBytes = null;
        var preset = Preset.Find(presetId) ?? Preset.BuiltIn[0];
        o.ConvertOnly = preset.ConvertOnly;
        o.TargetBytes = preset.Id switch
        {
            Preset.Meta => customMb is > 0 ? (long)(customMb.Value * 1024 * 1024) : null,
            _ => preset.TargetMb is { } mb ? mb * 1024 * 1024 : null,
        };
        return o;
    }
}

/// <summary>Lê e grava configuracoes.json com segurança (arquivo novo só substitui o antigo quando está completo).</summary>
public sealed class SettingsStore(string path)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) ?? new AppSettings();
            Normalize(s);
            return s;
        }
        catch (Exception ex)
        {
            // Arquivo estragado: guarda uma cópia para análise e começa do padrão (o app não pode deixar de abrir).
            Log.Error("Configurações ilegíveis; usando o padrão", ex);
            try { File.Copy(Path, Path + ".corrompido", overwrite: true); } catch { /* sem cópia */ }
            return new AppSettings();
        }
    }

    public void Save(AppSettings s)
    {
        lock (_gate)
        {
            try
            {
                var tmp = Path + ".gravando";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
                File.Move(tmp, Path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Não consegui salvar as configurações", ex);
            }
        }
    }

    private static void Normalize(AppSettings s)
    {
        s.Output ??= new OutputSettings();
        s.Options ??= new CompressOptions();
        s.Watches ??= [];
        if (s.Watch is { Folder: not null } old && s.Watches.Count == 0) s.Watches.Add(old);
        s.Watch = null;
        s.Watches.RemoveAll(w => w is null);
        if (s.Theme is not ("claro" or "escuro" or "sistema")) s.Theme = "sistema";
        s.Profiles ??= [];
        s.Output.Suffix ??= "-reduzido";
        s.VideosAtOnce = Math.Clamp(s.VideosAtOnce, 1, 4);
        s.OthersAtOnce = Math.Clamp(s.OthersAtOnce, 1, 16);
        s.ScanMinMb = Math.Clamp(s.ScanMinMb, 0.1, 10_000);
        if (s.CustomTargetMb <= 0) s.CustomTargetMb = 5;
        if (Preset.Find(s.PresetId) is null) s.PresetId = Preset.Qualidade;
        // Ajustes por arquivo nunca ficam salvos como padrão.
        s.Options.TrimStart = s.Options.TrimEnd = null;
        s.Options.Rotate = 0;
        s.Options.RemoveAudio = s.Options.ExtractAudioOnly = false;
        s.Options.SplitPartBytes = null;
        s.Options.TargetBytes = null;
        s.Options.ConvertOnly = false;
    }
}
