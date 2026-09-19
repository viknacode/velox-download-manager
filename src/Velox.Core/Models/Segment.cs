using System.Text.Json.Serialization;

namespace Velox.Core.Models;

/// <summary>
/// Um intervalo de bytes do arquivo, baixado por uma conexão independente.
/// </summary>
public sealed class Segment
{
    public int Index { get; set; }

    public long Start { get; set; }

    /// <summary>Byte final (inclusivo). -1 quando o tamanho total é desconhecido.</summary>
    public long End { get; set; } = -1;

    public long Downloaded { get; set; }

    [JsonIgnore] public bool IsActive { get; set; }

    /// <summary>Bytes em processo de gravação neste instante (ainda não contabilizados em Downloaded).</summary>
    [JsonIgnore] public int Pending { get; set; }

    [JsonIgnore] public long Position => Start + Downloaded;

    [JsonIgnore] public long Length => End < 0 ? -1 : End - Start + 1;

    [JsonIgnore] public long Remaining => End < 0 ? long.MaxValue : Math.Max(0, End - Position + 1);

    [JsonIgnore] public bool IsCompleted => End >= 0 && Position > End;
}

public readonly record struct SegmentSnapshot(long Start, long End, long Downloaded, bool IsActive)
{
    public bool IsCompleted => End >= 0 && Start + Downloaded > End;
}
