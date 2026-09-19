using System.Net;
using Velox.Core.Models;

namespace Velox.Core.Engine;

public static class HttpClientFactory
{
    public static HttpClient Create(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 256,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            EnableMultipleHttp2Connections = true,
            UseCookies = false,
        };

        if (!string.IsNullOrWhiteSpace(settings.ProxyUrl))
        {
            try
            {
                handler.Proxy = new WebProxy(settings.ProxyUrl.Trim());
                handler.UseProxy = true;
            }
            catch
            {
                handler.UseProxy = false;
            }
        }

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var ua = string.IsNullOrWhiteSpace(settings.UserAgent) ? "VeloxDM/1.0" : settings.UserAgent.Trim();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

        return client;
    }
}

internal static class RequestBuilder
{
    public static HttpRequestMessage Build(string url, IDictionary<string, string>? headers, string? referer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        if (!string.IsNullOrWhiteSpace(referer))
            req.Headers.TryAddWithoutValidation("Referer", referer.Trim());

        if (headers != null)
        {
            foreach (var kv in headers)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                req.Headers.Remove(kv.Key);
                req.Headers.TryAddWithoutValidation(kv.Key.Trim(), kv.Value?.Trim() ?? "");
            }
        }

        return req;
    }
}
