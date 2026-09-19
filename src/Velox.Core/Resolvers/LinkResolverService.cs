using System.Diagnostics;
using Velox.Core.Services;

namespace Velox.Core.Resolvers;

public enum ResolveStatus
{
    /// <summary>A URL não era um link encurtado/portão conhecido.</summary>
    Unchanged,
    /// <summary>Chegou a uma URL de destino diferente da original.</summary>
    Resolved,
    /// <summary>O site exige interação humana (captcha validado no servidor).</summary>
    NeedsBrowser,
    Failed
}

public sealed class ResolveStep
{
    public int Index { get; init; }
    public string Resolver { get; init; } = "";
    public string Description { get; init; } = "";
    public string Url { get; init; } = "";
    public double ElapsedMs { get; init; }
}

public sealed class ResolveResult
{
    public ResolveStatus Status { get; init; }
    public string OriginalUrl { get; init; } = "";
    public string FinalUrl { get; init; } = "";
    public List<ResolveStep> Steps { get; init; } = new();
    public string? Error { get; init; }
    /// <summary>true quando a URL final responde com um arquivo (não uma página HTML).</summary>
    public bool IsDirectFile { get; init; }
    public string? ContentType { get; init; }
    public long? Size { get; init; }
    public string? FileName { get; init; }
    public double TotalMs { get; init; }
}

/// <summary>
/// Orquestra a resolução de um link: busca a página, deixa cada resolvedor reconhecer o
/// "portão" pelo conteúdo e avança até chegar a uma URL que nenhum resolvedor reconhece
/// (o destino) ou a um arquivo direto.
/// </summary>
public sealed class LinkResolverService
{
    public const int MaxSteps = 12;

    private readonly IReadOnlyList<ILinkResolver> _resolvers;
    private readonly Func<ResolverHttp> _httpFactory;

    public LinkResolverService(Func<ResolverHttp>? httpFactory = null, IEnumerable<ILinkResolver>? resolvers = null)
    {
        _httpFactory = httpFactory ?? (() => new ResolverHttp());
        _resolvers = (resolvers ?? DefaultResolvers()).ToList();
    }

    public static IEnumerable<ILinkResolver> DefaultResolvers()
    {
        yield return new AdLinkFlyResolver();
        yield return new WpSafelinkResolver();
        yield return new JsRedirectResolver(); // genérico: sempre por último
    }

    /// <summary>Heurística barata para a UI decidir se vale a pena tentar resolver.</summary>
    public static bool LooksLikeShortener(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (KnownHosts.Any(h => host == h || host.EndsWith("." + h))) return true;
        // caminho curto tipo /AbC123 sem extensão de arquivo
        var path = uri.AbsolutePath.Trim('/');
        return path.Length is >= 4 and <= 12 && !path.Contains('/') && !path.Contains('.') && string.IsNullOrEmpty(uri.Query);
    }

    private static readonly string[] KnownHosts =
    {
        "shrinkme.click", "shrinkme.io", "shrinke.me", "shrinkearn.com", "clk.sh", "clks.pro", "gplinks.co", "gplinks.in",
        "droplink.co", "linkvertise.com", "ouo.io", "ouo.press", "exe.io", "exey.io", "cutt.ly", "bit.ly", "tinyurl.com",
        "adf.ly", "shorte.st", "sh.st", "za.gl", "mrproblogger.com", "themezon.net", "linkjust.com", "ez4short.com",
        "shrinkurl.org", "shrinkforearn.in", "urlcut.in", "1short.io", "adshnk.com", "adrinolinks.in", "linkrex.net"
    };

    public async Task<ResolveResult> ResolveAsync(string url, Action<string>? log = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<ResolveStep>();
        log ??= _ => { };

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var start) || (start.Scheme != "http" && start.Scheme != "https"))
            return new ResolveResult { Status = ResolveStatus.Failed, OriginalUrl = url, FinalUrl = url, Error = "Endereço inválido." };

        using var http = _httpFactory();
        var ctx = new ResolverContext { Http = http, Log = log, Cancellation = ct };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Uri current = start;
        Uri? referer = null;
        FetchedPage? page = null;

        try
        {
            for (int i = 0; i < MaxSteps; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!visited.Add(current.ToString()))
                    throw new InvalidOperationException("O site entrou em um ciclo de redirecionamentos.");

                var t0 = sw.Elapsed.TotalMilliseconds;
                log($"Abrindo {current.Host}{(current.AbsolutePath.Length > 1 ? current.AbsolutePath : "")}");
                page = await http.GetAsync(current, referer, ct).ConfigureAwait(false);

                if (!page.IsHtml)
                {
                    // arquivo direto (ou algo que não é página): fim da cadeia
                    return Finish(ResolveStatus.Resolved, page, steps, sw, direct: true, original: url);
                }

                Advance? advance = null;
                string resolverName = "";
                foreach (var r in _resolvers)
                {
                    advance = await r.TryAdvanceAsync(ctx, page).ConfigureAwait(false);
                    if (advance != null) { resolverName = r.Name; break; }
                }

                if (advance == null)
                {
                    // ninguém reconheceu: é o destino (ou nunca foi um encurtador)
                    var status = steps.Count == 0 && page.Url == start ? ResolveStatus.Unchanged : ResolveStatus.Resolved;
                    return Finish(status, page, steps, sw, direct: false, original: url);
                }

                steps.Add(new ResolveStep
                {
                    Index = steps.Count + 1,
                    Resolver = resolverName,
                    Description = advance.Description,
                    Url = advance.Url.ToString(),
                    ElapsedMs = sw.Elapsed.TotalMilliseconds - t0
                });

                if (advance.IsFinal)
                    return new ResolveResult
                    {
                        Status = ResolveStatus.Resolved, OriginalUrl = url, FinalUrl = advance.Url.ToString(),
                        Steps = steps, TotalMs = sw.Elapsed.TotalMilliseconds
                    };

                referer = advance.Referer ?? page.Url;
                current = advance.Url;
            }

            throw new InvalidOperationException($"A cadeia passou de {MaxSteps} passos sem chegar ao destino.");
        }
        catch (NeedsBrowserException ex)
        {
            return new ResolveResult
            {
                Status = ResolveStatus.NeedsBrowser, OriginalUrl = url, FinalUrl = ex.Url.ToString(),
                Steps = steps, Error = ex.Message, TotalMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ResolveResult
            {
                Status = ResolveStatus.Failed, OriginalUrl = url, FinalUrl = current.ToString(),
                Steps = steps, Error = ex is HttpRequestException ? "Falha de rede: " + ex.Message : ex.Message,
                TotalMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    private static ResolveResult Finish(ResolveStatus status, FetchedPage page, List<ResolveStep> steps, Stopwatch sw, bool direct, string original)
    {
        string? fileName = null;
        if (direct)
        {
            fileName = page.ContentDisposition != null ? FileNameHelper.FromContentDisposition(page.ContentDisposition) : null;
            fileName ??= FileNameHelper.FromUri(page.Url);
        }

        return new ResolveResult
        {
            Status = status,
            OriginalUrl = original,
            FinalUrl = page.Url.ToString(),
            Steps = steps,
            IsDirectFile = direct,
            ContentType = page.ContentType,
            Size = direct ? page.ContentLength : null,
            FileName = fileName,
            TotalMs = sw.Elapsed.TotalMilliseconds
        };
    }
}
