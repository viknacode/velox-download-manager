namespace Velox.Core.Models;

public sealed class DownloadRequest
{
    public required string Url { get; init; }
    public string? FileName { get; init; }
    public string? Directory { get; init; }
    public int? MaxConnections { get; init; }
    public string? Referer { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public string? ExpectedHash { get; init; }
    public string? Category { get; init; }
    public bool StartImmediately { get; init; } = true;
    public ProbeResult? Probe { get; init; }
    /// <summary>Stream HLS/DASH: variante escolhida (null = melhor qualidade).</summary>
    public StreamKind Kind { get; init; } = StreamKind.File;
    public string? VariantId { get; init; }
    public string? VariantLabel { get; init; }
}

public sealed class ProbeResult
{
    public string FinalUrl { get; set; } = "";
    public string Host { get; set; } = "";
    public long Size { get; set; } = -1;
    public bool SupportsResume { get; set; }
    public string FileName { get; set; } = "download";
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
}
