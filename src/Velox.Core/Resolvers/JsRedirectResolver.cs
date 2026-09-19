using System.Text.RegularExpressions;

namespace Velox.Core.Resolvers;

/// <summary>
/// Redirecionamentos que o HTTP não segue sozinho: meta refresh, <c>window.location = …</c>,
/// <c>location.replace(…)</c>, e desembrulho de redirecionadores (google.com/url?url=…).
/// Fica por último na lista: só age quando nenhum resolvedor específico reconheceu a página.
/// </summary>
public sealed class JsRedirectResolver : ILinkResolver
{
    public string Name => "Redirecionamento";

    private static readonly Regex MetaRefresh = new(
        @"<meta[^>]*http-equiv\s*=\s*[""']?refresh[""']?[^>]*content\s*=\s*[""']\s*\d+\s*;\s*url\s*=\s*(?<u>[^""'\s>]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScriptBlock = new(@"<script\b[^>]*>(?<code>[\s\S]*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Conditional = new(@"\bif\s*\(|function\s*\(|=>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsLocation = new(
        @"(?:window\.|top\.|self\.|document\.)?location(?:\.href)?\s*=\s*[""'](?<u>https?://[^""']+)[""']|location\.(?:replace|assign)\(\s*[""'](?<u>https?://[^""']+)[""']\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<Advance?> TryAdvanceAsync(ResolverContext ctx, FetchedPage page)
    {
        var unwrapped = ResolverHttp.Unwrap(page.Url);
        if (unwrapped != page.Url)
            return Task.FromResult<Advance?>(new Advance { Url = unwrapped, Referer = page.Referer, Description = "redirecionador desembrulhado" });

        if (!page.IsHtml || page.Html.Length == 0) return Task.FromResult<Advance?>(null);

        // meta refresh
        var meta = MetaRefresh.Match(page.Html);
        if (meta.Success)
        {
            var u = ResolverHttp.Absolute(page.Url, meta.Groups["u"].Value.Trim('"', '\''));
            if (u != null && u != page.Url)
                return Task.FromResult<Advance?>(new Advance { Url = u, Referer = page.Url, Description = "meta refresh" });
        }

        // redirecionamento por JS: só em blocos <script> curtos e incondicionais, declarados antes
        // de qualquer conteúdo visível (scripts de anúncios/onclick de logos não contam)
        foreach (Match script in ScriptBlock.Matches(page.Html))
        {
            if (!IsEarlyScript(page.Html, script.Index)) break;
            var code = script.Groups["code"].Value;
            if (code.Length > 600 || Conditional.IsMatch(code)) continue;
            var js = JsLocation.Match(code);
            if (!js.Success) continue;
            var u = ResolverHttp.Absolute(page.Url, js.Groups["u"].Value);
            if (u != null && u != page.Url)
                return Task.FromResult<Advance?>(new Advance { Url = u, Referer = page.Url, Description = "redirecionamento por JavaScript" });
        }

        return Task.FromResult<Advance?>(null);
    }

    private static bool IsEarlyScript(string html, int index)
    {
        // só consideramos redirecionamentos declarados antes de qualquer conteúdo visível
        var before = html[..index];
        return !Regex.IsMatch(before, @"<(?:h1|h2|p|article|main|form)\b", RegexOptions.IgnoreCase);
    }
}
