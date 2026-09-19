using Velox.Core.Models;

namespace Velox.Core.Engine;

/// <summary>Contrato comum entre o download de arquivo (segmentado por bytes) e o de stream (por segmentos de mídia).</summary>
internal interface IDownloadTask
{
    DownloadItem Item { get; }
    Task Completion { get; }
    void Pause();
    void Tick(double dtSeconds);
}
