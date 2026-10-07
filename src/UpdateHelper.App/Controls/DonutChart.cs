using System.Windows;
using System.Windows.Media;
using UpdateHelper.Presentation;

namespace UpdateHelper.App.Controls;

/// <summary>环形图：按 ChartSlice 的角度画圆环的各段。没有数据时画一圈浅灰。</summary>
public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices), typeof(IReadOnlyList<ChartSlice>), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(DonutChart),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSlice>? Slices
    {
        get => (IReadOnlyList<ChartSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;

        if (Slices is not { Count: > 0 } slices)
        {
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), Thickness), center, radius, radius);
            return;
        }

        foreach (var s in slices)
        {
            var pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Color)), Thickness);
            if (s.SweepAngle >= 359.99) { dc.DrawEllipse(null, pen, center, radius, radius); continue; }

            // 各段之间留 1.5 度的缝，看起来更清楚
            var gap = slices.Count > 1 ? Math.Min(1.5, s.SweepAngle / 3) : 0;
            var start = s.StartAngle + gap / 2;
            var sweep = s.SweepAngle - gap;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(PointAt(center, radius, start), false, false);
                ctx.ArcTo(PointAt(center, radius, start + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>12 点方向为 0 度、顺时针增加。</summary>
    private static Point PointAt(Point c, double r, double degrees)
    {
        var rad = (degrees - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}
