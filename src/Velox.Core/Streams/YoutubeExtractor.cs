using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velox.Core.Engine;
using Velox.Core.Models;

namespace Velox.Core.Streams;

/// <summary>Runtime JavaScript que o yt-dlp usa para resolver os desafios do player do YouTube.</summary>
public sealed record JsRuntime(string Name, string Path);

/// <summary>Ferramentas externas usadas pelos downloads de stream.</summary>
public sealed record ToolPaths(string? Ffmpeg, string? YtDlp, JsRuntime? JsRuntime);

public sealed class YoutubeFormat
{
    public required string Id { get; init; }
    public string Ext { get; init; } = "";
    public required string Url { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public string VCodec { get; init; } = "none";
    public string ACodec { get; init; } = "none";
    public double Tbr { get; init; }
    public double Abr { get; init; }
    public long FileSize { get; init; } = -1;
    public string Note { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new();

    public bool HasVideo => VCodec != "none" && !string.IsNullOrEmpty(VCodec);
    public bool HasAudio => ACodec != "none" && !string.IsNullOrEmpty(ACodec);
}

public sealed class YoutubeInfo
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Uploader { get; init; }
    public double DurationSeconds { get; init; }
    public string? Thumbnail { get; init; }
    public bool IsLive { get; init; }
    public List<YoutubeFormat> Formats { get; init; } = new();
}

/// <summary>
/// YouTube não expõe HLS/DASH público: os links do googlevideo têm assinatura cifrada e o
/// desafio "n" de throttling, resolvidos pelo player JS. O yt-dlp faz só a extração (links,
/// qualidades, título) com um runtime JS; o download em si é do Velox — vídeo e áudio em
/// faixas de bytes paralelas, retomáveis — e o ffmpeg junta no fim.
/// </summary>
public static class YoutubeExtractor
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    /// <summary>Tamanho de cada faixa de bytes (o googlevideo limita a velocidade de pedidos muito grandes).</summary>
    public const long ChunkSize = 8 * 1024 * 1024;

    private static readonly Regex IdRegex = new(
        @"(?:(?:www\.|m\.|music\.)?youtube(?:-nocookie)?\.com/(?:watch\?(?:[^#]*&)?v=|shorts/|embed/|live/|v/)|youtu\.be/)([A-Za-z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryGetVideoId(string url, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        var m = IdRegex.Match(url.Trim());
        if (!m.Success) return false;
        id = m.Groups[1].Value;
        return true;
    }

    public static bool IsYoutubeUrl(string url) => TryGetVideoId(url, out _);

    public static string CanonicalUrl(string id) => "https://www.youtube.com/watch?v=" + id;

    // ------------------------------------------------------------------ ferramentas

    public static string ToolsDirectory(string dataDirectory) => Path.Combine(dataDirectory, "tools");
    public static string BundledYtDlpPath(string dataDirectory) => Path.Combine(ToolsDirectory(dataDirectory), "yt-dlp.exe");
    public static string BundledDenoPath(string dataDirectory) => Path.Combine(ToolsDirectory(dataDirectory), "deno.exe");

