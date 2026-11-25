using System;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FTX1Control.ViewModels;

namespace FTX1Control.Views;

public partial class MainWindow : Window
{
    private static readonly string WindowPositionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FTX1Control",
        "window-position.txt");
    private PixelPoint? _savedWindowPosition;
    private PixelPoint? _lastKnownWindowPosition;
    private double _lastCatLogVerticalOffset;
    private double _catLogManualScrollOffset;
    private Size _lastCatLogExtent;
    private Size _lastCatLogViewport;
    private bool _catLogContentUpdatePending;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private const double VolumeWheelStep = 5;

    public MainWindow()
    {
        InitializeComponent();
        _savedWindowPosition = LoadWindowPosition();
        CatLogScrollViewer.ScrollChanged += OnCatLogScrollChanged;
        Opened += (_, _) =>
        {
            UpdateClock();
            _clockTimer.Start();
            if (_savedWindowPosition is PixelPoint savedPosition)
            {
                Position = savedPosition;
            }

            _lastKnownWindowPosition = Position;
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        PositionChanged += (_, _) => _lastKnownWindowPosition = Position;
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.PropertyChanged += OnMainViewModelPropertyChanged;
                viewModel.CatLogOldestLineRemoved += OnCatLogOldestLineRemoved;
                await viewModel.TryConnectOnStartupAsync();
            }
        };
        Closing += (_, _) => SaveWindowPosition();
        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
                viewModel.CatLogOldestLineRemoved -= OnCatLogOldestLineRemoved;
                viewModel.Dispose();
            }
        };
    }

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CatLog))
        {
            if (DataContext is MainViewModel { IsCatLogAutoScroll: true })
            {
                Dispatcher.UIThread.Post(ScrollCatLogToBottom);
            }
            else
            {
                _catLogContentUpdatePending = true;
                Dispatcher.UIThread.Post(RestoreCatLogManualScrollPosition, DispatcherPriority.Render);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsCatLogAutoScroll))
        {
            if (DataContext is MainViewModel { IsCatLogAutoScroll: true })
            {
                Dispatcher.UIThread.Post(ScrollCatLogToBottom);
            }
            else
            {
                _catLogManualScrollOffset = CatLogScrollViewer.Offset.Y;
            }
        }
        else if (sender is MainViewModel viewModel
            && e.PropertyName == nameof(MainViewModel.MainVolumeLevel))
        {
            UpdateVolumeFill(MainVolumeFill, viewModel.MainVolumeLevel);
        }
        else if (sender is MainViewModel subViewModel
            && e.PropertyName == nameof(MainViewModel.SubVolumeLevel))
        {
            UpdateVolumeFill(SubVolumeFill, subViewModel.SubVolumeLevel);
        }
    }

    private void OnCatLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var currentOffset = CatLogScrollViewer.Offset.Y;
        var extent = CatLogScrollViewer.Extent;
        var viewport = CatLogScrollViewer.Viewport;
        var layoutChanged = extent != _lastCatLogExtent || viewport != _lastCatLogViewport;

        if (!layoutChanged
            && currentOffset < _lastCatLogVerticalOffset - 0.5
            && DataContext is MainViewModel viewModel
            && viewModel.IsCatLogAutoScroll)
        {
            viewModel.IsCatLogAutoScroll = false;
        }
        else if (!layoutChanged
            && !_catLogContentUpdatePending
            && DataContext is MainViewModel { IsCatLogAutoScroll: false })
        {
            _catLogManualScrollOffset = currentOffset;
        }

        _lastCatLogVerticalOffset = currentOffset;
        _lastCatLogExtent = extent;
        _lastCatLogViewport = viewport;
    }

    private void OnCatLogOldestLineRemoved()
    {
        if (DataContext is MainViewModel { IsCatLogAutoScroll: false }
            && CatLogText.TextLayout.TextLines.Count > 0)
        {
            _catLogManualScrollOffset = Math.Max(0, _catLogManualScrollOffset - CatLogText.TextLayout.TextLines[0].Height);
        }
    }

    private void RestoreCatLogManualScrollPosition()
    {
        _catLogContentUpdatePending = false;
        if (DataContext is not MainViewModel { IsCatLogAutoScroll: false })
        {
            return;
        }

        var maximumOffset = Math.Max(0, CatLogScrollViewer.Extent.Height - CatLogScrollViewer.Viewport.Height);
        var manualOffset = Math.Clamp(_catLogManualScrollOffset, 0, maximumOffset);
        CatLogScrollViewer.Offset = new Vector(CatLogScrollViewer.Offset.X, manualOffset);
        _catLogManualScrollOffset = CatLogScrollViewer.Offset.Y;
        _lastCatLogVerticalOffset = CatLogScrollViewer.Offset.Y;
    }

    private void ScrollCatLogToBottom()
    {
        if (DataContext is not MainViewModel { IsCatLogAutoScroll: true })
        {
            return;
        }

        var bottomOffset = Math.Max(0, CatLogScrollViewer.Extent.Height - CatLogScrollViewer.Viewport.Height);
        CatLogScrollViewer.Offset = new Vector(CatLogScrollViewer.Offset.X, bottomOffset);
        _lastCatLogVerticalOffset = CatLogScrollViewer.Offset.Y;
    }

    private void OnComPortDropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.RefreshComPorts();
        }
    }

    private void OnPinnedFrequencyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Button { DataContext: PinnedFrequencyViewModel frequency } button
            && !(e.Source is Control source
                && (source is TextBox || source.FindAncestorOfType<TextBox>() is not null))
            && e.GetCurrentPoint(button).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            e.Handled = true;
            frequency.RemoveCommand.Execute(null);
        }
    }

    private void OnPinnedCallSignTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        var text = textBox.Text ?? string.Empty;
        var upperText = text.ToUpperInvariant();
        if (text == upperText)
        {
            return;
        }

        var caretIndex = textBox.CaretIndex;
        textBox.Text = upperText;
        textBox.CaretIndex = Math.Min(caretIndex, upperText.Length);
    }

    private void OnMainVolumeWheelChanged(object? sender, PointerWheelEventArgs e) =>
        AdjustVolumeWithWheel(e, isMainVfo: true);

    private void OnSubVolumeWheelChanged(object? sender, PointerWheelEventArgs e) =>
        AdjustVolumeWithWheel(e, isMainVfo: false);

    private void OnMainVolumePointerPressed(object? sender, PointerPressedEventArgs e) =>
        SetVolumeFromPointer(MainVolumeTrack, e, isMainVfo: true);

    private void OnSubVolumePointerPressed(object? sender, PointerPressedEventArgs e) =>
        SetVolumeFromPointer(SubVolumeTrack, e, isMainVfo: false);

    private void OnRfPowerSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.CommitRfPowerSliderValue();
        }
    }

    private void OnRfPowerSliderKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
            && DataContext is MainViewModel viewModel)
        {
            viewModel.CommitRfPowerSliderValue();
        }
    }

    private static void UpdateVolumeFill(Border volumeFill, double level)
    {
        ((ScaleTransform)volumeFill.RenderTransform!).ScaleY = Math.Clamp(level, 0, 255) / 255;
    }

    private void SetVolumeFromPointer(Border volumeTrack, PointerPressedEventArgs e, bool isMainVfo)
    {
        e.Handled = true;
        if (volumeTrack.Bounds.Height <= 0
            || DataContext is not MainViewModel viewModel
            || !viewModel.IsConnected)
        {
            return;
        }

        var position = e.GetPosition(volumeTrack);
        var levelRatio = Math.Clamp(1 - position.Y / volumeTrack.Bounds.Height, 0, 1);
        var level = Math.Round(levelRatio * 255, MidpointRounding.AwayFromZero);
        if (isMainVfo)
        {
            viewModel.SetVolumeLevelFromUser(0, level);
        }
        else
        {
            viewModel.SetVolumeLevelFromUser(1, level);
        }
    }

    private void AdjustVolumeWithWheel(PointerWheelEventArgs e, bool isMainVfo)
    {
        if (e.Delta.Y == 0)
        {
            return;
        }

        e.Handled = true;
        if (DataContext is not MainViewModel viewModel || !viewModel.IsConnected)
        {
            return;
        }

        var currentLevel = isMainVfo ? viewModel.MainVolumeLevel : viewModel.SubVolumeLevel;
        var step = Math.Sign(e.Delta.Y) * VolumeWheelStep;
        var newLevel = Math.Clamp(currentLevel + step, 0, 255);
        viewModel.SetVolumeLevelFromUser(isMainVfo ? 0 : 1, newLevel);
    }

    private static PixelPoint? LoadWindowPosition()
    {
        if (!File.Exists(WindowPositionPath))
        {
            return null;
        }

        try
        {
            var coordinates = File.ReadAllText(WindowPositionPath).Split(',');
            if (coordinates.Length != 2
                || !int.TryParse(coordinates[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(coordinates[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                Trace.TraceError("The saved window position is invalid.");
                return null;
            }

            return new PixelPoint(x, y);
        }
        catch (IOException exception)
        {
            Trace.TraceError($"Unable to read the saved window position: {exception}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Trace.TraceError($"Unable to read the saved window position: {exception}");
        }

        return null;
    }

    private void SaveWindowPosition()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WindowPositionPath)!);
            var position = Position;
            if (position == default && _lastKnownWindowPosition is PixelPoint lastKnownPosition)
            {
                position = lastKnownPosition;
            }

            File.WriteAllText(
                WindowPositionPath,
                $"{position.X.ToString(CultureInfo.InvariantCulture)},{position.Y.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (IOException exception)
        {
            Trace.TraceError($"Unable to save the window position: {exception}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Trace.TraceError($"Unable to save the window position: {exception}");
        }
    }
}