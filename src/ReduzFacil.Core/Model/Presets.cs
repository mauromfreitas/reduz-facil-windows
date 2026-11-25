namespace ReduzFacil.Core.Model;

/// <summary>Atalhos "Enviar para…" (mesmas metas do app web) e os modos fixos.</summary>
public sealed record Preset(string Id, string Label, string Hint, long? TargetMb, bool ConvertOnly = false)
{
    public const string Qualidade = "qualidade";
    public const string Meta = "meta";
    public const string Converter = "converter";

    public static readonly IReadOnlyList<Preset> BuiltIn =
    [
        new(Qualidade, "Por qualidade", "Reduz o máximo mantendo a qualidade do nível escolhido", null),
        new("whatsapp", "WhatsApp", "Cabe no envio de vídeo do WhatsApp (16 MB)", 16),
        new("email", "E-mail", "Cabe como anexo no Gmail e no Outlook (18 MB)", 18),
        new("gov", "gov.br e portais", "Limite mais comum em portais e formulários públicos (2 MB)", 2),
        new("enem", "Enem e concursos", "Fotos e documentos de inscrição (1 MB) — confira o edital", 1),
        new("curriculo", "Currículo", "Sites de vagas costumam aceitar até 2 MB", 2),
        new(Meta, "Meta em MB", "Você escolhe o tamanho máximo de cada arquivo", null),
        new(Converter, "Converter", "Deixa compatível com tudo: fotos em JPG, vídeos em MP4, áudios em MP3", null, ConvertOnly: true),
    ];

    public static Preset? Find(string? id) =>
        BuiltIn.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
