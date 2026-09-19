using System.Text.Json;
using System.Text.RegularExpressions;

namespace Velox.Core.Resolvers;

/// <summary>
/// Encurtadores baseados no script AdLinkFly (ShrinkMe, ShrinkEarn, Clk.sh, GPLinks, DropLink…).
/// Reconhece dois estados da página:
///  1) "verificação humana": o callback do captcha apenas monta um link previsível
///     (<c>getRandomLink() + "?link=" + data-link</c>) — seguimos direto;
///  2) formulário <c>#go-link</c> (<c>ad_form_data</c>, <c>_csrfToken</c>…): enviamos como o
///     próprio tema faz (AJAX para <c>/links/go</c>) e lemos o JSON com a URL de destino.
/// </summary>
public sealed class AdLinkFlyResolver : ILinkResolver
{
    public string Name => "AdLinkFly";

    public async Task<Advance?> TryAdvanceAsync(ResolverContext ctx, FetchedPage page)
    {
        if (!page.IsHtml) return null;

        // ---- estado 2: formulário de saída
        var form = ResolverHttp.ParseForm(page.Html, @"id\s*=\s*[""']go-link[""']");
        if (form != null && form.Value.Fields.Any(f => f.Key == "ad_form_data"))
            return await SubmitGoFormAsync(ctx, page, form.Value).ConfigureAwait(false);

        // ---- estado 1: portão de "verificação humana" com link previsível
        var verification = page.Match(@"id\s*=\s*[""']div-human-verification[""'][^>]*data-link\s*=\s*[""'](?<code>[^""']+)[""']");
        if (verification.Success)
        {
            var code = verification.Groups["code"].Value;
            var links = page.Match(@"const\s+links\s*=\s*\[(?<list>[\s\S]*?)\]");
            var target = links.Success
                ? Regex.Matches(links.Groups["list"].Value, @"[""'](?<u>https?://[^""']+)[""']").Select(m => m.Groups["u"].Value).FirstOrDefault()
                : null;

            if (target != null)
            {
                var next = new Uri(target.Contains('?') ? $"{target}&link={Uri.EscapeDataString(code)}" : $"{target}?link={Uri.EscapeDataString(code)}");
                ctx.Log($"Portão de verificação: seguindo para {next.Host}");
                return new Advance { Url = next, Referer = page.Url, Description = "portão de verificação (link previsível)" };
            }

            // variante em que o próprio div já contém o link
            var anchor = page.Match(@"id\s*=\s*[""']div-human-verification[""'][\s\S]{0,600}?<a[^>]*href\s*=\s*[""'](?<u>https?://[^""']+)[""']");
            if (anchor.Success)
                return new Advance { Url = new Uri(anchor.Groups["u"].Value), Referer = page.Url, Description = "portão de verificação" };

            throw new NeedsBrowserException(page.Url, "Este encurtador exige resolver um captcha no navegador.");
        }

        return null;
    }

    private static async Task<Advance?> SubmitGoFormAsync(ResolverContext ctx, FetchedPage page,
        (string? Action, string? Method, List<KeyValuePair<string, string>> Fields) form)
    {
        var action = ResolverHttp.Absolute(page.Url, string.IsNullOrWhiteSpace(form.Action) ? "/links/go" : form.Action)
                     ?? new Uri(page.Url, "/links/go");

        int counter = 0;
        var cv = page.Match(@"""counter_value""\s*:\s*""?(?<n>\d+)");
        if (cv.Success) int.TryParse(cv.Groups["n"].Value, out counter);

        // o servidor valida o tempo do contador (~12 s): esperar antes evita um "Bad Request"
        if (counter > 0 && counter <= 60)
        {
            ctx.Log($"Aguardando o contador do encurtador ({counter} s)…");
            await Task.Delay(TimeSpan.FromSeconds(counter + 1), ctx.Cancellation).ConfigureAwait(false);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var reply = await ctx.Http.PostFormAsync(action, form.Fields, page.Url, xhr: true, ctx.Cancellation).ConfigureAwait(false);

            var url = ExtractUrl(reply, out var message);
            if (url != null)
            {
                ctx.Log($"Formulário de saída aceito ({message ?? "ok"})");
                return new Advance { Url = url, Referer = page.Url, Description = "formulário /links/go" };
            }

            // servidor exige o tempo do contador — espera e tenta uma vez mais
            if (attempt == 0)
            {
                ctx.Log($"Servidor recusou ({message ?? "sem detalhes"}); tentando de novo em 5 s…");
                await Task.Delay(TimeSpan.FromSeconds(5), ctx.Cancellation).ConfigureAwait(false);
                continue;
            }

            if (message != null && Regex.IsMatch(message, "captcha", RegexOptions.IgnoreCase))
                throw new NeedsBrowserException(page.Url, "O encurtador exige captcha validado no servidor.");

            throw new InvalidOperationException("O encurtador recusou o formulário de saída" + (message != null ? $": {message}" : "."));
        }

        return null;
    }

    private static Uri? ExtractUrl(FetchedPage reply, out string? message)
    {
        message = null;
        var body = reply.Html.Trim();

        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String) message = msg.GetString();
                if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri))
                    return uri;
                if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String) message ??= st.GetString();
            }
            catch { }
            return null;
        }

        // algumas variantes respondem HTML com redirecionamento ou já na URL final
        if (!reply.IsHtml && reply.Url.Host != null && reply.Status == System.Net.HttpStatusCode.OK && string.IsNullOrEmpty(body))
            return reply.Url;

        var meta = Regex.Match(body, @"http-equiv\s*=\s*[""']refresh[""'][^>]*url\s*=\s*[""']?(?<u>https?://[^""'\s>]+)", RegexOptions.IgnoreCase);
        if (meta.Success) return new Uri(meta.Groups["u"].Value);

        var js = Regex.Match(body, @"location(?:\.href)?\s*=\s*[""'](?<u>https?://[^""']+)[""']", RegexOptions.IgnoreCase);
        if (js.Success) return new Uri(js.Groups["u"].Value);

        return null;
    }
}
