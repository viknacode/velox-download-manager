using Velox.Core.Models;

namespace Velox.Core.Streams;

/// <summary>Uma opção de qualidade oferecida pelo manifesto (HLS variant / DASH representation).</summary>
public sealed class StreamVariant
{
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public long Bandwidth { get; init; }
    public string? Codecs { get; init; }
    public double FrameRate { get; init; }
    /// <summary>Faixa de áudio separada (DASH ou HLS com EXT-X-MEDIA). null = áudio embutido.</summary>
    public string? AudioId { get; init; }
    public string? AudioLabel { get; init; }
    public bool HasSeparateAudio => AudioId != null;
    /// <summary>Contêiner do arquivo final ("mp4", "webm", "mkv", "m4a"); null = decidido na montagem.</summary>
    public string? Container { get; init; }
    /// <summary>Tamanho total conhecido (vídeo + áudio) ou -1.</summary>
    public long SizeBytes { get; init; } = -1;

    public long EstimatedBytes(double durationSeconds) => Bandwidth > 0 && durationSeconds > 0 ? (long)(Bandwidth / 8.0 * durationSeconds) : -1;
}

/// <summary>Resultado da sondagem de um manifesto.</summary>
public sealed class StreamInfo
{
    public required StreamKind Kind { get; init; }
    public required string ManifestUrl { get; init; }
    public double DurationSeconds { get; set; }
    public bool IsLive { get; set; }
    public List<StreamVariant> Variants { get; init; } = new();
    public StreamVariant? Best => Variants.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).FirstOrDefault();
    public bool IsEncrypted { get; set; }
    public string? DrmSystem { get; set; }
    /// <summary>Título/canal/miniatura quando a fonte os informa (YouTube).</summary>
    public string? Title { get; init; }
    public string? Uploader { get; init; }
    public string? Thumbnail { get; init; }
}

/// <summary>Um segmento a baixar (já com URL absoluta).</summary>
public sealed class MediaSegment
{
    public int Index { get; init; }
    public required Uri Url { get; init; }
    public long? RangeStart { get; init; }
    public long? RangeLength { get; init; }
    public double Duration { get; init; }
    public SegmentKey? Key { get; init; }
    public bool IsInit { get; init; }
}

public sealed class SegmentKey
{
    public required Uri Url { get; init; }
    public byte[]? Iv { get; init; }
    public string Method { get; init; } = "AES-128";
}

/// <summary>Plano de download resolvido: listas de segmentos por faixa.</summary>
public sealed class StreamPlan
{
    public required StreamKind Kind { get; init; }
    public required StreamVariant Variant { get; init; }
    public List<MediaSegment> Video { get; init; } = new();
    public List<MediaSegment> Audio { get; init; } = new();
    /// <summary>"ts" (MPEG-TS) ou "mp4" (fMP4/CMAF).</summary>
    public string VideoContainer { get; init; } = "ts";
    public double DurationSeconds { get; init; }
    public bool NeedsMux => Audio.Count > 0;
    /// <summary>As faixas já são arquivos completos (MP4/WebM do YouTube): sem áudio separado não precisa de remux.</summary>
    public bool IsCompleteFile { get; init; }
    /// <summary>Título real do vídeo, para batizar o arquivo quando o nome atual é um provisório.</summary>
    public string? Title { get; init; }
}
