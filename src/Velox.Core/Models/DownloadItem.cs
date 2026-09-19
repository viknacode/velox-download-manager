using System.Text.Json.Serialization;

namespace Velox.Core.Models;

public sealed class DownloadItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Directory { get; set; } = "";
    public long TotalSize { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public bool SupportsResume { get; set; }
    public List<Segment> Segments { get; set; } = new();
    public int MaxConnections { get; set; } = 8;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? ErrorMessage { get; set; }
    public bool LastErrorRetryable { get; set; } = true;
    public int AutoRetryCount { get; set; }
    public int RetryCount { get; set; }
    public string Category { get; set; } = "Outros";
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public string? Referer { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? ExpectedHash { get; set; }
    public string? ComputedHash { get; set; }

    // ----- streams (HLS / DASH) -----
    public StreamKind Kind { get; set; } = StreamKind.File;
    /// <summary>Id da variante escolhida (HLS: URL da playlist de mídia; DASH: id da Representation de vídeo).</summary>
    public string? VariantId { get; set; }
    public string? VariantLabel { get; set; }
    public int StreamSegmentsTotal { get; set; }
    public int StreamSegmentsDone { get; set; }
    /// <summary>true quando TotalSize é uma estimativa (streams: média dos segmentos × total).</summary>
    public bool TotalIsEstimate { get; set; }
    /// <summary>Observação sobre o resultado (ex.: salvo como .ts por falta de ffmpeg).</summary>
    public string? Note { get; set; }

    // ----- estado de execução (não persistido) -----

    [JsonIgnore] public object SegmentsLock { get; } = new();
    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] public int ActiveConnections { get; set; }
    [JsonIgnore] public int RetryingConnections { get; set; }
    [JsonIgnore] public string FullPath => Path.Combine(Directory, FileName);
    [JsonIgnore] public string TempPath => FullPath + ".vxpart";

    [JsonIgnore]
    public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Verifying or DownloadStatus.Merging;

    [JsonIgnore] public bool IsStream => Kind != StreamKind.File;

    /// <summary>Estado de cada segmento de mídia (0 pendente, 1 baixando, 2 concluído) — só em memória, para a barra.</summary>
    [JsonIgnore] public byte[]? StreamStates { get; set; }

    [JsonIgnore]
    public double Progress => Status == DownloadStatus.Completed ? 1
        : IsStream && StreamSegmentsTotal > 0 ? Math.Clamp((double)StreamSegmentsDone / StreamSegmentsTotal, 0, 1)
        : TotalSize > 0 ? Math.Clamp((double)DownloadedBytes / TotalSize, 0, 1)
        : 0;

    [JsonIgnore]
    public TimeSpan? Eta => Status == DownloadStatus.Downloading && TotalSize > 0 && Speed > 1
        ? TimeSpan.FromSeconds((TotalSize - DownloadedBytes) / Speed)
        : null;

    [JsonIgnore]
    public double AverageSpeed => ElapsedSeconds > 0.5 ? DownloadedBytes / ElapsedSeconds : 0;

    private readonly double[] _history = new double[120];
    private int _histCount, _histHead;

    public void PushSpeedSample(double value)
    {
        lock (_history)
        {
            _history[_histHead] = value;
            _histHead = (_histHead + 1) % _history.Length;
            if (_histCount < _history.Length) _histCount++;
        }
    }

    public double[] GetSpeedHistory()
    {
        lock (_history)
        {
            var result = new double[_histCount];
            int start = (_histHead - _histCount + _history.Length) % _history.Length;
            for (int i = 0; i < _histCount; i++)
                result[i] = _history[(start + i) % _history.Length];
            return result;
        }
    }

    public long GetDownloadedBytes()
    {
        lock (SegmentsLock)
        {
            long total = 0;
            foreach (var s in Segments) total += s.Downloaded;
            return total;
        }
    }

    public SegmentSnapshot[] SnapshotSegments()
    {
        if (IsStream) return SnapshotStreamSegments();
        lock (SegmentsLock)
        {
            return Segments
                .OrderBy(s => s.Start)
                .Select(s => new SegmentSnapshot(s.Start, s.End, s.Downloaded, s.IsActive))
                .ToArray();
        }
    }

    /// <summary>Converte o mapa de segmentos de mídia em faixas proporcionais (runs de mesmo estado) sobre o tamanho estimado.</summary>
    private SegmentSnapshot[] SnapshotStreamSegments()
    {
        var states = StreamStates;
        long total = TotalSize;
        if (states == null || states.Length == 0 || total <= 0) return Array.Empty<SegmentSnapshot>();

        var list = new List<SegmentSnapshot>();
        int n = states.Length;
        int runStart = 0;
        for (int i = 1; i <= n; i++)
        {
            if (i < n && states[i] == states[runStart]) continue;
            long start = (long)((double)total * runStart / n);
            long end = (i == n ? total : (long)((double)total * i / n)) - 1;
            byte st = states[runStart];
            list.Add(new SegmentSnapshot(start, end, st == 2 ? end - start + 1 : 0, st == 1));
            runStart = i;
        }
        return list.ToArray();
    }

    public DownloadItem CloneForPersistence()
    {
        lock (SegmentsLock)
        {
            return new DownloadItem
            {
                Id = Id,
                Url = Url,
                FileName = FileName,
                Directory = Directory,
                TotalSize = TotalSize,
                DownloadedBytes = GetDownloadedBytes(),
                Status = Status,
                SupportsResume = SupportsResume,
                Segments = Segments.Select(s => new Segment
                {
                    Index = s.Index, Start = s.Start, End = s.End, Downloaded = s.Downloaded
                }).ToList(),
                MaxConnections = MaxConnections,
                CreatedAt = CreatedAt,
                CompletedAt = CompletedAt,
                ElapsedSeconds = ElapsedSeconds,
                ErrorMessage = ErrorMessage,
                LastErrorRetryable = LastErrorRetryable,
                AutoRetryCount = AutoRetryCount,
                RetryCount = RetryCount,
                Category = Category,
                ContentType = ContentType,
                ETag = ETag,
                Referer = Referer,
                Headers = new Dictionary<string, string>(Headers),
                ExpectedHash = ExpectedHash,
                ComputedHash = ComputedHash,
                Kind = Kind,
                VariantId = VariantId,
                VariantLabel = VariantLabel,
                StreamSegmentsTotal = StreamSegmentsTotal,
                StreamSegmentsDone = StreamSegmentsDone,
                TotalIsEstimate = TotalIsEstimate,
                Note = Note
            };
        }
    }
}
