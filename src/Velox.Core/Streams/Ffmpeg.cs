using System.Diagnostics;
using System.IO.Compression;

namespace Velox.Core.Streams;

/// <summary>
/// Localiza o ffmpeg (configuração, pasta de ferramentas do Velox ou PATH), baixa a build
/// oficial "essentials" quando pedido e faz remux/mux sem recodificar.
/// </summary>
public static class Ffmpeg
{
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static string ToolsDirectory(string dataDirectory) => Path.Combine(dataDirectory, "tools");
    public static string BundledPath(string dataDirectory) => Path.Combine(ToolsDirectory(dataDirectory), "ffmpeg.exe");

    /// <summary>Caminho do ffmpeg.exe utilizável, ou null.</summary>
    public static string? Locate(string dataDirectory, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;

        var bundled = BundledPath(dataDirectory);
        if (File.Exists(bundled)) return bundled;

        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }

        foreach (var common in new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"ffmpeg\bin\ffmpeg.exe"),
        })
        {
            if (File.Exists(common)) return common;
        }

        return null;
    }

    public static async Task<string?> GetVersionAsync(string ffmpegPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, "-version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var first = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return first?.Replace("ffmpeg version", "").Trim().Split(' ')[0];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Baixa o zip oficial e extrai só o ffmpeg.exe para a pasta de ferramentas.</summary>
    public static async Task<string> InstallAsync(string dataDirectory, HttpClient http, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var tools = ToolsDirectory(dataDirectory);
        Directory.CreateDirectory(tools);
        var zipPath = Path.Combine(tools, "ffmpeg-download.zip");

        using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
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

        var target = BundledPath(dataDirectory);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("ffmpeg.exe não encontrado no pacote baixado.");
            entry.ExtractToFile(target, overwrite: true);
        }

        try { File.Delete(zipPath); } catch { }
        return target;
    }

    /// <summary>Remux sem recodificar: TS→MP4 ou vídeo+áudio separados→MP4/MKV.</summary>
    public static async Task RemuxAsync(string ffmpegPath, string videoPath, string? audioPath, string outputPath, CancellationToken ct)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", videoPath };
        if (audioPath != null) args.AddRange(new[] { "-i", audioPath, "-map", "0:v:0?", "-map", "1:a:0?" });
        args.AddRange(new[] { "-c", "copy" });
        // o muxer MP4 do ffmpeg aplica aac_adtstoasc sozinho quando o áudio vem de TS
        if (outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(outputPath);

        var psi = new ProcessStartInfo(ffmpegPath) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
        var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            var tail = stderr.Length > 400 ? stderr[^400..] : stderr;
            throw new InvalidOperationException("ffmpeg falhou: " + tail.Trim());
        }
    }
}
