using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Velox.Core.Utils;

namespace Velox.App.Controls;

/// <summary>Gráfico de área com o histórico de velocidade (últimos ~60 s).</summary>
public sealed class SpeedGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(0x30, 0x8C, 0x93, 0xAB)), 1) { DashStyle = DashStyles.Dash };
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0x63, 0x7A));
    private static readonly Typeface LabelFace = new("Segoe UI");

    static SpeedGraph()
    {
        GridPen.Freeze();
        LabelBrush.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var values = Values;
        int n = values?.Count ?? 0;
        int cap = Math.Max(2, Capacity);

        double max = 1;
        if (values != null) foreach (var v in values) if (v > max) max = v;
        max *= 1.15;

        double top = ShowLabels ? 14 : 4;
        double bottom = h - 1;
        double plotH = bottom - top;

        if (ShowGrid)
        {
            for (int i = 1; i <= 3; i++)
            {
                double y = Math.Round(top + plotH * i / 4) + 0.5;
                dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            }
        }

        if (n < 2 || values == null)
        {
            dc.DrawLine(new Pen(Stroke, 1.5), new Point(0, bottom), new Point(w, bottom));
            return;
        }

        double step = w / (cap - 1);
        double x0 = w - (n - 1) * step;

        var fillGeo = new StreamGeometry();
        var lineGeo = new StreamGeometry();
        using (var f = fillGeo.Open())
        using (var l = lineGeo.Open())
        {
            f.BeginFigure(new Point(x0, bottom), true, true);
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * step;
                double y = bottom - plotH * Math.Clamp(values[i] / max, 0, 1);
                f.LineTo(new Point(x, y), true, false);
                if (i == 0) l.BeginFigure(new Point(x, y), false, false);
                else l.LineTo(new Point(x, y), true, true);
            }
            f.LineTo(new Point(x0 + (n - 1) * step, bottom), true, false);
        }
        fillGeo.Freeze();
        lineGeo.Freeze();

        if (Fill != null) dc.DrawGeometry(Fill, null, fillGeo);
        dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, lineGeo);

        if (ShowLabels)
        {
            var peak = new FormattedText(FormatHelper.Speed(max / 1.15), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelFace, 10, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(peak, new Point(0, 0));
        }
    }
}
