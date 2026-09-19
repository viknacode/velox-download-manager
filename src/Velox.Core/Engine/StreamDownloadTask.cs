using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using Velox.Core.Models;
using Velox.Core.Services;
using Velox.Core.Streams;

namespace Velox.Core.Engine;

/// <summary>
/// Baixa um stream HLS/DASH sob demanda: resolve o manifesto, baixa os segmentos em paralelo
/// (cada um retomável, guardado em &lt;arquivo&gt;.vxparts), descriptografa AES-128, concatena
/// na ordem e, com ffmpeg disponível, remuxa para MP4 (juntando vídeo + áudio separados).
/// </summary>
internal sealed class StreamDownloadTask : IDownloadTask
{
    private readonly DownloadItem _item;
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly BandwidthLimiter _limiter;
    private readonly string? _ffmpeg;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, byte[]> _keys = new();
    private readonly object _keyLock = new();

    private long _bytes;             // bytes baixados nesta sessão + já existentes em disco
    private long _lastBytes;
    private int _done;               // segmentos concluídos (inclui os já existentes)
    private int _active;
    private int _retrying;
    private volatile bool _pauseRequested;
    private DownloadException? _fatal;
    private string _partsDir = "";
    private int _audioOffset;

    public DownloadItem Item => _item;
    public Task Completion { get; }

    public StreamDownloadTask(DownloadItem item, HttpClient http, AppSettings settings, BandwidthLimiter limiter, string? ffmpegPath)
    {
        _item = item;
        _http = http;
        _settings = settings;
        _limiter = limiter;
        _ffmpeg = ffmpegPath;
        // mantém o progresso anterior visível enquanto o manifesto é rebaixado na retomada
        _done = item.StreamSegmentsDone;
        _bytes = item.DownloadedBytes;
        _lastBytes = _bytes;
        Completion = Task.Run(RunAsync);
    }

    public void Pause()
    {
        _pauseRequested = true;
        _cts.Cancel();
    }

    public void Tick(double dtSeconds)
    {
        long now = Volatile.Read(ref _bytes);
        double instant = dtSeconds > 0 ? (now - _lastBytes) / dtSeconds : 0;
        _lastBytes = now;
        _item.Speed = _item.Speed <= 0 ? instant : _item.Speed * 0.55 + instant * 0.45;
        if (_item.Speed < 1) _item.Speed = 0;
        _item.DownloadedBytes = now;
        _item.StreamSegmentsDone = Volatile.Read(ref _done);
        _item.ActiveConnections = Volatile.Read(ref _active);
        _item.RetryingConnections = Volatile.Read(ref _retrying);
        if (_item.Status == DownloadStatus.Downloading) _item.ElapsedSeconds += dtSeconds;

        // estimativa de tamanho total: média dos segmentos concluídos × total
        int done = _item.StreamSegmentsDone;
        if (done >= 3 && _item.StreamSegmentsTotal > 0 && now > 0)
        {
            _item.TotalSize = (long)((double)now / done * _item.StreamSegmentsTotal);
            _item.TotalIsEstimate = true;
        }
        _item.PushSpeedSample(_item.Speed);
    }

    // ------------------------------------------------------------------ ciclo principal

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            _item.Status = DownloadStatus.Connecting;
            _item.ErrorMessage = null;
            _item.Note = null;

            var plan = await StreamProbe.BuildPlanAsync(_http, _item, ct).ConfigureAwait(false);
            _item.Kind = plan.Kind;
            _item.VariantLabel ??= plan.Variant.Label;
            _item.VariantId ??= plan.Variant.Id;
            _item.StreamSegmentsTotal = plan.Video.Count + plan.Audio.Count;
            _item.SupportsResume = true;
            var states = new byte[_item.StreamSegmentsTotal];
            _audioOffset = plan.Video.Count;

            Directory.CreateDirectory(_item.Directory);
            _partsDir = _item.FullPath + ".vxparts";
            Directory.CreateDirectory(_partsDir);

