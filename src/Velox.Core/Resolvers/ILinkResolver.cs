using System.Net;
using System.Text.RegularExpressions;

namespace Velox.Core.Resolvers;

/// <summary>Página obtida durante a resolução (URL final após redirecionamentos + HTML).</summary>
public sealed class FetchedPage
{
    public required Uri Url { get; init; }
    public Uri? Referer { get; init; }
    public HttpStatusCode Status { get; init; }
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public string? ContentDisposition { get; init; }
    public string Html { get; init; } = "";

    public bool IsHtml => ContentType != null &&
                          (ContentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                           ContentType.Contains("xhtml", StringComparison.OrdinalIgnoreCase));

    public bool Contains(string needle) => Html.Contains(needle, StringComparison.OrdinalIgnoreCase);
    public Match Match(string pattern, RegexOptions opts = RegexOptions.IgnoreCase) => Regex.Match(Html, pattern, opts);
}

/// <summary>Próximo passo decidido por um resolvedor.</summary>
public sealed class Advance
{
    public required Uri Url { get; init; }
    public Uri? Referer { get; init; }
    public string Description { get; init; } = "";
    /// <summary>true quando o resolvedor já sabe que esta é a URL de destino (não precisa reprocessar).</summary>
    public bool IsFinal { get; init; }
}

public sealed class ResolverContext
{
    public required ResolverHttp Http { get; init; }
    public required Action<string> Log { get; init; }
    public required CancellationToken Cancellation { get; init; }
}

/// <summary>
/// Um resolvedor reconhece um tipo de "portão" (encurtador, safelink, redirecionamento por JS…)
/// pelo conteúdo da página e devolve o próximo passo — ou null se não reconhece a página.
/// </summary>
public interface ILinkResolver
{
    string Name { get; }
    Task<Advance?> TryAdvanceAsync(ResolverContext ctx, FetchedPage page);
}

/// <summary>Lançada quando o site exige interação humana real (captcha validado no servidor).</summary>
public sealed class NeedsBrowserException : Exception
{
    public Uri Url { get; }
    public NeedsBrowserException(Uri url, string message) : base(message) => Url = url;
}
