using System.Globalization;
using System.Text.RegularExpressions;

namespace Velox.Core.Streams;

/// <summary>Parser de playlists HLS (RFC 8216): master (variantes) e mídia (segmentos).</summary>
public static class HlsParser
{
    public static bool LooksLikePlaylist(string text) => text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);
    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public sealed class MasterVariant
    {
        public required Uri Url { get; init; }
        public long Bandwidth { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string? Codecs { get; init; }
        public double FrameRate { get; init; }
        public string? AudioGroup { get; init; }
    }

    public sealed class MediaRendition
    {
        public required string GroupId { get; init; }
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public Uri? Url { get; init; }
        public bool Default { get; init; }
        public string? Language { get; init; }
    }

    public sealed class MediaPlaylist
    {
        public List<MediaSegment> Segments { get; } = new();
        public MediaSegment? Init { get; set; }
        public bool IsLive { get; set; }
        public double Duration => Segments.Sum(s => s.Duration);
        public bool IsFmp4 => Init != null;
        public bool IsEncrypted { get; set; }
        public string? UnsupportedKeyMethod { get; set; }
    }

    // ------------------------------------------------------------------ master

    public static (List<MasterVariant> Variants, List<MediaRendition> Renditions) ParseMaster(string text, Uri baseUrl)
    {
        var variants = new List<MasterVariant>();
        var renditions = new List<MediaRendition>();
        var lines = SplitLines(text);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                var attrs = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
                // a URI é a próxima linha não vazia que não seja tag
                string? uri = null;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (string.IsNullOrWhiteSpace(lines[j])) continue;
                    if (lines[j].StartsWith('#')) continue;
                    uri = lines[j];
                    i = j;
                    break;
                }
                if (uri == null) continue;

                int w = 0, h = 0;
                if (attrs.TryGetValue("RESOLUTION", out var res))
                {
                    var m = Regex.Match(res, @"(\d+)x(\d+)");
                    if (m.Success) { w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value); }
                }
                variants.Add(new MasterVariant
                {
                    Url = new Uri(baseUrl, uri),
                    Bandwidth = attrs.TryGetValue("AVERAGE-BANDWIDTH", out var ab) && long.TryParse(ab, out var abv) ? abv
                              : attrs.TryGetValue("BANDWIDTH", out var b) && long.TryParse(b, out var bv) ? bv : 0,
                    Width = w,
                    Height = h,
                    Codecs = attrs.GetValueOrDefault("CODECS"),
                    FrameRate = attrs.TryGetValue("FRAME-RATE", out var fr) && double.TryParse(fr, NumberStyles.Float, CultureInfo.InvariantCulture, out var frv) ? frv : 0,
                    AudioGroup = attrs.GetValueOrDefault("AUDIO")
                });
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var attrs = ParseAttributes(line["#EXT-X-MEDIA:".Length..]);
                renditions.Add(new MediaRendition
                {
                    GroupId = attrs.GetValueOrDefault("GROUP-ID") ?? "",
                    Name = attrs.GetValueOrDefault("NAME") ?? "",
                    Type = attrs.GetValueOrDefault("TYPE") ?? "",
                    Url = attrs.TryGetValue("URI", out var u) && !string.IsNullOrWhiteSpace(u) ? new Uri(baseUrl, u) : null,
                    Default = string.Equals(attrs.GetValueOrDefault("DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase),
                    Language = attrs.GetValueOrDefault("LANGUAGE")
                });
            }
        }

        return (variants, renditions);
    }

    // ------------------------------------------------------------------ mídia

    public static MediaPlaylist ParseMedia(string text, Uri baseUrl)
    {
        var pl = new MediaPlaylist { IsLive = !text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal) };
        var lines = SplitLines(text);

        long mediaSequence = 0;
        var seqMatch = Regex.Match(text, @"#EXT-X-MEDIA-SEQUENCE:(\d+)");
        if (seqMatch.Success) mediaSequence = long.Parse(seqMatch.Groups[1].Value);

        SegmentKey? currentKey = null;
        double nextDuration = 0;
        long? nextRangeLen = null, nextRangeStart = null;
        long lastRangeEnd = 0;
        int index = 0;
        long seq = mediaSequence;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var a = ParseAttributes(line["#EXT-X-KEY:".Length..]);
                var method = a.GetValueOrDefault("METHOD") ?? "NONE";
                if (method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    currentKey = null;
                }
                else if (method.Equals("AES-128", StringComparison.OrdinalIgnoreCase) && a.TryGetValue("URI", out var keyUri))
                {
                    byte[]? iv = null;
                    if (a.TryGetValue("IV", out var ivHex)) iv = ParseHex(ivHex);
                    currentKey = new SegmentKey { Url = new Uri(baseUrl, keyUri), Iv = iv, Method = "AES-128" };
                    pl.IsEncrypted = true;
                }
                else
                {
                    pl.IsEncrypted = true;
                    pl.UnsupportedKeyMethod = method; // SAMPLE-AES / DRM
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var a = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                if (a.TryGetValue("URI", out var mapUri))
                {
                    long? rs = null, rl = null;
                    if (a.TryGetValue("BYTERANGE", out var br)) (rl, rs) = ParseByteRange(br, 0);
                    pl.Init = new MediaSegment { Index = -1, Url = new Uri(baseUrl, mapUri), RangeStart = rs, RangeLength = rl, IsInit = true, Key = null };
                }
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var d = line["#EXTINF:".Length..].Split(',')[0];
                double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out nextDuration);
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                (nextRangeLen, nextRangeStart) = ParseByteRange(line["#EXT-X-BYTERANGE:".Length..], lastRangeEnd);
                continue;
            }

            if (line.StartsWith('#')) continue;

            // URI de segmento
            var key = currentKey == null ? null : new SegmentKey
            {
                Url = currentKey.Url,
                Method = currentKey.Method,
                Iv = currentKey.Iv ?? SequenceIv(seq)
            };

            pl.Segments.Add(new MediaSegment
            {
                Index = index++,
                Url = new Uri(baseUrl, line),
                Duration = nextDuration,
                RangeStart = nextRangeStart,
                RangeLength = nextRangeLen,
                Key = key
            });

            if (nextRangeLen != null) lastRangeEnd = (nextRangeStart ?? 0) + nextRangeLen.Value;
            nextDuration = 0;
            nextRangeLen = nextRangeStart = null;
            seq++;
        }

        return pl;
    }

    // ------------------------------------------------------------------ util

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToArray();

    /// <summary>Atributos no formato KEY=VALUE,KEY="quoted, value".</summary>
    public static Dictionary<string, string> ParseAttributes(string s)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(s, @"([A-Z0-9\-]+)=(""(?<q>[^""]*)""|(?<v>[^,]*))"))
        {
            var key = m.Groups[1].Value;
            var val = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["v"].Value;
            dict[key] = val.Trim();
        }
        return dict;
    }

    private static (long? Length, long? Start) ParseByteRange(string s, long defaultStart)
    {
        var parts = s.Split('@');
        if (!long.TryParse(parts[0].Trim(), out var len)) return (null, null);
        long start = parts.Length > 1 && long.TryParse(parts[1].Trim(), out var st) ? st : defaultStart;
        return (len, start);
    }

    private static byte[] ParseHex(string hex)
    {
        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length % 2 == 1) hex = "0" + hex;
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length == 16) return bytes;
        var iv = new byte[16];
        Array.Copy(bytes, 0, iv, Math.Max(0, 16 - bytes.Length), Math.Min(16, bytes.Length));
        return iv;
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (int i = 0; i < 8; i++) iv[15 - i] = (byte)((sequence >> (8 * i)) & 0xFF);
        return iv;
    }
}