    public static string? LocateYtDlp(string dataDirectory, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;
        var bundled = BundledYtDlpPath(dataDirectory);
        if (File.Exists(bundled)) return bundled;
        return FindOnPath("yt-dlp.exe")
               ?? FirstExisting(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\yt-dlp.exe"));
    }

    /// <summary>Deno da pasta de ferramentas, senão deno/node/bun no PATH ou em locais comuns.</summary>
    public static JsRuntime? LocateJsRuntime(string dataDirectory)
    {
        var deno = BundledDenoPath(dataDirectory);
        if (File.Exists(deno)) return new JsRuntime("deno", deno);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new (string Name, string? Path)[]
        {
            ("deno", FindOnPath("deno.exe")),
            ("node", FindOnPath("node.exe")),
            ("bun", FindOnPath("bun.exe")),
            ("deno", FirstExisting(Path.Combine(home, @".deno\bin\deno.exe"), Path.Combine(local, @"Microsoft\WinGet\Links\deno.exe"))),
            ("node", FirstExisting(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\node.exe"))),
            ("bun", FirstExisting(Path.Combine(home, @".bun\bin\bun.exe"))),
        };
        foreach (var (name, path) in candidates)
            if (path != null) return new JsRuntime(name, path);
        return null;
    }

    private static string? FindOnPath(string exe)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    public static async Task<string?> GetVersionAsync(string ytDlpPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(ytDlpPath, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return line?.Trim();
        }
        catch { return null; }
    }

    public static async Task<string> InstallYtDlpAsync(string dataDirectory, HttpClient http, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var tools = ToolsDirectory(dataDirectory);
        Directory.CreateDirectory(tools);
        var target = BundledYtDlpPath(dataDirectory);
        var tmp = target + ".download";
        await DownloadFileAsync(http, YtDlpUrl, tmp, progress, ct).ConfigureAwait(false);
        File.Move(tmp, target, overwrite: true);
        return target;
    }

    public static async Task<string> InstallDenoAsync(string dataDirectory, HttpClient http, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var tools = ToolsDirectory(dataDirectory);
        Directory.CreateDirectory(tools);
        var zipPath = Path.Combine(tools, "deno-download.zip");
        await DownloadFileAsync(http, DenoUrl, zipPath, progress, ct).ConfigureAwait(false);

        var target = BundledDenoPath(dataDirectory);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("deno.exe não encontrado no pacote baixado.");
            entry.ExtractToFile(target, overwrite: true);
        }
        try { File.Delete(zipPath); } catch { }
        return target;
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string path, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        var buffer = new byte[256 * 1024];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress?.Report((done, total));
        }
    }

    // ------------------------------------------------------------------ extração

    /// <summary>Roda `yt-dlp -J` e devolve título, duração e formatos com URL direta (https).</summary>
    public static async Task<YoutubeInfo> ExtractAsync(string ytDlpPath, JsRuntime? runtime, string url, CancellationToken ct)
    {
        if (!TryGetVideoId(url, out var id)) throw new DownloadException("Isto não parece um link de vídeo do YouTube.", false);

        var psi = new ProcessStartInfo(ytDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-J", "--no-playlist", "--no-colors", "--no-cache-dir", "--socket-timeout", "20" }) psi.ArgumentList.Add(a);
        if (runtime != null)
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add($"{runtime.Name}:{runtime.Path}");
        }
        psi.ArgumentList.Add(CanonicalUrl(id));

        using var p = Process.Start(psi) ?? throw new DownloadException("Não foi possível iniciar o yt-dlp.", false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));

        var stdoutTask = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = p.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new DownloadException("O yt-dlp demorou demais para responder (tempo limite).", true);
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            var err = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal))
                      ?? stderr.Trim().Split('\n').LastOrDefault() ?? "erro desconhecido";
            err = err.Replace("ERROR: [youtube] ", "").Replace("ERROR: ", "");
            if (err.Length > 300) err = err[..300];
            bool transient = err.Contains("timed out", StringComparison.OrdinalIgnoreCase) || err.Contains("Unable to download", StringComparison.OrdinalIgnoreCase)
                             || err.Contains("HTTP Error 5", StringComparison.Ordinal) || err.Contains("429", StringComparison.Ordinal);
            throw new DownloadException("yt-dlp: " + err, transient);
        }

        return ParseInfo(stdout);
    }

    public static YoutubeInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var formats = new List<YoutubeFormat>();
        if (root.TryGetProperty("formats", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                var url = Str(f, "url");
                if (string.IsNullOrEmpty(url)) continue;
                var protocol = Str(f, "protocol") ?? "";
                if (protocol != "https" && protocol != "http") continue; // m3u8/dash do YouTube ficam de fora: temos os arquivos diretos

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (f.TryGetProperty("http_headers", out var hh) && hh.ValueKind == JsonValueKind.Object)
                    foreach (var kv in hh.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String) headers[kv.Name] = kv.Value.GetString() ?? "";

                formats.Add(new YoutubeFormat
                {
                    Id = Str(f, "format_id") ?? "",
                    Ext = Str(f, "ext") ?? "",
                    Url = url,
                    Width = Int(f, "width"),
                    Height = Int(f, "height"),
                    Fps = Dbl(f, "fps"),
                    VCodec = Str(f, "vcodec") ?? "none",
                    ACodec = Str(f, "acodec") ?? "none",
                    Tbr = Dbl(f, "tbr"),
                    Abr = Dbl(f, "abr"),
                    FileSize = Lng(f, "filesize") is > 0 and var fs ? fs : Lng(f, "filesize_approx"),
                    Note = Str(f, "format_note") ?? "",
                    Headers = headers,
                });
            }
        }

        return new YoutubeInfo
        {
            Id = Str(root, "id") ?? "",
            Title = Str(root, "title") ?? "video",
            Uploader = Str(root, "uploader") ?? Str(root, "channel"),
            DurationSeconds = Dbl(root, "duration"),
            Thumbnail = Str(root, "thumbnail"),
            IsLive = root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True,
            Formats = formats,
        };
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
    private static double Dbl(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static long Lng(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : -1;

    // ------------------------------------------------------------------ qualidades

    /// <summary>
    /// Monta as opções: por resolução/fps um vídeo-only (H.264 de preferência, senão AV1, senão VP9) casado com o
    /// melhor áudio do mesmo contêiner; formatos progressivos (vídeo+áudio) quando não há separados; e "somente áudio".
    /// </summary>
    public static StreamInfo ToStreamInfo(YoutubeInfo info, string sourceUrl)
    {
        var audioM4a = info.Formats.Where(f => f.HasAudio && !f.HasVideo && f.Ext == "m4a" && !f.Id.Contains("drc", StringComparison.OrdinalIgnoreCase))
                                   .OrderByDescending(f => f.Abr > 0 ? f.Abr : f.Tbr).FirstOrDefault();
        var audioOpus = info.Formats.Where(f => f.HasAudio && !f.HasVideo && f.Ext == "webm" && !f.Id.Contains("drc", StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(f => f.Abr > 0 ? f.Abr : f.Tbr).FirstOrDefault();

        var variants = new List<StreamVariant>();
        var videoOnly = info.Formats.Where(f => f.HasVideo && !f.HasAudio && f.Height > 0).ToList();

        foreach (var group in videoOnly.GroupBy(f => (f.Height, Fps: (int)Math.Round(f.Fps))).OrderByDescending(g => g.Key.Height).ThenByDescending(g => g.Key.Fps))
        {
            var pick = group.FirstOrDefault(f => f.VCodec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) && f.Ext == "mp4")
                       ?? group.FirstOrDefault(f => f.VCodec.StartsWith("av01", StringComparison.OrdinalIgnoreCase) && f.Ext == "mp4")
                       ?? group.FirstOrDefault(f => f.Ext == "webm")
                       ?? group.First();
            var audio = pick.Ext == "webm" ? (audioOpus ?? audioM4a) : (audioM4a ?? audioOpus);
            var container = pick.Ext == "webm" && audio?.Ext == "webm" ? "webm" : pick.Ext == "webm" ? "mkv" : "mp4";
            long size = pick.FileSize > 0 ? pick.FileSize + Math.Max(0, audio?.FileSize ?? 0) : -1;

            variants.Add(new StreamVariant
            {
                Id = audio != null ? $"{pick.Id}+{audio.Id}" : pick.Id,
                Label = $"{group.Key.Height}p{(group.Key.Fps > 30 ? group.Key.Fps.ToString() : "")} · {container.ToUpperInvariant()}",
                Width = pick.Width, Height = pick.Height, FrameRate = pick.Fps,
                Bandwidth = (long)((pick.Tbr + (audio?.Tbr ?? 0)) * 1000),
                Codecs = CodecName(pick.VCodec),
                AudioId = audio?.Id,
                AudioLabel = audio != null ? AudioName(audio) : null,
                Container = container,
                SizeBytes = size,
            });
        }

        if (variants.Count == 0)
        {
            foreach (var f in info.Formats.Where(f => f.HasVideo && f.HasAudio && f.Height > 0).OrderByDescending(f => f.Height).ThenByDescending(f => f.Tbr))
            {
                variants.Add(new StreamVariant
                {
                    Id = f.Id,
                    Label = $"{f.Height}p · {f.Ext.ToUpperInvariant()}",
                    Width = f.Width, Height = f.Height, FrameRate = f.Fps,
                    Bandwidth = (long)(f.Tbr * 1000),
                    Codecs = CodecName(f.VCodec),
                    Container = f.Ext,
                    SizeBytes = f.FileSize,
                });
            }
        }

        var bestAudio = audioM4a ?? audioOpus;
        if (bestAudio != null)
        {
            variants.Add(new StreamVariant
            {
                Id = bestAudio.Id,
                Label = "Somente áudio · " + (bestAudio.Ext == "m4a" ? "M4A" : "WebM"),
                Bandwidth = (long)((bestAudio.Abr > 0 ? bestAudio.Abr : bestAudio.Tbr) * 1000),
                Codecs = CodecName(bestAudio.ACodec),
                Container = bestAudio.Ext == "m4a" ? "m4a" : "webm",
                SizeBytes = bestAudio.FileSize,
                AudioLabel = AudioName(bestAudio),
            });
        }

        return new StreamInfo
        {
            Kind = StreamKind.Youtube,
            ManifestUrl = sourceUrl,
            DurationSeconds = info.DurationSeconds,
            IsLive = info.IsLive,
            Variants = variants,
            Title = info.Title,
            Uploader = info.Uploader,
            // o "thumbnail" do yt-dlp costuma ser WebP (o WPF não decodifica); o JPEG mqdefault existe para todo vídeo
            Thumbnail = !string.IsNullOrEmpty(info.Id) ? $"https://i.ytimg.com/vi/{info.Id}/mqdefault.jpg" : info.Thumbnail,
        };
    }

    private static string CodecName(string codec)
    {
        var c = codec.ToLowerInvariant();
        return c.StartsWith("avc1") ? "H.264" : c.StartsWith("av01") ? "AV1" : c.StartsWith("vp09") || c.StartsWith("vp9") ? "VP9"
             : c.StartsWith("mp4a") ? "AAC" : c.StartsWith("opus") ? "Opus" : codec;
    }

    private static string AudioName(YoutubeFormat a)
    {
        var kbps = (int)Math.Round(a.Abr > 0 ? a.Abr : a.Tbr);
        return kbps > 0 ? $"{CodecName(a.ACodec)} {kbps} kbps" : CodecName(a.ACodec);
    }

    // ------------------------------------------------------------------ plano de download

    /// <summary>Extrai de novo (links expiram em ~6 h) e divide vídeo e áudio em faixas de bytes.</summary>
    public static async Task<StreamPlan> BuildPlanAsync(HttpClient http, DownloadItem item, string ytDlpPath, JsRuntime? runtime, CancellationToken ct)
    {
        var info = await ExtractAsync(ytDlpPath, runtime, item.Url, ct).ConfigureAwait(false);
        if (info.IsLive) throw new DownloadException("Transmissão ao vivo — só vídeos já publicados podem ser baixados.", false);
        var streamInfo = ToStreamInfo(info, item.Url);
        if (streamInfo.Variants.Count == 0) throw new DownloadException("O YouTube não devolveu nenhum formato baixável para este vídeo.", false);

        var variant = streamInfo.Variants.FirstOrDefault(v => v.Id == item.VariantId) ?? streamInfo.Best!;
        var ids = variant.Id.Split('+');
        var videoFmt = info.Formats.First(f => f.Id == ids[0]);
        var audioFmt = ids.Length > 1 ? info.Formats.First(f => f.Id == ids[1]) : null;

        // cabeçalhos que o yt-dlp usaria (User-Agent do cliente que gerou os links)
        foreach (var kv in videoFmt.Headers)
            if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) item.Headers["User-Agent"] = kv.Value;

        var video = await ChunkAsync(http, item, videoFmt, ct).ConfigureAwait(false);
        var audio = audioFmt != null ? await ChunkAsync(http, item, audioFmt, ct).ConfigureAwait(false) : new List<MediaSegment>();

        return new StreamPlan
        {
            Kind = StreamKind.Youtube,
            Variant = variant,
            Video = video,
            Audio = audio,
            VideoContainer = variant.Container ?? videoFmt.Ext,
            DurationSeconds = info.DurationSeconds,
            IsCompleteFile = true,
            Title = info.Title,
        };
    }

    private static async Task<List<MediaSegment>> ChunkAsync(HttpClient http, DownloadItem item, YoutubeFormat fmt, CancellationToken ct)
    {
        long size = fmt.FileSize;
        // tamanho exato é obrigatório para dividir em faixas: confirma no servidor (Content-Range)
        using (var req = RequestBuilder.Build(fmt.Url, item.Headers, null))
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode == 403) throw new DownloadException("O YouTube recusou o link (403) — tente novamente em instantes.", true);
            if (!resp.IsSuccessStatusCode) throw new DownloadException($"O servidor do YouTube respondeu {(int)resp.StatusCode}.", (int)resp.StatusCode >= 500);
            var total = resp.Content.Headers.ContentRange?.Length;
            if (total is > 0) size = total.Value;
            else if (resp.StatusCode == System.Net.HttpStatusCode.OK && resp.Content.Headers.ContentLength is > 0) size = resp.Content.Headers.ContentLength.Value;
        }
        if (size <= 0) throw new DownloadException("Não foi possível descobrir o tamanho do arquivo no YouTube.", true);

        var list = new List<MediaSegment>();
        var url = new Uri(fmt.Url);
        int index = 0;
        for (long start = 0; start < size; start += ChunkSize)
        {
            long len = Math.Min(ChunkSize, size - start);
            list.Add(new MediaSegment { Index = index++, Url = url, RangeStart = start, RangeLength = len });
        }
        return list;
    }
}
