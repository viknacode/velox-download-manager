using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Velox.Core.Models;
using Velox.Core.Services;

namespace Velox.Core.Engine;

/// <summary>
/// Executa um único download: sonda o servidor, divide o arquivo em segmentos,
/// abre N conexões paralelas, redistribui trabalho dinamicamente (quando uma conexão
/// termina ela "rouba" metade do maior segmento restante), tenta novamente em falhas
/// com backoff exponencial e persiste o progresso para retomada.
/// </summary>
internal sealed class DownloadTask : IDownloadTask
{
    private const int BufferSize = 128 * 1024;

    private readonly DownloadItem _item;
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly BandwidthLimiter _limiter;
    private readonly CancellationTokenSource _cts = new();

    private SafeFileHandle? _file;
    private int _activeConnections;
    private int _retryingConnections;
    private long _lastBytes;
    private DownloadException? _fatal;
    private volatile bool _pauseRequested;
    private volatile bool _moved;
    private int _nextSegmentIndex;

    public DownloadItem Item => _item;
    public Task Completion { get; }

    public DownloadTask(DownloadItem item, HttpClient http, AppSettings settings, BandwidthLimiter limiter)
    {
        _item = item;
        _http = http;
        _settings = settings;
        _limiter = limiter;
        Completion = Task.Run(RunAsync);
    }

    public void Pause()
    {
        _pauseRequested = true;
        _cts.Cancel();
    }