            // retomada: conta o que já está no disco
            int done = 0; long bytes = 0;
            int slot = 0;
            foreach (var (seg, prefix) in plan.Video.Select(s => (s, "v")).Concat(plan.Audio.Select(s => (s, "a"))))
            {
                var path = PartPath(seg, prefix);
                if (File.Exists(path))
                {
                    done++;
                    bytes += new FileInfo(path).Length;
                    states[slot] = 2;
                }
                slot++;
            }
            _item.StreamStates = states;
            _done = done; _bytes = bytes;
            _lastBytes = _bytes;
            _item.DownloadedBytes = _bytes;
            _item.StreamSegmentsDone = _done;

            _item.Status = DownloadStatus.Downloading;
            await DownloadListAsync(plan.Video, "v", ct).ConfigureAwait(false);
            await DownloadListAsync(plan.Audio, "a", ct).ConfigureAwait(false);

            if (_fatal != null) throw _fatal;
            ct.ThrowIfCancellationRequested();

            _item.Status = DownloadStatus.Merging;
            _item.Speed = 0;
            await AssembleAsync(plan, ct).ConfigureAwait(false);

            _item.Status = DownloadStatus.Completed;
            _item.CompletedAt = DateTime.Now;
            _item.TotalSize = File.Exists(_item.FullPath) ? new FileInfo(_item.FullPath).Length : _bytes;
            _item.TotalIsEstimate = false;
            _item.DownloadedBytes = _item.TotalSize;
            _item.StreamSegmentsDone = _item.StreamSegmentsTotal;
        }
        catch (OperationCanceledException) when (_pauseRequested && _fatal == null)
        {
            _item.Status = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            var err = _fatal ?? ex;
            _item.Status = DownloadStatus.Failed;
            _item.ErrorMessage = err.Message;
            _item.LastErrorRetryable = err is not DownloadException de || de.Retryable;
        }
        finally
        {
            _item.Speed = 0;
            _item.ActiveConnections = 0;
            _item.RetryingConnections = 0;
            _item.DownloadedBytes = Math.Max(_item.DownloadedBytes, Volatile.Read(ref _bytes));
            _item.StreamSegmentsDone = Volatile.Read(ref _done);
        }
    }

    private string PartPath(MediaSegment seg, string prefix) =>
        Path.Combine(_partsDir, seg.IsInit ? $"{prefix}-init.bin" : $"{prefix}-{seg.Index:D6}.bin");

    // ------------------------------------------------------------------ download dos segmentos

    private async Task DownloadListAsync(List<MediaSegment> segments, string prefix, CancellationToken ct)
    {
        if (segments.Count == 0) return;
        int parallel = Math.Clamp(_item.MaxConnections > 0 ? _item.MaxConnections : _settings.StreamConnections, 1, 16);
        int slotBase = prefix == "a" ? _audioOffset : 0;
        using var gate = new SemaphoreSlim(parallel, parallel);
        var tasks = new List<Task>(segments.Count);

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var path = PartPath(seg, prefix);
            if (File.Exists(path)) continue;

            await gate.WaitAsync(ct).ConfigureAwait(false);
            if (_fatal != null) { gate.Release(); break; }

            var s = seg;
            int slot = slotBase + i;
            tasks.Add(Task.Run(async () =>
            {
                var states = _item.StreamStates;
                try
                {
                    if (states != null && slot < states.Length) states[slot] = 1;
                    await DownloadSegmentWithRetryAsync(s, path, ct).ConfigureAwait(false);
                    if (states != null && slot < states.Length) states[slot] = 2;
                }
                catch (OperationCanceledException) { if (states != null && slot < states.Length) states[slot] = 0; }
                catch (DownloadException ex)
                {
                    Interlocked.CompareExchange(ref _fatal, ex, null);
                    _cts.Cancel();
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _fatal, new DownloadException(ex.Message, true, ex), null);
                    _cts.Cancel();
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task DownloadSegmentWithRetryAsync(MediaSegment seg, string path, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadSegmentOnceAsync(seg, path, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _done);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException or OperationCanceledException)
            {
                attempt++;
                if (attempt > _settings.MaxRetriesPerSegment)
                    throw new DownloadException($"Segmento {seg.Index + 1} falhou após {attempt - 1} tentativas: {UrlProber.Describe(ex)}", true, ex);
                _item.RetryCount++;
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5))));
                Interlocked.Increment(ref _retrying);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _retrying); }
            }
        }
    }

    private async Task DownloadSegmentOnceAsync(MediaSegment seg, string path, CancellationToken ct)
    {
        using var req = RequestBuilder.Build(seg.Url.ToString(), _item.Headers, _item.Referer);
        if (seg.RangeStart != null && seg.RangeLength != null)
            req.Headers.Range = new RangeHeaderValue(seg.RangeStart.Value, seg.RangeStart.Value + seg.RangeLength.Value - 1);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.ReadTimeoutSeconds, 5, 600));
        timeoutCts.CancelAfter(timeout);

        Interlocked.Increment(ref _active);
        try
        {
            await FetchSegmentBodyAsync(req, seg, path, timeoutCts, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task FetchSegmentBodyAsync(HttpRequestMessage req, MediaSegment seg, string path, CancellationTokenSource timeoutCts, TimeSpan timeout, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Tempo limite ao pedir o segmento.");
        }

        using (resp)
        {
            int status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                if (status >= 500 || status == 408 || status == 429) throw new IOException($"Servidor respondeu {status} para um segmento.");
                if (status == 403 || status == 401) throw new DownloadException($"Segmento negado ({status}) — o link do stream pode ter expirado. Abra o vídeo de novo e tente novamente.", false);
                throw new DownloadException($"Servidor respondeu {status} para um segmento.", false);
            }

            timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream(resp.Content.Headers.ContentLength is long len && len > 0 && len < int.MaxValue ? (int)len : 1 << 20);

            var buffer = new byte[128 * 1024];
            while (true)
            {
                timeoutCts.CancelAfter(timeout);
                int read;
                try { read = await stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Tempo limite de leitura do segmento."); }
                timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read <= 0) break;

                await _limiter.ThrottleAsync(read, ct).ConfigureAwait(false);
                ms.Write(buffer, 0, read);
                Interlocked.Add(ref _bytes, read);
            }

            var data = ms.ToArray();
            if (seg.Key != null) data = await DecryptAsync(seg.Key, data, ct).ConfigureAwait(false);

            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
    }

    private async Task<byte[]> DecryptAsync(SegmentKey key, byte[] data, CancellationToken ct)
    {
        var keyBytes = await GetKeyAsync(key.Url, ct).ConfigureAwait(false);
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = key.Iv ?? new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        try
        {
            return dec.TransformFinalBlock(data, 0, data.Length);
        }
        catch (CryptographicException)
        {
            // alguns servidores não preenchem o último bloco: tenta sem padding
            aes.Padding = PaddingMode.None;
            using var dec2 = aes.CreateDecryptor();
            int usable = data.Length - data.Length % 16;
            return dec2.TransformFinalBlock(data, 0, usable);
        }
    }

    private async Task<byte[]> GetKeyAsync(Uri url, CancellationToken ct)
    {
        var k = url.ToString();
        lock (_keyLock)
        {
            if (_keys.TryGetValue(k, out var cached)) return cached;
        }

        using var req = RequestBuilder.Build(k, _item.Headers, _item.Referer);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new DownloadException($"Não foi possível obter a chave de descriptografia ({(int)resp.StatusCode}).", (int)resp.StatusCode >= 500);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length != 16) throw new DownloadException("Chave AES-128 inválida (tamanho diferente de 16 bytes).", false);

        lock (_keyLock) _keys[k] = bytes;
        return bytes;
    }

    // ------------------------------------------------------------------ montagem

    private async Task AssembleAsync(StreamPlan plan, CancellationToken ct)
    {
        string videoExt = plan.VideoContainer == "webm" ? ".webm" : plan.VideoContainer == "mp4" ? ".mp4" : ".ts";
        var videoOut = Path.Combine(_partsDir, "video" + videoExt);
        await ConcatAsync(plan.Video, "v", videoOut, ct).ConfigureAwait(false);

        string? audioOut = null;
        if (plan.Audio.Count > 0)
        {
            var first = plan.Audio.First(s => !s.IsInit);
            var aExt = first.Url.AbsolutePath.EndsWith(".aac", StringComparison.OrdinalIgnoreCase) ? ".aac"
                     : first.Url.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? ".ts"
                     : plan.VideoContainer == "webm" ? ".webm" : ".m4a";
            audioOut = Path.Combine(_partsDir, "audio" + aExt);
            await ConcatAsync(plan.Audio, "a", audioOut, ct).ConfigureAwait(false);
        }

        var baseName = Path.GetFileNameWithoutExtension(_item.FileName);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "video";

        if (_ffmpeg != null)
        {
            var finalExt = plan.VideoContainer == "webm" ? ".webm" : ".mp4";
            var finalName = FileNameHelper.MakeUnique(_item.Directory, baseName + finalExt);
            var finalPath = Path.Combine(_item.Directory, finalName);
            var tmpOut = Path.Combine(_partsDir, "muxed" + finalExt);

            try
            {
                await Ffmpeg.RemuxAsync(_ffmpeg, videoOut, audioOut, tmpOut, ct).ConfigureAwait(false);
                File.Move(tmpOut, finalPath, overwrite: false);
                _item.FileName = finalName;
                _item.Note = audioOut != null ? "Vídeo e áudio juntados com ffmpeg" : (videoExt == ".ts" ? "Convertido de TS para MP4 com ffmpeg" : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ffmpeg falhou: entrega o que dá sem remux
                _item.Note = "ffmpeg falhou (" + ex.Message.Split('\n')[0] + ") — arquivo salvo sem remux";
                await SaveWithoutFfmpegAsync(plan, videoOut, audioOut, baseName, videoExt).ConfigureAwait(false);
            }
        }
        else
        {
            await SaveWithoutFfmpegAsync(plan, videoOut, audioOut, baseName, videoExt).ConfigureAwait(false);
        }

        try { Directory.Delete(_partsDir, recursive: true); } catch { }
    }

    private Task SaveWithoutFfmpegAsync(StreamPlan plan, string videoOut, string? audioOut, string baseName, string videoExt)
    {
        var videoName = FileNameHelper.MakeUnique(_item.Directory, baseName + (audioOut != null ? ".video" : "") + videoExt);
        File.Move(videoOut, Path.Combine(_item.Directory, videoName), overwrite: false);
        _item.FileName = videoName;

        if (audioOut != null)
        {
            var audioName = FileNameHelper.MakeUnique(_item.Directory, baseName + ".audio" + Path.GetExtension(audioOut));
            File.Move(audioOut, Path.Combine(_item.Directory, audioName), overwrite: false);
            _item.Note = $"Sem ffmpeg: vídeo e áudio salvos separados ({audioName}). Instale o ffmpeg em Configurações para gerar um MP4 único.";
        }
        else if (videoExt == ".ts")
        {
            _item.Note = "Sem ffmpeg: salvo como .ts (reproduz no VLC/MPC). Instale o ffmpeg em Configurações para converter em MP4.";
        }
        return Task.CompletedTask;
    }

    private async Task ConcatAsync(List<MediaSegment> segments, string prefix, string output, CancellationToken ct)
    {
        await using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var seg in segments.OrderBy(s => s.IsInit ? -1 : s.Index))
        {
            ct.ThrowIfCancellationRequested();
            var path = PartPath(seg, prefix);
            if (!File.Exists(path)) throw new DownloadException($"Segmento {seg.Index + 1} ausente ao juntar o arquivo.", true);
            await using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
    }
}
