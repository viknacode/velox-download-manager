using System.Text;
using Velox.Core.Engine;
using Velox.Core.Models;

namespace Velox.Core.Streams;

/// <summary>
/// Reconhece manifestos HLS/DASH (por content-type, extensão ou conteúdo), lista as
/// variantes de qualidade e monta o plano de download (listas de segmentos por faixa).
/// </summary>
public static class StreamProbe
{
    public static bool LooksLikeManifestUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var path = u.AbsolutePath;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsManifestContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var t = contentType.ToLowerInvariant();
        return t.Contains("mpegurl") || t.Contains("dash+xml") || t.Contains("vnd.apple.mpegurl");
    }

    /// <summary>Baixa um texto (manifesto/playlist) com os cabeçalhos do item.</summary>
    public static async Task<(string Text, Uri FinalUrl, string? ContentType)> FetchTextAsync(HttpClient http, Uri url,
        IDictionary<string, string>? headers, string? referer, CancellationToken ct)
    {
        using var req = RequestBuilder.Build(url.ToString(), headers, referer);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new DownloadException($"O servidor respondeu {(int)resp.StatusCode} ao pedir o manifesto.", (int)resp.StatusCode >= 500);
        var bytes = await resp.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (bytes.Length > 8 * 1024 * 1024) throw new DownloadException("Manifesto grande demais.", false);
        return (Encoding.UTF8.GetString(bytes), resp.RequestMessage?.RequestUri ?? url, resp.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Tenta interpretar a URL como manifesto. Retorna null se não for HLS/DASH.</summary>
    public static async Task<StreamInfo?> ProbeAsync(HttpClient http, string url, IDictionary<string, string>? headers, string? referer,
        string? knownContentType, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!LooksLikeManifestUrl(url) && !IsManifestContentType(knownContentType)) return null;

        var (text, finalUrl, contentType) = await FetchTextAsync(http, uri, headers, referer, ct).ConfigureAwait(false);
        var info = Analyze(text, finalUrl, contentType);

        // master HLS não traz duração nem sinaliza live/criptografia: espia a playlist da melhor variante
        if (info is { Kind: StreamKind.Hls, DurationSeconds: 0 } && info.Best != null && Uri.TryCreate(info.Best.Id, UriKind.Absolute, out var mediaUri))
        {
            try
            {
                var (mediaText, mediaUrl, _) = await FetchTextAsync(http, mediaUri, headers, referer, ct).ConfigureAwait(false);
                var media = HlsParser.ParseMedia(mediaText, mediaUrl);
                info.DurationSeconds = media.Duration;
                info.IsLive = media.IsLive;
                info.IsEncrypted |= media.IsEncrypted;
                info.DrmSystem ??= media.UnsupportedKeyMethod;
            }
            catch { /* informativo apenas; o download real revalida */ }
        }
        return info;
    }

    public static StreamInfo? Analyze(string text, Uri finalUrl, string? contentType)
    {
        if (HlsParser.LooksLikePlaylist(text)) return AnalyzeHls(text, finalUrl);
        if (DashParser.LooksLikeMpd(text)) return AnalyzeDash(text, finalUrl);
        return null;
    }

    private static StreamInfo AnalyzeHls(string text, Uri url)
    {
        if (HlsParser.IsMaster(text))
        {
            var (variants, renditions) = HlsParser.ParseMaster(text, url);
            var list = new List<StreamVariant>();
            foreach (var v in variants.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth))
            {
                HlsParser.MediaRendition? audio = null;
                if (v.AudioGroup != null)
                    audio = renditions.Where(r => r.GroupId == v.AudioGroup && r.Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase) && r.Url != null)
                                      .OrderByDescending(r => r.Default).FirstOrDefault();
                list.Add(new StreamVariant
                {
                    Id = v.Url.ToString(),
                    Label = LabelFor(v.Height, v.Width, v.Bandwidth, v.FrameRate),
                    Width = v.Width, Height = v.Height, Bandwidth = v.Bandwidth, Codecs = v.Codecs, FrameRate = v.FrameRate,
                    AudioId = audio?.Url?.ToString(),
                    AudioLabel = audio != null ? (audio.Name + (audio.Language != null ? $" ({audio.Language})" : "")) : null
                });
            }
            // remove duplicatas de mesma resolução/bitrate (variantes idênticas em codecs diferentes ficam)
            return new StreamInfo { Kind = StreamKind.Hls, ManifestUrl = url.ToString(), Variants = list, DurationSeconds = 0 };
        }

        var media = HlsParser.ParseMedia(text, url);
        return new StreamInfo
        {
            Kind = StreamKind.Hls,
            ManifestUrl = url.ToString(),
            DurationSeconds = media.Duration,
            IsLive = media.IsLive,
            IsEncrypted = media.IsEncrypted,
            DrmSystem = media.UnsupportedKeyMethod,
            Variants = { new StreamVariant { Id = url.ToString(), Label = "Qualidade única", Bandwidth = 0 } }
        };
    }

    /// <summary>"AAC 128 kbps (en)" em vez do codec cru "mp4a.40.2".</summary>
    private static string AudioLabelFor(DashParser.Representation audio)
    {
        var c = (audio.Codecs ?? "").ToLowerInvariant();
        var codec = c.StartsWith("mp4a") ? "AAC" : c.StartsWith("ec-3") ? "Dolby Digital+" : c.StartsWith("ac-3") ? "Dolby Digital"
                  : c.StartsWith("opus") ? "Opus" : c.StartsWith("vorbis") ? "Vorbis" : c.StartsWith("flac") ? "FLAC" : "áudio";
        var label = audio.Bandwidth > 0 ? $"{codec} {audio.Bandwidth / 1000} kbps" : codec;
        return audio.Language != null && audio.Language != "und" ? $"{label} ({audio.Language})" : label;
    }

    private static StreamInfo AnalyzeDash(string text, Uri url)
    {
        var mpd = DashParser.Parse(text, url);
        var bestAudio = mpd.Audio.OrderByDescending(a => a.Bandwidth).FirstOrDefault();
        var list = new List<StreamVariant>();
        foreach (var v in mpd.Video.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth))
        {
            list.Add(new StreamVariant
            {
                Id = v.Id,
                Label = LabelFor(v.Height, v.Width, v.Bandwidth, v.FrameRate) + (v.Container == "webm" ? " · webm" : ""),
                Width = v.Width, Height = v.Height, Bandwidth = v.Bandwidth, Codecs = v.Codecs, FrameRate = v.FrameRate,
                AudioId = bestAudio?.Id,
                AudioLabel = bestAudio != null ? AudioLabelFor(bestAudio) : null
            });
        }
        if (list.Count == 0 && bestAudio != null)
            list.Add(new StreamVariant { Id = bestAudio.Id, Label = "Somente áudio", Bandwidth = bestAudio.Bandwidth });

        return new StreamInfo
        {
            Kind = StreamKind.Dash,
            ManifestUrl = url.ToString(),
            DurationSeconds = mpd.DurationSeconds,
            IsLive = mpd.IsDynamic,
            IsEncrypted = mpd.Representations.Any(r => r.IsProtected),
            DrmSystem = mpd.Representations.Any(r => r.IsProtected) ? "DRM" : null,
            Variants = list
        };
    }

    public static string LabelFor(int height, int width, long bandwidth, double fps)
    {
        var parts = new List<string>();
        if (height > 0) parts.Add(height >= 2160 ? "4K" : $"{height}p");
        else if (width > 0) parts.Add($"{width}w");
        if (fps >= 48) parts.Add($"{Math.Round(fps)}fps");
        if (bandwidth > 0) parts.Add(bandwidth >= 1_000_000 ? $"{bandwidth / 1_000_000.0:0.#} Mbps" : $"{bandwidth / 1000} kbps");
        return parts.Count > 0 ? string.Join(" · ", parts) : "padrão";
    }

    /// <summary>Resolve a variante escolhida em listas concretas de segmentos.</summary>
    public static async Task<StreamPlan> BuildPlanAsync(HttpClient http, DownloadItem item, CancellationToken ct)
    {
        var manifestUri = new Uri(item.Url);
        var (text, finalUrl, contentType) = await FetchTextAsync(http, manifestUri, item.Headers, item.Referer, ct).ConfigureAwait(false);

        if (HlsParser.LooksLikePlaylist(text))
        {
            Uri mediaUrl = finalUrl;
            Uri? audioUrl = null;
            StreamVariant variant;

            if (HlsParser.IsMaster(text))
            {
                var info = AnalyzeHls(text, finalUrl);
                variant = info.Variants.FirstOrDefault(v => v.Id == item.VariantId) ?? info.Best
                          ?? throw new DownloadException("A playlist master não tem variantes utilizáveis.", false);
                mediaUrl = new Uri(variant.Id);
                if (variant.AudioId != null) audioUrl = new Uri(variant.AudioId);
            }
            else
            {
                variant = new StreamVariant { Id = finalUrl.ToString(), Label = item.VariantLabel ?? "Qualidade única" };
            }

            var (mediaText, mediaFinal, _) = mediaUrl == finalUrl ? (text, finalUrl, contentType)
                : await FetchTextAsync(http, mediaUrl, item.Headers, item.Referer, ct).ConfigureAwait(false);
            var media = HlsParser.ParseMedia(mediaText, mediaFinal);
            if (media.IsLive) throw new DownloadException("Transmissão ao vivo (sem EXT-X-ENDLIST) — só streams sob demanda são suportados.", false);
            if (media.UnsupportedKeyMethod != null) throw new DownloadException($"Stream protegido ({media.UnsupportedKeyMethod}) — não é possível baixar.", false);
            if (media.Segments.Count == 0) throw new DownloadException("A playlist não tem segmentos.", false);

            var video = new List<MediaSegment>();
            if (media.Init != null) video.Add(media.Init);
            video.AddRange(media.Segments);

            var audio = new List<MediaSegment>();
            if (audioUrl != null)
            {
                var (audioText, audioFinal, _) = await FetchTextAsync(http, audioUrl, item.Headers, item.Referer, ct).ConfigureAwait(false);
                var ap = HlsParser.ParseMedia(audioText, audioFinal);
                if (ap.UnsupportedKeyMethod != null) throw new DownloadException($"Áudio protegido ({ap.UnsupportedKeyMethod}).", false);
                if (ap.Init != null) audio.Add(ap.Init);
                audio.AddRange(ap.Segments);
            }

            return new StreamPlan
            {
                Kind = StreamKind.Hls,
                Variant = variant,
                Video = video,
                Audio = audio,
                VideoContainer = media.IsFmp4 || media.Segments.Any(s => s.Url.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) || s.Url.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ? "mp4" : "ts",
                DurationSeconds = media.Duration
            };
        }

        if (DashParser.LooksLikeMpd(text))
        {
            var mpd = DashParser.Parse(text, finalUrl);
            if (mpd.IsDynamic) throw new DownloadException("MPD dinâmico (ao vivo) — só streams sob demanda são suportados.", false);

            var videoRep = mpd.Video.FirstOrDefault(r => r.Id == item.VariantId)
                           ?? mpd.Video.OrderByDescending(r => r.Height).ThenByDescending(r => r.Bandwidth).FirstOrDefault();
            var audioRep = mpd.Audio.OrderByDescending(a => a.Bandwidth).FirstOrDefault();
            if (videoRep == null && audioRep == null) throw new DownloadException("O MPD não tem representações de vídeo/áudio.", false);
            if ((videoRep?.IsProtected ?? false) || (audioRep?.IsProtected ?? false))
                throw new DownloadException("Stream protegido por DRM — não é possível baixar.", false);

            var video = new List<MediaSegment>();
            var audio = new List<MediaSegment>();
            if (videoRep != null) { if (videoRep.Init != null) video.Add(videoRep.Init); video.AddRange(videoRep.Segments); }
            if (audioRep != null) { if (audioRep.Init != null) audio.Add(audioRep.Init); audio.AddRange(audioRep.Segments); }

            // sem vídeo: trata o áudio como faixa principal
            if (video.Count == 0) { video = audio; audio = new List<MediaSegment>(); }

            var main = videoRep ?? audioRep!;
            return new StreamPlan
            {
                Kind = StreamKind.Dash,
                Variant = new StreamVariant
                {
                    Id = main.Id, Label = item.VariantLabel ?? LabelFor(main.Height, main.Width, main.Bandwidth, main.FrameRate),
                    Width = main.Width, Height = main.Height, Bandwidth = main.Bandwidth, Codecs = main.Codecs,
                    AudioId = audio.Count > 0 ? audioRep!.Id : null
                },
                Video = video,
                Audio = audio,
                VideoContainer = main.Container,
                DurationSeconds = mpd.DurationSeconds
            };
        }

        throw new DownloadException("A URL não é uma playlist HLS nem um manifesto DASH.", false);
    }
}
