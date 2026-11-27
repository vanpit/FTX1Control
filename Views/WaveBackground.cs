using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FTX1Control.Views;

public sealed class WaveBackground : Control
{
    public static readonly StyledProperty<double> PhaseProperty =
        AvaloniaProperty.Register<WaveBackground, double>(nameof(Phase));

    private const double WaveSpacing = 120;
    private static readonly Pen WavePen = new(new SolidColorBrush(Color.FromArgb(38, 35, 154, 198)), 1);

    static WaveBackground() => AffectsRender<WaveBackground>(PhaseProperty);

    public double Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var maximumRadius = Math.Sqrt(Bounds.Width * Bounds.Width + Bounds.Height * Bounds.Height);
        var topRight = new Point(Bounds.Width, 0);
        var bottomLeft = new Point(0, Bounds.Height);
        for (var radius = Math.Clamp(Phase, 0, 1) * WaveSpacing; radius <= maximumRadius; radius += WaveSpacing)
        {
            if (radius <= 0)
            {
                continue;
            }

            using (context.PushOpacity(Math.Min(radius / WaveSpacing, 1)))
            {
                context.DrawEllipse(null, WavePen, topRight, radius, radius);
                context.DrawEllipse(null, WavePen, bottomLeft, radius, radius);
            }
        }
    }
}
