namespace ReduzFacil.Core.Model;

/// <summary>Quais arquivos o app trata, pelo nome (mesma lista do app web, mais alguns do Windows).</summary>
public static class FileTypes
{
    public static readonly string[] ImageExt = ["jpg", "jpeg", "jfif", "png", "webp", "bmp", "avif", "heic", "heif"];
    public static readonly string[] VideoExt = ["mp4", "mov", "mkv", "avi", "webm", "m4v", "wmv", "3gp", "mts", "m2ts", "mpg", "mpeg", "flv"];
    public static readonly string[] AudioExt = ["mp3", "wav", "m4a", "aac", "ogg", "opus", "flac", "wma", "aiff", "aif"];
    public static readonly string[] PdfExt = ["pdf"];

    public static IEnumerable<string> AllExtensions => ImageExt.Concat(VideoExt).Concat(AudioExt).Concat(PdfExt);

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static FileKind? KindOf(string path)
    {
        var ext = Ext(path);
        if (PdfExt.Contains(ext)) return FileKind.Pdf;
        if (ImageExt.Contains(ext)) return FileKind.Image;
        if (VideoExt.Contains(ext)) return FileKind.Video;
        if (AudioExt.Contains(ext)) return FileKind.Audio;
        return null;
    }

    /// <summary>Arquivos que nunca entram (downloads pela metade, temporários, nossos próprios temporários).</summary>
    public static bool IsTemporary(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.StartsWith("~$") || name.StartsWith(".~")) return true;
        return name.EndsWith(".crdownload") || name.EndsWith(".part") || name.EndsWith(".partial")
               || name.EndsWith(".tmp") || name.EndsWith(".download") || name.EndsWith(".reduzindo");
    }

    public static string Label(FileKind kind) => kind switch
    {
        FileKind.Image => "Imagem",
        FileKind.Video => "Vídeo",
        FileKind.Audio => "Áudio",
        FileKind.Pdf => "PDF",
        _ => "Arquivo",
    };
}
