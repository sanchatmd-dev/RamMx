using System.Windows;
using System.Windows.Media;

namespace RamMx.App;

public sealed class RamChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(double[]), typeof(RamChart), new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public double[] Values { get => (double[])GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double width = ActualWidth, height = ActualHeight;
        if (width < 1 || height < 1) return;
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush"); var border = (System.Windows.Media.Brush)FindResource("BorderBrush");
        for (int i = 0; i <= 4; i++) dc.DrawLine(new Pen(border, .5), new(0, height * i / 4), new(width, height * i / 4));
        if (Values.Length < 2) return;
        var geometry = new StreamGeometry(); using (var context = geometry.Open())
        {
            context.BeginFigure(new(0, height * (1 - Math.Clamp(Values[0], 0, 100) / 100)), false, false);
            for (int i = 1; i < Values.Length; i++) context.LineTo(new(width * i / (Values.Length - 1), height * (1 - Math.Clamp(Values[i], 0, 100) / 100)), true, false);
        }
        geometry.Freeze(); dc.DrawGeometry(null, new Pen(accent, 2.5), geometry);
    }
}
