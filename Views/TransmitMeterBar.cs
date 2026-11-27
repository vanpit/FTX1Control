using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FTX1Control.ViewModels;

namespace FTX1Control.Views;

public sealed class TransmitMeterBar : Control
{
    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<TransmitMeterBar, double?>(nameof(Value));
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TransmitMeterBar, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<bool> IsSwrProperty =
        AvaloniaProperty.Register<TransmitMeterBar, bool>(nameof(IsSwr));
    public static readonly StyledProperty<IBrush> FillProperty =
        AvaloniaProperty.Register<TransmitMeterBar, IBrush>(nameof(Fill), Brushes.DeepSkyBlue);

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#2A3D47"));
    private static readonly IBrush SwrNormalBrush = new SolidColorBrush(Color.Parse("#78B855"));
    private static readonly IBrush SwrWarningBrush = new SolidColorBrush(Color.Parse("#D59A42"));
    private static readonly IBrush SwrCriticalBrush = new SolidColorBrush(Color.Parse("#E53935"));
    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.Parse("#E9F8FF"));

    static TransmitMeterBar() => AffectsRender<TransmitMeterBar>(
        ValueProperty, MaximumProperty, IsSwrProperty, FillProperty);

    public double? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool IsSwr
    {
        get => GetValue(IsSwrProperty);
        set => SetValue(IsSwrProperty, value);
    }

    public IBrush Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        if (width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var trackHeight = 6.0;
        var trackTop = (Bounds.Height - trackHeight) / 2;
        if (IsSwr)
        {
            var warningX = width * (MainViewModel.SwrWarningThreshold - 1) / 2;
            var criticalX = width * (MainViewModel.SwrCriticalThreshold - 1) / 2;
            context.DrawRectangle(SwrNormalBrush, null, new Rect(0, trackTop, warningX, trackHeight), 2, 2);
            context.DrawRectangle(SwrWarningBrush, null, new Rect(warningX, trackTop, criticalX - warningX, trackHeight), 2, 2);
            context.DrawRectangle(SwrCriticalBrush, null, new Rect(criticalX, trackTop, width - criticalX, trackHeight), 2, 2);
        }
        else
        {
            context.DrawRectangle(TrackBrush, null, new Rect(0, trackTop, width, trackHeight), 3, 3);
        }

        if (Value is not double value || !double.IsFinite(value) || Maximum <= 0)
        {
            return;
        }

        var ratio = IsSwr ? Math.Clamp((value - 1) / 2, 0, 1) : Math.Clamp(value / Maximum, 0, 1);
        var markerX = Math.Clamp(ratio * width, Math.Min(4, width / 2), Math.Max(width - 4, width / 2));
        if (IsSwr)
        {
            context.DrawRectangle(MarkerBrush, null, new Rect(markerX - 1.5, 0, 3, Bounds.Height), 1.5, 1.5);
        }
        else
        {
            if (ratio > 0)
            {
                context.DrawRectangle(Fill, null, new Rect(0, trackTop, ratio * width, trackHeight), 3, 3);
            }
            context.DrawEllipse(MarkerBrush, null, new Point(markerX, Bounds.Height / 2), 4, 4);
        }
    }
}
