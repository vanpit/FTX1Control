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
    private static readonly Pen[] TrailPens =
    {
        new(new SolidColorBrush(Color.FromArgb(12, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(11, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(10, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(9, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(8, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(7, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(6, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(5, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(4, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(3, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(2, 35, 154, 198)), 1),
        new(new SolidColorBrush(Color.FromArgb(1, 35, 154, 198)), 1)
    };

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
                for (var i = 0; i < TrailPens.Length; i++)
                {
                    var trailRadius = radius - i - 1;
                    if (trailRadius <= 0)
                    {
                        break;
                    }

                    context.DrawEllipse(null, TrailPens[i], topRight, trailRadius, trailRadius);
                    context.DrawEllipse(null, TrailPens[i], bottomLeft, trailRadius, trailRadius);
                }

                context.DrawEllipse(null, WavePen, topRight, radius, radius);
                context.DrawEllipse(null, WavePen, bottomLeft, radius, radius);
            }
        }
    }
}
