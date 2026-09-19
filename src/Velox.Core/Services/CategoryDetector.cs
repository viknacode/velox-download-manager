namespace Velox.Core.Services;

public static class CategoryDetector
{
    public const string Video = "Vídeos";
    public const string Music = "Músicas";
    public const string Documents = "Documentos";
    public const string Compressed = "Compactados";
    public const string Programs = "Programas";
    public const string Images = "Imagens";
    public const string Other = "Outros";

    public static readonly string[] All = { Video, Music, Documents, Compressed, Programs, Images, Other };

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // vídeo
        [".mp4"] = Video, [".mkv"] = Video, [".avi"] = Video, [".mov"] = Video, [".wmv"] = Video,
        [".flv"] = Video, [".webm"] = Video, [".m4v"] = Video, [".mpg"] = Video, [".mpeg"] = Video,
        [".ts"] = Video, [".3gp"] = Video, [".vob"] = Video,
        // música
        [".mp3"] = Music, [".flac"] = Music, [".wav"] = Music, [".aac"] = Music, [".ogg"] = Music,
        [".m4a"] = Music, [".wma"] = Music, [".opus"] = Music, [".aiff"] = Music, [".alac"] = Music,
        // documentos
        [".pdf"] = Documents, [".doc"] = Documents, [".docx"] = Documents, [".xls"] = Documents,
        [".xlsx"] = Documents, [".ppt"] = Documents, [".pptx"] = Documents, [".txt"] = Documents,
        [".rtf"] = Documents, [".odt"] = Documents, [".ods"] = Documents, [".odp"] = Documents,
        [".epub"] = Documents, [".mobi"] = Documents, [".csv"] = Documents, [".md"] = Documents,
        // compactados
        [".zip"] = Compressed, [".rar"] = Compressed, [".7z"] = Compressed, [".tar"] = Compressed,
        [".gz"] = Compressed, [".bz2"] = Compressed, [".xz"] = Compressed, [".tgz"] = Compressed,
        [".iso"] = Compressed, [".cab"] = Compressed, [".zst"] = Compressed,
        // programas
        [".exe"] = Programs, [".msi"] = Programs, [".msix"] = Programs, [".appx"] = Programs,
        [".apk"] = Programs, [".dmg"] = Programs, [".pkg"] = Programs, [".deb"] = Programs,
        [".rpm"] = Programs, [".appimage"] = Programs, [".jar"] = Programs, [".bat"] = Programs,
        [".ps1"] = Programs, [".bin"] = Programs,
        // imagens
        [".jpg"] = Images, [".jpeg"] = Images, [".png"] = Images, [".gif"] = Images, [".bmp"] = Images,
        [".webp"] = Images, [".svg"] = Images, [".tiff"] = Images, [".tif"] = Images, [".psd"] = Images,
        [".heic"] = Images, [".avif"] = Images, [".ico"] = Images, [".raw"] = Images,
    };

    public static IEnumerable<string> KnownExtensions => ByExtension.Keys;

    public static string Detect(string? fileName, string? contentType)
    {
        if (!string.IsNullOrEmpty(fileName))
        {
            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) && ByExtension.TryGetValue(ext, out var cat))
                return cat;
        }

        if (!string.IsNullOrEmpty(contentType))
        {
            var ct = contentType.ToLowerInvariant();
            if (ct.StartsWith("video/")) return Video;
            if (ct.StartsWith("audio/")) return Music;
            if (ct.StartsWith("image/")) return Images;
            if (ct.Contains("pdf") || ct.StartsWith("text/") || ct.Contains("word") || ct.Contains("excel") ||
                ct.Contains("powerpoint") || ct.Contains("officedocument")) return Documents;
            if (ct.Contains("zip") || ct.Contains("rar") || ct.Contains("7z") || ct.Contains("tar") ||
                ct.Contains("compressed") || ct.Contains("iso")) return Compressed;
            if (ct.Contains("msdownload") || ct.Contains("executable") || ct.Contains("x-msi") ||
                ct.Contains("android.package")) return Programs;
        }

        return Other;
    }

    public static bool HasKnownExtension(string urlOrName)
    {
        try
        {
            var path = Uri.TryCreate(urlOrName, UriKind.Absolute, out var uri) ? uri.AbsolutePath : urlOrName;
            var ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && ByExtension.ContainsKey(ext);
        }
        catch
        {
            return false;
        }
    }
}
