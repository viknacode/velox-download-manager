using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Velox.Core.Services;

public static partial class FileNameHelper
{
    private static readonly Dictionary<string, string> ExtByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip", ["application/x-zip-compressed"] = ".zip",
        ["application/x-rar-compressed"] = ".rar", ["application/vnd.rar"] = ".rar",
        ["application/x-7z-compressed"] = ".7z", ["application/gzip"] = ".gz",
        ["application/x-tar"] = ".tar", ["application/x-iso9660-image"] = ".iso",
        ["application/pdf"] = ".pdf", ["application/msword"] = ".doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["application/x-msdownload"] = ".exe", ["application/x-msi"] = ".msi",
        ["application/vnd.android.package-archive"] = ".apk",
        ["application/json"] = ".json", ["application/xml"] = ".xml", ["text/xml"] = ".xml",
        ["text/plain"] = ".txt", ["text/html"] = ".html", ["text/csv"] = ".csv",
        ["video/mp4"] = ".mp4", ["video/x-matroska"] = ".mkv", ["video/webm"] = ".webm",
        ["video/x-msvideo"] = ".avi", ["video/quicktime"] = ".mov",
        ["audio/mpeg"] = ".mp3", ["audio/flac"] = ".flac", ["audio/wav"] = ".wav",
        ["audio/ogg"] = ".ogg", ["audio/aac"] = ".aac", ["audio/mp4"] = ".m4a",
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif",
        ["image/webp"] = ".webp", ["image/svg+xml"] = ".svg", ["image/bmp"] = ".bmp",
        ["application/octet-stream"] = ".bin",
    };

    [GeneratedRegex(@"filename\*\s*=\s*(?<cs>[^']*)'[^']*'(?<v>[^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameStarRegex();

    [GeneratedRegex(@"filename\s*=\s*(?:""(?<v>[^""]+)""|(?<v>[^;]+))", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    public static string Resolve(HttpResponseMessage response, string requestUrl)
    {
        string? name = null;

        if (response.Content.Headers.TryGetValues("Content-Disposition", out var values))
        {
            var cd = string.Join(";", values);
            name = FromContentDisposition(cd);
        }

        var uri = response.RequestMessage?.RequestUri ?? (Uri.TryCreate(requestUrl, UriKind.Absolute, out var u) ? u : null);
        if (string.IsNullOrWhiteSpace(name) && uri != null)
            name = FromUri(uri);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(name))
            name = "download";

        name = Sanitize(name);

        if (string.IsNullOrEmpty(Path.GetExtension(name)) && contentType != null &&
            ExtByMime.TryGetValue(contentType, out var ext) && ext != ".bin" && ext != ".html")
            name += ext;

        return name;
    }

    public static string? FromContentDisposition(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        var star = FileNameStarRegex().Match(header);
        if (star.Success)
        {
            var raw = star.Groups["v"].Value.Trim().Trim('"');
            try
            {
                var decoded = Uri.UnescapeDataString(raw);
                if (!string.IsNullOrWhiteSpace(decoded)) return decoded;
            }
            catch { /* ignora e cai no filename= */ }
        }

        var plain = FileNameRegex().Match(header);
        if (plain.Success)
        {
            var v = plain.Groups["v"].Value.Trim().Trim('"');
            // alguns servidores enviam UTF-8 puro ou %xx no filename=
            if (v.Contains('%'))
            {
                try { v = Uri.UnescapeDataString(v); } catch { }
            }
            else
            {
                // tenta corrigir latin1 → utf8 (mojibake)
                try
                {
                    var bytes = Encoding.Latin1.GetBytes(v);
                    var utf = Encoding.UTF8.GetString(bytes);
                    if (!utf.Contains('�') && utf.Length < v.Length) v = utf;
                }
                catch { }
            }
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        return null;
    }

    public static string FromUri(Uri uri)
    {
        try
        {
            var path = uri.AbsolutePath;
            var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(last)) return uri.Host;
            return WebUtility.UrlDecode(last);
        }
        catch
        {
            return "download";
        }
    }

    public static string FromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Sanitize(FromUri(uri));
        return "download";
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > 200)
        {
            var ext = Path.GetExtension(result);
            result = result[..(200 - ext.Length)] + ext;
        }

        return string.IsNullOrWhiteSpace(result) ? "download" : result;
    }

    /// <summary>Garante um nome que não colida com arquivos existentes nem com outros downloads.</summary>
    public static string MakeUnique(string directory, string fileName, Func<string, bool>? isTaken = null)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = fileName;
        int n = 1;

        bool Taken(string f)
        {
            var full = Path.Combine(directory, f);
            return File.Exists(full) || File.Exists(full + ".vxpart") || (isTaken?.Invoke(full) ?? false);
        }

        while (Taken(candidate))
        {
            candidate = $"{baseName} ({n++}){ext}";
        }

        return candidate;
    }
}