    /// <summary>Chamado pelo gerenciador a cada ~500 ms para atualizar velocidade/estatísticas.</summary>
    public void Tick(double dtSeconds)
    {
        long now = _item.GetDownloadedBytes();
        double instant = dtSeconds > 0 ? (now - _lastBytes) / dtSeconds : 0;
        _lastBytes = now;

        _item.Speed = _item.Speed <= 0 ? instant : _item.Speed * 0.55 + instant * 0.45;
        if (_item.Speed < 1) _item.Speed = 0;
        _item.DownloadedBytes = now;
        _item.ActiveConnections = Volatile.Read(ref _activeConnections);
        _item.RetryingConnections = Volatile.Read(ref _retryingConnections);
        if (_item.Status == DownloadStatus.Downloading) _item.ElapsedSeconds += dtSeconds;
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
            _lastBytes = _item.GetDownloadedBytes();

            var probe = await UrlProber.ProbeAsync(_http, _item.Url, _item.Headers, _item.Referer, ct)
                .ConfigureAwait(false);

            PrepareSegments(probe);
            OpenFile();

            _item.Status = DownloadStatus.Downloading;

            int workers = 1;
            if (_item.SupportsResume && _item.TotalSize > 0)
            {
                long minSplit = Math.Max(64 * 1024, _settings.MinSplitSizeKB * 1024L);
                workers = (int)Math.Clamp(_item.TotalSize / minSplit, 1, Math.Clamp(_item.MaxConnections, 1, 64));
            }

            var tasks = new Task[workers];
            for (int i = 0; i < workers; i++)
                tasks[i] = Task.Run(() => WorkerLoopAsync(ct), CancellationToken.None);

            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (_fatal != null) throw _fatal;
            ct.ThrowIfCancellationRequested();

            if (!AllSegmentsCompleted())
                throw new DownloadException("O download terminou incompleto. Tente retomar.", retryable: true);

            await FinalizeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_moved)
        {
            MarkCompleted();
        }
        catch (OperationCanceledException) when (_pauseRequested && _fatal == null)
        {
            _item.Status = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            var err = _fatal ?? ex;
            _item.Status = DownloadStatus.Failed;
            _item.ErrorMessage = err is DownloadException de ? de.Message : err.Message;
            _item.LastErrorRetryable = err is not DownloadException de2 || de2.Retryable;
        }
        finally
        {
            _file?.Dispose();
            _file = null;
            _item.Speed = 0;
            _item.ActiveConnections = 0;
            _item.RetryingConnections = 0;
            _item.DownloadedBytes = _item.GetDownloadedBytes();
            lock (_item.SegmentsLock)
            {
                foreach (var s in _item.Segments)
                {
                    s.IsActive = false;
                    s.Pending = 0;
                }
            }
        }
    }

    private void PrepareSegments(ProbeResult probe)
    {
        lock (_item.SegmentsLock)
        {
            bool reset = _item.Segments.Count == 0
                         || _item.TotalSize != probe.Size
                         || !_item.SupportsResume
                         || !probe.SupportsResume
                         || !File.Exists(_item.TempPath)
                         || (!string.IsNullOrEmpty(_item.ETag) && !string.IsNullOrEmpty(probe.ETag) && _item.ETag != probe.ETag);

            if (!reset)
            {
                foreach (var s in _item.Segments)
                {
                    if (s.Downloaded < 0 || s.Start < 0 || (s.End >= 0 && s.Position > s.End + 1))
                    {
                        reset = true;
                        break;
                    }
                }
            }

            _item.TotalSize = probe.Size;
            _item.SupportsResume = probe.SupportsResume;
            _item.ETag = probe.ETag;
            _item.ContentType ??= probe.ContentType;
            if (string.IsNullOrWhiteSpace(_item.FileName)) _item.FileName = probe.FileName;

            if (reset)
            {
                _item.Segments.Clear();
                _item.ElapsedSeconds = 0;
                _nextSegmentIndex = 0;

                if (probe.SupportsResume && probe.Size > 0)
                {
                    long minSplit = Math.Max(64 * 1024, _settings.MinSplitSizeKB * 1024L);
                    int n = (int)Math.Clamp(probe.Size / minSplit, 1, Math.Clamp(_item.MaxConnections, 1, 64));
                    long chunk = probe.Size / n;
                    for (int i = 0; i < n; i++)
                    {
                        long start = i * chunk;
                        long end = i == n - 1 ? probe.Size - 1 : start + chunk - 1;
                        _item.Segments.Add(new Segment { Index = _nextSegmentIndex++, Start = start, End = end });
                    }
                }
                else
                {
                    _item.Segments.Add(new Segment
                    {
                        Index = _nextSegmentIndex++,
                        Start = 0,
                        End = probe.Size > 0 ? probe.Size - 1 : -1
                    });
                }

                try { if (File.Exists(_item.TempPath)) File.Delete(_item.TempPath); } catch { }
            }
            else
            {
                _nextSegmentIndex = _item.Segments.Max(s => s.Index) + 1;
                foreach (var s in _item.Segments)
                {
                    s.IsActive = false;
                    s.Pending = 0;
                }
            }

            _lastBytes = _item.Segments.Sum(s => s.Downloaded);
            _item.DownloadedBytes = _lastBytes;
        }
    }

    private void OpenFile()
    {
        Directory.CreateDirectory(_item.Directory);

        if (!File.Exists(_item.TempPath))
        {
            using var fs = new FileStream(_item.TempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            NativeFile.TryMarkSparse(fs.SafeFileHandle);
            if (_item.TotalSize > 0) fs.SetLength(_item.TotalSize);
        }

        _file = File.OpenHandle(_item.TempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            FileOptions.Asynchronous);
    }

    private bool AllSegmentsCompleted()
    {
        lock (_item.SegmentsLock)
        {
            return _item.Segments.Count > 0 && _item.Segments.All(s => s.IsCompleted);
        }
    }

    // ------------------------------------------------------------------ workers

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var seg = AcquireSegment();
                if (seg == null) return;

                try
                {
                    await DownloadSegmentAsync(seg, ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_item.SegmentsLock)
                    {
                        seg.IsActive = false;
                        seg.Pending = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // pausa ou falha fatal em outra conexão
        }
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
    }

    /// <summary>
    /// Pega um segmento livre; se não houver, divide o maior segmento ativo ao meio
    /// (a partir da posição atual) para manter todas as conexões ocupadas até o fim.
    /// </summary>
    private Segment? AcquireSegment()
    {
        lock (_item.SegmentsLock)
        {
            var free = _item.Segments.FirstOrDefault(s => !s.IsActive && !s.IsCompleted);
            if (free != null)
            {
                free.IsActive = true;
                return free;
            }

            if (!_item.SupportsResume || _item.TotalSize <= 0) return null;

            long minSplit = Math.Max(64 * 1024, _settings.MinSplitSizeKB * 1024L);
            var largest = _item.Segments
                .Where(s => s.IsActive && !s.IsCompleted)
                .OrderByDescending(s => s.Remaining)
                .FirstOrDefault();

            if (largest == null) return null;

            long effectivePos = largest.Position + largest.Pending;
            long remaining = largest.End - effectivePos + 1;
            if (remaining < minSplit * 2) return null;

            long newStart = effectivePos + remaining / 2;
            var created = new Segment
            {
                Index = _nextSegmentIndex++,
                Start = newStart,
                End = largest.End,
                IsActive = true
            };
            largest.End = newStart - 1;
            _item.Segments.Add(created);
            return created;
        }
    }

    private async Task DownloadSegmentAsync(Segment seg, CancellationToken ct)
    {
        int attempt = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (seg.IsCompleted) return;

                try
                {
                    await DownloadSegmentOnceAsync(seg, buffer, ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException
                                               or TimeoutException or OperationCanceledException)
                {
                    attempt++;
                    if (attempt > _settings.MaxRetriesPerSegment)
                        throw new DownloadException(
                            $"Conexão falhou após {attempt - 1} tentativas: {UrlProber.Describe(ex)}", true, ex);

                    _item.RetryCount++;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5))));

                    Interlocked.Increment(ref _retryingConnections);
                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _retryingConnections);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DownloadSegmentOnceAsync(Segment seg, byte[] buffer, CancellationToken ct)
    {
        long pos = seg.Position;
        bool ranged = _item.SupportsResume || pos > 0;

        using var req = RequestBuilder.Build(_item.Url, _item.Headers, _item.Referer);
        if (ranged)
            req.Headers.Range = new RangeHeaderValue(pos, seg.End >= 0 ? seg.End : null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.ReadTimeoutSeconds, 5, 600));

        timeoutCts.CancelAfter(timeout);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Tempo limite ao conectar.");
        }

        using (resp)
        {
            var status = (int)resp.StatusCode;

            if (ranged)
            {
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    // servidor ignorou o Range
                    bool single;
                    lock (_item.SegmentsLock) single = _item.Segments.Count == 1;
                    if (pos != 0 || !single)
                        throw new DownloadException(
                            "O servidor deixou de aceitar retomada por intervalos. Reinicie o download.", false);
                    _item.SupportsResume = false;
                }
                else if (resp.StatusCode != HttpStatusCode.PartialContent)
                {
                    ThrowForStatus(status, resp.ReasonPhrase);
                }
                else
                {
                    var cr = resp.Content.Headers.ContentRange;
                    if (cr?.From is long from && from != pos)
                        throw new IOException($"Servidor devolveu intervalo incorreto (esperado {pos}, recebido {from}).");
                }
            }
            else if (!resp.IsSuccessStatusCode)
            {
                ThrowForStatus(status, resp.ReasonPhrase);
            }

            timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            Interlocked.Increment(ref _activeConnections);
            try
            {
                while (true)
                {
                    int toRead = buffer.Length;
                    if (seg.End >= 0)
                    {
                        long allowed = seg.End - seg.Position + 1;
                        if (allowed <= 0) return;
                        toRead = (int)Math.Min(toRead, allowed);
                    }

                    timeoutCts.CancelAfter(timeout);
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(0, toRead), timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("Tempo limite de leitura excedido (conexão parada).");
                    }
                    timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);

                    if (read <= 0)
                    {
                        if (seg.End >= 0)
                        {
                            if (!seg.IsCompleted)
                                throw new IOException("A conexão foi encerrada antes do fim do segmento.");
                            return;
                        }

                        // tamanho desconhecido: chegou ao fim
                        lock (_item.SegmentsLock)
                        {
                            seg.End = seg.Position - 1;
                            _item.TotalSize = seg.Position;
                        }
                        return;
                    }

                    await _limiter.ThrottleAsync(read, ct).ConfigureAwait(false);

                    int toWrite;
                    lock (_item.SegmentsLock)
                    {
                        toWrite = seg.End >= 0 ? (int)Math.Min(read, seg.End - seg.Position + 1) : read;
                        seg.Pending = Math.Max(0, toWrite);
                    }

                    if (toWrite > 0)
                    {
                        await RandomAccess.WriteAsync(_file!, buffer.AsMemory(0, toWrite), seg.Position, ct)
                            .ConfigureAwait(false);

                        lock (_item.SegmentsLock)
                        {
                            seg.Downloaded += toWrite;
                            seg.Pending = 0;
                        }
                    }

                    if (seg.IsCompleted) return;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnections);
            }
        }
    }

    private static void ThrowForStatus(int status, string? reason)
    {
        var msg = $"O servidor respondeu {status} ({reason}).";
        if (status >= 500 || status == 408 || status == 429)
            throw new IOException(msg);
        if (status == 403 || status == 401)
            throw new DownloadException(msg + " O link pode ter expirado ou exige autenticação.", false);
        if (status == 404 || status == 410)
            throw new DownloadException(msg + " O arquivo não está mais disponível.", false);
        throw new DownloadException(msg, false);
    }

    // ------------------------------------------------------------------ finalização

    private async Task FinalizeAsync(CancellationToken ct)
    {
        _file?.Dispose();
        _file = null;

        if (_item.TotalSize < 0)
        {
            try { _item.TotalSize = new FileInfo(_item.TempPath).Length; } catch { }
        }

        var final = _item.FullPath;
        if (File.Exists(final))
        {
            _item.FileName = FileNameHelper.MakeUnique(_item.Directory, _item.FileName);
            final = _item.FullPath;
        }

        File.Move(_item.TempPath, final);
        _moved = true;

        var expected = _item.ExpectedHash?.Trim();
        if (_settings.VerifyHashOnComplete || !string.IsNullOrWhiteSpace(expected))
        {
            _item.Status = DownloadStatus.Verifying;
            var hash = await ComputeHashAsync(final, expected, ct).ConfigureAwait(false);
            _item.ComputedHash = hash;

            if (!string.IsNullOrWhiteSpace(expected) &&
                !string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new DownloadException(
                    $"Verificação de integridade falhou. Esperado {expected[..Math.Min(12, expected.Length)]}…, obtido {hash[..12]}…",
                    false);
            }
        }

        MarkCompleted();
    }

    private void MarkCompleted()
    {
        _item.Status = DownloadStatus.Completed;
        _item.CompletedAt = DateTime.Now;
        _item.ErrorMessage = null;
        _item.DownloadedBytes = _item.TotalSize > 0 ? _item.TotalSize : _item.GetDownloadedBytes();
    }

    private static async Task<string> ComputeHashAsync(string path, string? expected, CancellationToken ct)
    {
        using HashAlgorithm algo = (expected?.Length ?? 64) switch
        {
            32 => MD5.Create(),
            40 => SHA1.Create(),
            _ => SHA256.Create()
        };

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await algo.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
