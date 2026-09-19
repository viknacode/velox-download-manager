using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Velox.Core.Resolvers;

/// <summary>
/// Cliente HTTP de uma sessão de resolução: cookies próprios, cabeçalhos de navegador,
/// leitura limitada de HTML e utilitários de parsing (sem dependências externas).
/// </summary>
public sealed class ResolverHttp : IDisposable
{
    private const int MaxHtmlBytes = 3 * 1024 * 1024;

    private readonly HttpClient _client;
    public CookieContainer Cookies { get; } = new();
    public string UserAgent { get; }

    public ResolverHttp(string? userAgent = null, string? proxyUrl = null)
    {
        UserAgent = string.IsNullOrWhiteSpace(userAgent)
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
            : userAgent;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 12,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = Cookies,
            UseCookies = true,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            try { handler.Proxy = new WebProxy(proxyUrl.Trim()); handler.UseProxy = true; } catch { }
        }

        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
    }

    private void ApplyBrowserHeaders(HttpRequestMessage req, Uri? referer, bool xhr = false)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        req.Headers.TryAddWithoutValidation("Accept", xhr
            ? "application/json, text/javascript, */*; q=0.01"
            : "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", xhr ? "0" : "1");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", xhr ? "empty" : "document");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", xhr ? "cors" : "navigate");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", referer == null ? "none" : "same-origin");
        if (xhr) req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (referer != null) req.Headers.Referrer = referer;
    }

    /// <summary>GET seguindo redirecionamentos; lê o corpo só quando é HTML (limitado).</summary>
    public async Task<FetchedPage> GetAsync(Uri url, Uri? referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyBrowserHeaders(req, referer);
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await ToPageAsync(resp, url, referer, ct).ConfigureAwait(false);
    }

    /// <summary>POST de formulário (application/x-www-form-urlencoded).</summary>
    public async Task<FetchedPage> PostFormAsync(Uri url, IEnumerable<KeyValuePair<string, string>> fields, Uri? referer,
        bool xhr, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        ApplyBrowserHeaders(req, referer, xhr);
        req.Headers.TryAddWithoutValidation("Origin", url.GetLeftPart(UriPartial.Authority));
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await ToPageAsync(resp, url, referer, ct, alwaysReadBody: true).ConfigureAwait(false);
    }

    private static async Task<FetchedPage> ToPageAsync(HttpResponseMessage resp, Uri requested, Uri? referer,
        CancellationToken ct, bool alwaysReadBody = false)
    {
        var finalUrl = resp.RequestMessage?.RequestUri ?? requested;
        var contentType = resp.Content.Headers.ContentType?.ToString();
        bool html = contentType != null && (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                                            contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                                            contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                                            contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

        string body = "";
        if (html || alwaysReadBody)
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buf = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buf, 0, read);
                if (ms.Length > MaxHtmlBytes) break;
            }
            body = DecodeBody(ms.ToArray(), resp.Content.Headers.ContentType?.CharSet);
        }

        return new FetchedPage
        {
            Url = finalUrl,
            Referer = referer,
            Status = resp.StatusCode,
            ContentType = contentType,
            ContentLength = resp.Content.Headers.ContentLength,
            ContentDisposition = resp.Content.Headers.TryGetValues("Content-Disposition", out var cd) ? string.Join(";", cd) : null,
            Html = body
        };
    }

    private static string DecodeBody(byte[] bytes, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
                return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
        }
        catch { }
        return Encoding.UTF8.GetString(bytes);
    }

    // ------------------------------------------------------------ parsing

    /// <summary>Extrai os inputs (name → value) de um formulário identificado por atributo (id ou name).</summary>
    public static (string? Action, string? Method, List<KeyValuePair<string, string>> Fields)? ParseForm(string html, string formAttributePattern)
    {
        var form = Regex.Match(html, @"<form\b[^>]*" + formAttributePattern + @"[^>]*>(?<body>[\s\S]*?)</form>", RegexOptions.IgnoreCase);
        if (!form.Success) return null;

        var openTag = form.Value[..form.Value.IndexOf('>')];
        var action = AttributeValue(openTag, "action");
        var method = AttributeValue(openTag, "method");

        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form.Groups["body"].Value, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var name = AttributeValue(input.Value, "name");
            if (string.IsNullOrEmpty(name)) continue;
            var type = AttributeValue(input.Value, "type")?.ToLowerInvariant() ?? "text";
            if (type is "submit" or "button" or "image" or "file" or "reset") continue;
            if (type is "checkbox" or "radio" && !Regex.IsMatch(input.Value, @"\bchecked\b", RegexOptions.IgnoreCase)) continue;
            fields.Add(new KeyValuePair<string, string>(name, WebUtility.HtmlDecode(AttributeValue(input.Value, "value") ?? "")));
        }

        return (action, method, fields);
    }

    public static string? AttributeValue(string tag, string attribute)
    {
        var m = Regex.Match(tag, attribute + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups["v"].Value) : null;
    }

    public static Uri? Absolute(Uri baseUrl, string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        href = WebUtility.HtmlDecode(href.Trim());
        if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith('#')) return null;
        return Uri.TryCreate(baseUrl, href, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https") ? abs : null;
    }

    /// <summary>Desembrulha redirecionadores conhecidos (google.com/url?url=…, ?q=…).</summary>
    public static Uri Unwrap(Uri url)
    {
        if (url.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase) && url.AbsolutePath.StartsWith("/url", StringComparison.OrdinalIgnoreCase))
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            var target = q["url"] ?? q["q"];
            if (Uri.TryCreate(target, UriKind.Absolute, out var inner)) return inner;
        }
        return url;
    }

    public void Dispose() => _client.Dispose();
}
