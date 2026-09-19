using System.Globalization;

namespace Velox.Core.Utils;

public static class FormatHelper
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0
            ? string.Format(Culture, "{0} B", bytes)
            : string.Format(Culture, v >= 100 ? "{0:0} {1}" : "{0:0.#} {1}", v, Units[u]);
    }

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0.5) return "0 B/s";
        double v = bytesPerSecond;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0
            ? string.Format(Culture, "{0:0} B/s", v)
            : string.Format(Culture, v >= 100 ? "{0:0} {1}/s" : "{0:0.#} {1}/s", v, Units[u]);
    }

    public static string Eta(TimeSpan? eta)
    {
        if (eta is null) return "—";
        var t = eta.Value;
        if (t.TotalDays >= 1) return "> 1 dia";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}min";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}min {t.Seconds:00}s";
        return $"{Math.Max(1, t.Seconds)}s";
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}min {t.Seconds:00}s";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}min {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }

    public static string Percent(double progress) =>
        string.Format(Culture, "{0:0.#}%", Math.Clamp(progress, 0, 1) * 100);
}
