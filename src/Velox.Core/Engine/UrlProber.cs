using System.Net;
using System.Net.Http.Headers;
using Velox.Core.Models;
using Velox.Core.Services;

namespace Velox.Core.Engine;

/// <summary>
/// Faz uma requisição leve (Range: bytes=0-0) para descobrir tamanho, nome do arquivo
/// e se o servidor aceita retomada por intervalos.
/// </summary>
public static class UrlProber
{
    public static async Task<ProbeResult> ProbeAsync(HttpClient http, string url,
        IDictionary<string, string>? headers, string? referer, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new DownloadException("Endereço inválido. Use um link http:// ou https://.", retryable: false);

        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await ProbeOnceAsync(http, url, headers, referer, ct).ConfigureAwait(false);
            }
            catch (DownloadException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                last = ex;
                await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }

        throw new DownloadException("Não foi possível conectar ao servidor: " + Describe(last), retryable: true, last);
    }

    private static async Task<ProbeResult> ProbeOnceAsync(HttpClient http, string url,
        IDictionary<string, string>? headers, string? referer, CancellationToken ct)
    {
        using var req = RequestBuilder.Build(url, headers, referer);
        req.Headers.Range = new RangeHeaderValue(0, 0);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        var status = (int)resp.StatusCode;
        if (status == 416)
        {
            // arquivo vazio ou servidor estranho — trata como tamanho 0 sem retomada
            return new ProbeResult
            {
                FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url,
                Host = resp.RequestMessage?.RequestUri?.Host ?? "",
                Size = resp.Content.Headers.ContentRange?.Length ?? 0,
                SupportsResume = false,
                FileName = FileNameHelper.Resolve(resp, url),
                ContentType = resp.Content.Headers.ContentType?.MediaType,
            };
        }

        if (!resp.IsSuccessStatusCode)
        {
            bool retryable = status >= 500 || status == 408 || status == 429;
            throw new DownloadException($"O servidor respondeu {status} ({resp.ReasonPhrase}).", retryable);
        }

        var result = new ProbeResult
        {
            FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url,
            Host = resp.RequestMessage?.RequestUri?.Host ?? "",
            ContentType = resp.Content.Headers.ContentType?.MediaType,
            ETag = resp.Headers.ETag?.Tag,
            LastModified = resp.Content.Headers.LastModified,
        };

        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = resp.Content.Headers.ContentRange;
            if (range?.Length is long len)
            {
                result.Size = len;
                result.SupportsResume = true;
            }
            else
            {
                result.Size = -1;
                result.SupportsResume = false;
            }
        }
        else
        {
            result.Size = resp.Content.Headers.ContentLength ?? -1;
            result.SupportsResume = result.Size > 0 && resp.Headers.AcceptRanges.Contains("bytes");
        }

        result.FileName = FileNameHelper.Resolve(resp, url);
        return result;
    }

    internal static string Describe(Exception? ex)
    {
        if (ex is null) return "erro desconhecido";
        if (ex is OperationCanceledException) return "tempo limite excedido";
        var inner = ex;
        while (inner.InnerException != null && string.IsNullOrWhiteSpace(inner.Message)) inner = inner.InnerException;
        var msg = inner.Message;
        if (ex.InnerException is System.Net.Sockets.SocketException se)
            msg = se.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.HostNotFound => "host não encontrado",
                System.Net.Sockets.SocketError.ConnectionRefused => "conexão recusada",
                System.Net.Sockets.SocketError.TimedOut => "tempo limite de conexão",
                System.Net.Sockets.SocketError.NetworkUnreachable => "rede indisponível",
                _ => se.Message
            };
        return msg;
    }
}
