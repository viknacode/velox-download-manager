using System.Text.RegularExpressions;
using System.Web;

namespace Velox.Core.Resolvers;

/// <summary>
/// Desvio por blog com o plugin "WP Safelink" (usado pelo ShrinkMe e outros): a página
/// <c>link.php?link=CODE</c> grava um cookie e manda o visitante para posts aleatórios com
/// um formulário <c>newwpsafelink</c>. O servidor devolve o botão "Go To Url" quando recebe
/// esse formulário por POST na URL de um post — uma única requisição, sem esperar timers.
/// </summary>
public sealed class WpSafelinkResolver : ILinkResolver
{
    public string Name => "WP Safelink";

    public async Task<Advance?> TryAdvanceAsync(ResolverContext ctx, FetchedPage page)
    {
        if (!page.IsHtml && string.IsNullOrEmpty(page.Html)) return null;

        // ---- link.php?link=CODE → redirecionamento JS (via Google) para um post do blog
        var query = HttpUtility.ParseQueryString(page.Url.Query);
        var codeFromQuery = query["link"];
        if (!string.IsNullOrEmpty(codeFromQuery) && page.Url.AbsolutePath.Contains("link.php", StringComparison.OrdinalIgnoreCase))
        {
            var redirect = page.Match(@"location(?:\.href)?\s*=\s*[""'](?<u>https?://[^""']+)[""']");
            if (redirect.Success)
            {
                var post = ResolverHttp.Unwrap(new Uri(redirect.Groups["u"].Value));
                ctx.Log($"Safelink: cookie recebido, indo direto ao passo final em {post.Host}");
                return await SubmitAsync(ctx, post, codeFromQuery, page.Url).ConfigureAwait(false);
            }

            if (page.Contains("Direct Access not Allowed"))
                throw new InvalidOperationException("O safelink recusou o acesso direto (Referer inválido).");
        }

        // ---- já estamos num post com o formulário newwpsafelink
        var form = page.Match(@"<input[^>]*name\s*=\s*[""']newwpsafelink[""'][^>]*value\s*=\s*[""'](?<code>[^""']+)[""']");
        if (form.Success)
            return await SubmitAsync(ctx, page.Url, form.Groups["code"].Value, page.Url).ConfigureAwait(false);

        return null;
    }

    private static async Task<Advance?> SubmitAsync(ResolverContext ctx, Uri post, string code, Uri referer)
    {
        var fields = new[] { new KeyValuePair<string, string>("newwpsafelink", code) };
        var reply = await ctx.Http.PostFormAsync(post, fields, referer, xhr: false, ctx.Cancellation).ConfigureAwait(false);

        var target = ExtractGoToUrl(reply);
        if (target == null)
        {
            // alguns temas exigem um segundo envio (o primeiro só "registra" a visita)
            reply = await ctx.Http.PostFormAsync(reply.Url, fields, reply.Url, xhr: false, ctx.Cancellation).ConfigureAwait(false);
            target = ExtractGoToUrl(reply);
        }

        if (target == null)
            throw new InvalidOperationException("O safelink não liberou o link de saída.");

        ctx.Log($"Safelink liberou: {target.Host}");
        return new Advance { Url = target, Referer = reply.Url, Description = "safelink (newwpsafelink)" };
    }

    private static Uri? ExtractGoToUrl(FetchedPage reply)
    {
        // botão "Go To Url" (id tp-snp2) ou qualquer link externo dentro de #nextPage
        var byId = Regex.Match(reply.Html, @"<a[^>]*href\s*=\s*[""'](?<u>https?://[^""']+)[""'][^>]*>\s*<button[^>]*id\s*=\s*[""']tp-snp2", RegexOptions.IgnoreCase);
        if (byId.Success) return ResolverHttp.Absolute(reply.Url, byId.Groups["u"].Value);

        var nextPage = Regex.Match(reply.Html, @"id\s*=\s*[""']nextPage[""'][\s\S]{0,800}?<a[^>]*href\s*=\s*[""'](?<u>https?://[^""'#]+)[""']", RegexOptions.IgnoreCase);
        if (nextPage.Success)
        {
            var u = ResolverHttp.Absolute(reply.Url, nextPage.Groups["u"].Value);
            if (u != null && !u.Host.Equals(reply.Url.Host, StringComparison.OrdinalIgnoreCase)) return u;
        }

        return null;
    }
}
