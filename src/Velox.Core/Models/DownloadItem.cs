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

    // ----- estado de execução (não persistido) -----

    [JsonIgnore] public object SegmentsLock { get; } = new();
    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] public int ActiveConnections { get; set; }
    [JsonIgnore] public int RetryingConnections { get; set; }
    [JsonIgnore] public string FullPath => Path.Combine(Directory, FileName);
    [JsonIgnore] public string TempPath => FullPath + ".vxpart";

    [JsonIgnore]
    public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Verifying;

    [JsonIgnore]
    public double Progress => TotalSize > 0
        ? Math.Clamp((double)DownloadedBytes / TotalSize, 0, 1)
        : (Status == DownloadStatus.Completed ? 1 : 0);

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
        lock (SegmentsLock)
        {
            return Segments
                .OrderBy(s => s.Start)
                .Select(s => new SegmentSnapshot(s.Start, s.End, s.Downloaded, s.IsActive))
                .ToArray();
        }
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
                ComputedHash = ComputedHash
            };
        }
    }
}
