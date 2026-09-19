using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Velox.Core.Streams;

/// <summary>
/// Parser de manifestos MPEG-DASH (MPD) — subconjunto VOD: SegmentTemplate ($Number$/$Time$ com
/// SegmentTimeline ou duration), SegmentList e SegmentBase (arquivo único). BaseURL em qualquer nível.
/// </summary>
public static class DashParser
{
    public static bool LooksLikeMpd(string text)
    {
        var head = text.Length > 600 ? text[..600] : text;
        return head.Contains("<MPD", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class Representation
    {
        public required string Id { get; init; }
        public string ContentType { get; init; } = "";   // video | audio
        public string MimeType { get; init; } = "";
        public string? Codecs { get; init; }
        public long Bandwidth { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double FrameRate { get; init; }
        public string? Language { get; init; }
        public List<MediaSegment> Segments { get; init; } = new();
        public MediaSegment? Init { get; init; }
        public bool IsProtected { get; init; }
        public string Container => MimeType.Contains("webm", StringComparison.OrdinalIgnoreCase) ? "webm" : "mp4";
    }

    public sealed class Mpd
    {
        public bool IsDynamic { get; init; }
        public double DurationSeconds { get; init; }
        public List<Representation> Representations { get; init; } = new();
        public IEnumerable<Representation> Video => Representations.Where(r => r.ContentType == "video");
        public IEnumerable<Representation> Audio => Representations.Where(r => r.ContentType == "audio");
    }

    public static Mpd Parse(string xml, Uri manifestUrl)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidOperationException("MPD vazio.");
        XNamespace ns = root.Name.Namespace;

        bool dynamic = string.Equals((string?)root.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase);
        double mpdDuration = ParseDuration((string?)root.Attribute("mediaPresentationDuration"));

        var mpdBase = ResolveBase(manifestUrl, root, ns);
        var reps = new List<Representation>();

        foreach (var period in root.Elements(ns + "Period"))
        {
            var periodBase = ResolveBase(mpdBase, period, ns);
            double periodDuration = ParseDuration((string?)period.Attribute("duration"));
            if (periodDuration <= 0) periodDuration = mpdDuration;

            foreach (var set in period.Elements(ns + "AdaptationSet"))
            {
                var setBase = ResolveBase(periodBase, set, ns);
                var setMime = (string?)set.Attribute("mimeType") ?? "";
                var setType = (string?)set.Attribute("contentType") ?? "";
                var setCodecs = (string?)set.Attribute("codecs");
                var setLang = (string?)set.Attribute("lang");
                var setTemplate = set.Element(ns + "SegmentTemplate");
                bool setProtected = set.Elements(ns + "ContentProtection").Any();
                double setFps = ParseFrameRate((string?)set.Attribute("frameRate"));

                foreach (var rep in set.Elements(ns + "Representation"))
                {
                    var repBase = ResolveBase(setBase, rep, ns);
                    var id = (string?)rep.Attribute("id") ?? Guid.NewGuid().ToString("N");
                    var mime = (string?)rep.Attribute("mimeType") ?? setMime;
                    var type = setType;
                    if (string.IsNullOrEmpty(type))
                        type = mime.StartsWith("video", StringComparison.OrdinalIgnoreCase) ? "video"
                             : mime.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ? "audio"
                             : (rep.Attribute("width") != null ? "video" : "audio");
                    long bandwidth = long.TryParse((string?)rep.Attribute("bandwidth"), out var bw) ? bw : 0;
                    int width = int.TryParse((string?)rep.Attribute("width"), out var w) ? w : 0;
                    int height = int.TryParse((string?)rep.Attribute("height"), out var h) ? h : 0;
                    double fps = ParseFrameRate((string?)rep.Attribute("frameRate"));
                    if (fps <= 0) fps = setFps;

                    var template = rep.Element(ns + "SegmentTemplate") ?? setTemplate;
                    var segList = rep.Element(ns + "SegmentList") ?? set.Element(ns + "SegmentList");
                    var segBase = rep.Element(ns + "SegmentBase") ?? set.Element(ns + "SegmentBase");

                    var segments = new List<MediaSegment>();
                    MediaSegment? init = null;

                    if (template != null)
                    {
                        (init, segments) = FromTemplate(template, ns, repBase, id, bandwidth, periodDuration);
                    }
                    else if (segList != null)
                    {
                        var initEl = segList.Element(ns + "Initialization");
                        if (initEl != null && (string?)initEl.Attribute("sourceURL") is string src)
                            init = new MediaSegment { Index = -1, Url = new Uri(repBase, src), IsInit = true };
                        int i = 0;
                        double segDur = ParseTimescaled(segList, "duration", "timescale");
                        foreach (var su in segList.Elements(ns + "SegmentURL"))
                        {
                            var media = (string?)su.Attribute("media");
                            if (media == null) continue;
                            var (rs, rl) = ParseRange((string?)su.Attribute("mediaRange"));
                            segments.Add(new MediaSegment { Index = i++, Url = new Uri(repBase, media), RangeStart = rs, RangeLength = rl, Duration = segDur });
                        }
                    }
                    else
                    {
                        // SegmentBase / BaseURL: arquivo único progressivo (init + índice + dados no mesmo arquivo)
                        segments.Add(new MediaSegment { Index = 0, Url = repBase, Duration = periodDuration });
                    }

                    if (segments.Count == 0) continue;

                    reps.Add(new Representation
                    {
                        Id = id,
                        ContentType = type,
                        MimeType = mime,
                        Codecs = (string?)rep.Attribute("codecs") ?? setCodecs,
                        Bandwidth = bandwidth,
                        Width = width,
                        Height = height,
                        FrameRate = fps,
                        Language = setLang,
                        Segments = segments,
                        Init = init,
                        IsProtected = setProtected || rep.Elements(ns + "ContentProtection").Any()
                    });
                }
            }
        }

        double duration = mpdDuration > 0 ? mpdDuration : reps.Select(r => r.Segments.Sum(s => s.Duration)).DefaultIfEmpty(0).Max();
        return new Mpd { IsDynamic = dynamic, DurationSeconds = duration, Representations = reps };
    }

    // ------------------------------------------------------------------ SegmentTemplate

    private static (MediaSegment? Init, List<MediaSegment> Segments) FromTemplate(XElement template, XNamespace ns, Uri baseUrl,
        string repId, long bandwidth, double periodDuration)
    {
        var segments = new List<MediaSegment>();
        MediaSegment? init = null;

        long timescale = long.TryParse((string?)template.Attribute("timescale"), out var ts) && ts > 0 ? ts : 1;
        long startNumber = long.TryParse((string?)template.Attribute("startNumber"), out var sn) ? sn : 1;
        var media = (string?)template.Attribute("media");
        var initialization = (string?)template.Attribute("initialization");

        if (initialization != null)
            init = new MediaSegment { Index = -1, Url = new Uri(baseUrl, Expand(initialization, repId, bandwidth, 0, 0)), IsInit = true };

        if (media == null) return (init, segments);

        var timeline = template.Element(ns + "SegmentTimeline");
        int index = 0;
        if (timeline != null)
        {
            long number = startNumber;
            long time = 0;
            foreach (var s in timeline.Elements(ns + "S"))
            {
                if (long.TryParse((string?)s.Attribute("t"), out var t)) time = t;
                long d = long.TryParse((string?)s.Attribute("d"), out var dv) ? dv : 0;
                int r = int.TryParse((string?)s.Attribute("r"), out var rv) ? rv : 0;
                if (r < 0) // repete até o fim do período
                    r = d > 0 ? (int)Math.Max(0, Math.Ceiling((periodDuration * timescale - time) / (double)d) - 1) : 0;
                for (int k = 0; k <= r; k++)
                {
                    segments.Add(new MediaSegment
                    {
                        Index = index++,
                        Url = new Uri(baseUrl, Expand(media, repId, bandwidth, number, time)),
                        Duration = d / (double)timescale
                    });
                    number++;
                    time += d;
                }
            }
        }
        else
        {
            long duration = long.TryParse((string?)template.Attribute("duration"), out var dur) ? dur : 0;
            if (duration <= 0) return (init, segments);
            double segSeconds = duration / (double)timescale;
            int count = periodDuration > 0 ? (int)Math.Ceiling(periodDuration / segSeconds) : 0;
            if (count <= 0) count = 1;
            for (int k = 0; k < count; k++)
            {
                long number = startNumber + k;
                segments.Add(new MediaSegment
                {
                    Index = index++,
                    Url = new Uri(baseUrl, Expand(media, repId, bandwidth, number, k * duration)),
                    Duration = segSeconds
                });
            }
        }

        return (init, segments);
    }

    private static string Expand(string template, string repId, long bandwidth, long number, long time)
    {
        return Regex.Replace(template, @"\$(RepresentationID|Bandwidth|Number|Time)(%0(\d+)d)?\$", m =>
        {
            string name = m.Groups[1].Value;
            int pad = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            string value = name switch
            {
                "RepresentationID" => repId,
                "Bandwidth" => bandwidth.ToString(CultureInfo.InvariantCulture),
                "Number" => number.ToString(CultureInfo.InvariantCulture),
                "Time" => time.ToString(CultureInfo.InvariantCulture),
                _ => m.Value
            };
            return pad > 0 ? value.PadLeft(pad, '0') : value;
        }).Replace("$$", "$");
    }

    // ------------------------------------------------------------------ util

    private static Uri ResolveBase(Uri current, XElement el, XNamespace ns)
    {
        var b = el.Element(ns + "BaseURL")?.Value?.Trim();
        return string.IsNullOrEmpty(b) ? current : new Uri(current, b);
    }

    private static double ParseTimescaled(XElement el, string attr, string tsAttr)
    {
        if (!double.TryParse((string?)el.Attribute(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return 0;
        double ts = double.TryParse((string?)el.Attribute(tsAttr), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 1;
        return d / ts;
    }

    private static (long? Start, long? Length) ParseRange(string? range)
    {
        if (string.IsNullOrEmpty(range)) return (null, null);
        var m = Regex.Match(range, @"(\d+)-(\d+)");
        if (!m.Success) return (null, null);
        long a = long.Parse(m.Groups[1].Value), b = long.Parse(m.Groups[2].Value);
        return (a, b - a + 1);
    }

    private static double ParseFrameRate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) return n / d;
        return n;
    }

    /// <summary>ISO 8601 (PT1H2M3.5S) → segundos.</summary>
    public static double ParseDuration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return 0;
        var m = Regex.Match(iso, @"P(?:(\d+)D)?T?(?:(\d+)H)?(?:(\d+)M)?(?:([\d.]+)S)?", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        double total = 0;
        if (m.Groups[1].Success) total += int.Parse(m.Groups[1].Value) * 86400;
        if (m.Groups[2].Success) total += int.Parse(m.Groups[2].Value) * 3600;
        if (m.Groups[3].Success) total += int.Parse(m.Groups[3].Value) * 60;
        if (m.Groups[4].Success) total += double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        return total;
    }
}
