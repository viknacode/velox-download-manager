using System.Windows;
using System.Windows.Media;
using Velox.Core.Models;

namespace Velox.App.Controls;

/// <summary>
/// Barra de progresso que desenha cada segmento do download individualmente
/// (estilo IDM): partes concluídas, partes ativas e divisões entre conexões.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentSnapshot>), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalSizeProperty = DependencyProperty.Register(
        nameof(TotalSize), typeof(long), typeof(SegmentBar),
        new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(SegmentBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(SegmentBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x33)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x7C, 0x6C, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(
        nameof(ActiveBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompletedBrushProperty = DependencyProperty.Register(
        nameof(CompletedBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(SegmentBar),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowDividersProperty = DependencyProperty.Register(
        nameof(ShowDividers), typeof(bool), typeof(SegmentBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SegmentSnapshot>? Segments
    {
        get => (IReadOnlyList<SegmentSnapshot>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long TotalSize { get => (long)GetValue(TotalSizeProperty); set => SetValue(TotalSizeProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush ActiveBrush { get => (Brush)GetValue(ActiveBrushProperty); set => SetValue(ActiveBrushProperty, value); }
    public Brush CompletedBrush { get => (Brush)GetValue(CompletedBrushProperty); set => SetValue(CompletedBrushProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public bool ShowDividers { get => (bool)GetValue(ShowDividersProperty); set => SetValue(ShowDividersProperty, value); }

    private static readonly Brush DividerBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x0A, 0x0C, 0x13));

    static SegmentBar()
    {
        DividerBrush.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double r = Math.Min(Radius, h / 2);
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);

        var segs = Segments;
        long total = TotalSize;

        if (IsIndeterminate)
        {
            // faixa animada simples (deslocada pelo tempo)
            double t = (Environment.TickCount64 % 1600) / 1600.0;
            double bw = w * 0.28;
            double x = -bw + (w + bw) * t;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), r, r));
            dc.DrawRectangle(ActiveBrush, null, new Rect(x, 0, bw, h));
            dc.Pop();
            return;
        }

        if (segs == null || segs.Count == 0 || total <= 0)
        {
            double pw = w * Math.Clamp(Progress, 0, 1);
            if (pw > 0.5) dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, 0, pw, h), r, r);
            return;
        }

        bool allDone = true;
        foreach (var s in segs) if (!s.IsCompleted) { allDone = false; break; }

        if (allDone)
        {
            dc.DrawRoundedRectangle(CompletedBrush, null, new Rect(0, 0, w, h), r, r);
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), r, r));

        foreach (var s in segs)
        {
            if (s.Downloaded <= 0) continue;
            // alinha às bordas de pixel para não deixar emendas entre segmentos adjacentes
            double x1 = Math.Floor(w * s.Start / total);
            double x2 = Math.Ceiling(w * (s.Start + s.Downloaded) / total);
            if (x2 - x1 < 1) x2 = x1 + 1;
            Brush brush = s.IsActive ? ActiveBrush : FillBrush;
            dc.DrawRectangle(brush, null, new Rect(x1, 0, x2 - x1, h));
        }

        if (ShowDividers && h >= 6 && !allDone && segs.Count <= 64)
        {
            foreach (var s in segs)
            {
                if (s.Start == 0) continue;
                double x = Math.Round(w * s.Start / total);
                dc.DrawRectangle(DividerBrush, null, new Rect(x, 0, 1, h));
            }
        }

        dc.Pop();
    }
}
