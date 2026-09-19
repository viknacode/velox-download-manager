namespace Velox.Core.Engine;

/// <summary>
/// Limitador global de banda (token bucket simples com janelas de 250 ms).
/// Compartilhado por todas as conexões de todos os downloads.
/// </summary>
public sealed class BandwidthLimiter
{
    private const int WindowMs = 250;

    private readonly object _lock = new();
    private long _limitPerSecond;
    private long _windowStart;
    private long _consumed;

    /// <summary>Bytes por segundo. 0 ou negativo = ilimitado.</summary>
    public long LimitBytesPerSecond
    {
        get => Volatile.Read(ref _limitPerSecond);
        set => Volatile.Write(ref _limitPerSecond, value);
    }

    public bool IsEnabled => LimitBytesPerSecond > 0;

    public async ValueTask ThrottleAsync(int bytes, CancellationToken ct)
    {
        var limit = LimitBytesPerSecond;
        if (limit <= 0 || bytes <= 0) return;

        long perWindow = Math.Max(1, limit * WindowMs / 1000);

        while (true)
        {
            int delay;
            lock (_lock)
            {
                var now = Environment.TickCount64;
                if (now - _windowStart >= WindowMs)
                {
                    _windowStart = now;
                    _consumed = 0;
                }

                if (_consumed < perWindow)
                {
                    // permite pequeno excesso para não fragmentar demais as leituras
                    _consumed += bytes;
                    return;
                }

                delay = (int)Math.Max(1, WindowMs - (now - _windowStart));
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
