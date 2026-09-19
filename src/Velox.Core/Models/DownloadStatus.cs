namespace Velox.Core.Models;

public enum DownloadStatus
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Completed,
    Failed,
    Verifying,
    /// <summary>Juntando segmentos / remuxando (streams HLS e DASH).</summary>
    Merging
}

public enum StreamKind
{
    File,
    Hls,
    Dash
}
