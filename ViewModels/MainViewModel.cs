using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FTX1Control.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using FTX1Control.Services;
using System.IO;
using System.Globalization;

namespace FTX1Control.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const int MaximumCatLogLines = 300;
    private const int MaximumPinnedFrequencies = 5;
    private const long VfoSwapDetectionWindowMilliseconds = 1000;
    private static readonly IBrush TransmitIndicatorRedBrush = new ImmutableSolidColorBrush(Color.FromRgb(229, 57, 53));

    private static readonly (int Raw, double Watts)[] PowerCalibration =
    {
        (57, 5),
        (72, 10),
        (85, 15),
        (97, 20),
        (104, 25),
        (113, 30)
    };

    private static readonly (int Raw, double Swr)[] SwrCalibration =
    {
        (3, 1.0),
        (22, 1.2),
        (65, 1.5),
        (113, 2.0),
        (175, 3.0),
        (220, 5.0)
    };

    private readonly CatSerialConnection _cat = new();
    private readonly Queue<string> _catLogLines = new();
    private readonly object _catLogFileSync = new();
    private string? _catLogFilePath;
    private CancellationTokenSource? _transmitMeterPollingCancellation;
    private Task? _transmitMeterPollingTask;
    private readonly SemaphoreSlim _dLevelOperation = new(1, 1);
    private TaskCompletionSource<bool>? _meterDetectorReadCompletion;
    private bool _requestedPeakMeterDetector;
    private int? _mainMeterCode;
    private int? _subMeterCode;
    private int? _mainFrequencyHz;
    private int? _subFrequencyHz;
    private char? _mainModeCode;
    private char? _subModeCode;
    private bool _isApplyingRadioAudioGain;
    private double _mainVolumeBeforeMute;
    private double _subVolumeBeforeMute;
    private bool _isApplyingRadioRfPower;
    private readonly Dictionary<int, int> _rfPowerLimits = new();
    private int? _rfPowerWatts;
    private CancellationTokenSource? _rfPowerChangeCancellation;
    private TaskCompletionSource<int>? _rfPowerReadCompletion;
    private (int Vfo, int Band, bool IsAm)? _rfPowerContext;
    private PendingVfoSwap? _pendingVfoSwap;
    private PendingAudioGainUpdate? _pendingAudioGainUpdate;

    private readonly record struct AudioGainState(
        double MainLevel,
        double SubLevel,
        bool MainMuted,
        bool SubMuted,
        double MainLevelBeforeMute,
        double SubLevelBeforeMute);
    private readonly record struct PendingVfoSwap(
        int FirstVfo,
        int FirstFrequencyHz,
        int ExpectedVfo,
        int ExpectedFrequencyHz,
        long StartedAtMilliseconds);
    private readonly record struct PendingAudioGainUpdate(AudioGainState State, int UpdatedVfoMask, long ReceivedAtMilliseconds);

    [ObservableProperty]
    private ComPortOption? _selectedComPort;

    [ObservableProperty]
    private string _selectedBaudRate = "4800";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isAlwaysOnTop = true;

    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    private bool _isConnecting;

    private string _connectionStatus = "NIEPOŁĄCZONY";
    private string _statusMessage = "Wybierz port COM i połącz się z radiem FTX-1.";
    private string _catLogFileError = string.Empty;

    [ObservableProperty]
    private string _catLog = string.Empty;

    [ObservableProperty]
    private bool _isCatLogAutoScroll = true;

    [ObservableProperty]
    private string _powerOutputReading = "-- W";

    [ObservableProperty]
    private string _swrReading = "--";

    private double _swrValue;

    [ObservableProperty]
    private string _alcReading = "-- %";

    [ObservableProperty]
    private int _alcPercent;

    [ObservableProperty]
    private string _mainFrequencyDisplay = "--";

    [ObservableProperty]
    private string _subFrequencyDisplay = "--";

    [ObservableProperty]
    private string _mainModulationDisplay = "--";

    [ObservableProperty]
    private string _subModulationDisplay = "--";

    [ObservableProperty]
    private double _mainVolumeLevel;

    [ObservableProperty]
    private double _subVolumeLevel;

    [ObservableProperty]
    private bool _isMainVolumeMuted;

    [ObservableProperty]
    private bool _isSubVolumeMuted;

    [ObservableProperty]
    private int _sMeterRaw;

    [ObservableProperty]
    private string _sMeterReading = "--";

    [ObservableProperty]
    private decimal? _dLevel;

    [ObservableProperty]
    private bool _isDLevelUpdating;

    [ObservableProperty]
    private int? _noiseBlankerLevel;

    [ObservableProperty]
    private bool _isNoiseBlankerUpdating;

    [ObservableProperty]
    private int? _digitalNoiseReductionLevel;

    [ObservableProperty]
    private bool _isDigitalNoiseReductionUpdating;

    [ObservableProperty]
    private bool _isTransmitting;

    [ObservableProperty]
    private int? _transmitVfo;

    [ObservableProperty]
    private bool? _isPeakMeterDetector;

    [ObservableProperty]
    private bool _isMeterDetectorUpdating;

    [ObservableProperty]
    private double _rfPowerSliderValue = 5;

    public int SMeterSRangeValue => Math.Min(SMeterRaw, 125);
    public int SMeterOverRangeValue => Math.Max(SMeterRaw - 125, 0);
    public string DLevelReading => DLevel is decimal level
        ? $"{level.ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture)} dB"
        : "-- dB";
    public bool CanIncreaseDLevel => IsConnected && !IsDLevelUpdating && DLevel is < 30m;
    public bool CanDecreaseDLevel => IsConnected && !IsDLevelUpdating && DLevel is > -30m;
    public string NoiseBlankerLevelReading => NoiseBlankerLevel is 0
        ? "OFF"
        : NoiseBlankerLevel?.ToString(CultureInfo.InvariantCulture) ?? "--";
    public bool CanIncreaseNoiseBlankerLevel => IsConnected && !IsNoiseBlankerUpdating && NoiseBlankerLevel is < 10;
    public bool CanDecreaseNoiseBlankerLevel => IsConnected && !IsNoiseBlankerUpdating && NoiseBlankerLevel is > 0;
    public string DigitalNoiseReductionLevelReading => DigitalNoiseReductionLevel is 0
        ? "OFF"
        : DigitalNoiseReductionLevel?.ToString(CultureInfo.InvariantCulture) ?? "--";
    public bool CanIncreaseDigitalNoiseReductionLevel => IsConnected && !IsDigitalNoiseReductionUpdating && DigitalNoiseReductionLevel is < 10;
    public bool CanDecreaseDigitalNoiseReductionLevel => IsConnected && !IsDigitalNoiseReductionUpdating && DigitalNoiseReductionLevel is > 0;

    public ObservableCollection<string> BaudRates { get; } = new ObservableCollection<string> { "4800", "9600", "19200", "38400", "115200" };

    public ObservableCollection<ComPortOption> AvailableComPorts { get; } = new();
    public ObservableCollection<MeterOptionViewModel> MainMeters { get; }
    public ObservableCollection<MeterOptionViewModel> SubMeters { get; }
    public ObservableCollection<PinnedFrequencyViewModel> MainPinnedFrequencies { get; } = new();
    public ObservableCollection<PinnedFrequencyViewModel> SubPinnedFrequencies { get; } = new();

    public event Action? CatLogOldestLineRemoved;

    public string ConnectionStatus
    {
        get => LanguageService.TranslateStatus(_connectionStatus);
        set
        {
            if (_connectionStatus == value)
            {
                return;
            }

            _connectionStatus = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => LanguageService.TranslateStatus(_statusMessage);
        set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string CatLogFileError
    {
        get => LanguageService.TranslateStatus(_catLogFileError);
        set
        {
            if (_catLogFileError == value)
            {
                return;
            }

            _catLogFileError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCatLogFileError));
        }
    }

    public string ConnectButtonText => IsConnecting
        ? LanguageService.TranslateStatus("Łączenie...")
        : IsConnected
            ? LanguageService.TranslateStatus("Rozłącz")
            : LanguageService.TranslateStatus("Połącz");
    public bool CanSelectComPort => !IsConnected && !IsConnecting;
    public bool CanSelectBaudRate => !IsConnected && !IsConnecting;
    public string CatLogAutoScrollButtonText => LanguageService.TranslateStatus(
        IsCatLogAutoScroll ? "AUTO-PRZEWIJANIE: WŁ." : "AUTO-PRZEWIJANIE: WYŁ.");
    public bool HasCatLogFileError => !string.IsNullOrEmpty(_catLogFileError);
    public bool IsAverageMeterDetector => IsPeakMeterDetector == false;
    public bool IsPeakMeterDetectorSelected => IsPeakMeterDetector == true;
    public bool CanSelectMeterDetector => IsConnected && !IsMeterDetectorUpdating;
    public bool CanTuneAntenna => IsConnected && !IsTransmitting;
    public string RfPowerReading => _rfPowerWatts is int watts ? $"{watts} W" : "-- W";
    public bool IsRfPower5Selected => _rfPowerWatts == 5;
    public bool IsRfPower10Selected => _rfPowerWatts == 10;
    public bool IsRfPower15Selected => _rfPowerWatts == 15;
    public bool IsRfPower20Selected => _rfPowerWatts == 20;
    public bool IsRfPower25Selected => _rfPowerWatts == 25;
    public bool IsRfPower30Selected => _rfPowerWatts == 30;
    public bool IsRfPower50Selected => _rfPowerWatts == 50;
    public bool IsRfPower100Selected => _rfPowerWatts == 100;
    public int MaximumRfPower
    {
        get
        {
            if (_rfPowerContext is not { } context || !_rfPowerLimits.TryGetValue(context.Band, out var maximum))
            {
                return 5;
            }

            var amItem = context.Band is 8 or 9 ? 11 : 10;
            return context.IsAm && _rfPowerLimits.TryGetValue(amItem, out var amMaximum)
                ? Math.Min(maximum, amMaximum)
                : maximum;
        }
    }
    public bool CanSetRfPower => IsConnected && _rfPowerWatts.HasValue && _rfPowerContext.HasValue
        && _rfPowerLimits.ContainsKey(_rfPowerContext.Value.Band)
        && (!_rfPowerContext.Value.IsAm || _rfPowerLimits.ContainsKey(_rfPowerContext.Value.Band is 8 or 9 ? 11 : 10));
    public bool CanPinMainFrequency => IsConnected && _mainFrequencyHz.HasValue && _mainModeCode.HasValue;
    public bool CanPinSubFrequency => IsConnected && _subFrequencyHz.HasValue && _subModeCode.HasValue;
    public string MainVfoStateText => TransmitVfo switch { 0 => "TRX", 1 => "RX", _ => string.Empty };
    public string SubVfoStateText => TransmitVfo switch { 0 => "RX", 1 => "TRX", _ => string.Empty };
    public IBrush MainVfoStateBrush => GetVfoStateBrush(TransmitVfo == 0);
    public IBrush SubVfoStateBrush => GetVfoStateBrush(TransmitVfo == 1);
    public IBrush MainFrequencyBrush => GetFrequencyBrush(IsTransmitting && TransmitVfo == 0, TransmitVfo == 1);
    public IBrush SubFrequencyBrush => GetFrequencyBrush(IsTransmitting && TransmitVfo == 1, TransmitVfo == 0);
    public double MainFrequencyFontSize => TransmitVfo == 0 ? 28 : 22;
    public double SubFrequencyFontSize => TransmitVfo == 1 ? 28 : 22;
    public IBrush ConnectionIndicatorBrush => IsConnected
        ? new SolidColorBrush(Color.FromRgb(182, 214, 108))
        : new SolidColorBrush(Color.FromRgb(103, 117, 114));
    public string TransmitStatusText => LanguageService.TranslateStatus(
        IsTransmitting ? "TX - NADAWANIE" : "RX - ODBIÓR");
    public IBrush TransmitStatusBrush => IsTransmitting
        ? TransmitIndicatorRedBrush
        : new SolidColorBrush(Color.FromRgb(40, 106, 154));
    public IBrush AlcIndicatorBrush => AlcPercent > 70
        ? new SolidColorBrush(Color.FromRgb(231, 76, 60))
        : AlcPercent > 50
            ? new SolidColorBrush(Color.FromRgb(213, 154, 66))
            : new SolidColorBrush(Color.FromRgb(233, 239, 237));
    public IBrush SwrIndicatorBrush => _swrValue > 2.0
        ? new SolidColorBrush(Color.FromRgb(229, 57, 53))
        : _swrValue > 1.5
            ? new SolidColorBrush(Color.FromRgb(213, 154, 66))
            : new SolidColorBrush(Color.FromRgb(233, 239, 237));

    public MainViewModel()
    {
        _selectedLanguageIndex = LanguageService.LoadPreferredLanguageIndex();
        LanguageService.SetLanguage(_selectedLanguageIndex, savePreference: false);
        InitializeCatLogFile();
        MainMeters = CreateMeterOptions(0);
        SubMeters = CreateMeterOptions(1);
        RefreshComPorts();
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        LanguageService.SetLanguage(value, savePreference: true);
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(CatLogFileError));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(CatLogAutoScrollButtonText));
        OnPropertyChanged(nameof(TransmitStatusText));
        foreach (var frequency in MainPinnedFrequencies.Concat(SubPinnedFrequencies))
        {
            frequency.NotifyLanguageChanged();
        }
    }

    partial void OnIsCatLogAutoScrollChanged(bool value) =>
        OnPropertyChanged(nameof(CatLogAutoScrollButtonText));

    private void InitializeCatLogFile()
    {
        try
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documentsPath))
            {
                throw new IOException("Nie znaleziono folderu Dokumenty.");
            }

            var directory = Path.Combine(documentsPath, "FTX1ControlLogs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"CAT_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fffffff}_{Guid.NewGuid():N}.txt");
            using (File.Create(path))
            {
            }

            _catLogFilePath = path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            Trace.TraceError($"Unable to create CAT log file: {exception}");
            CatLogFileError = $"Nie można utworzyć pliku logu CAT: {exception.Message}";
        }
    }

    [RelayCommand]
    private void ToggleMainVolumeMute() => ToggleVolumeMute(isMainVfo: true);

    [RelayCommand]
    private void ToggleSubVolumeMute() => ToggleVolumeMute(isMainVfo: false);

    private void ToggleVolumeMute(bool isMainVfo)
    {
        if (!IsConnected)
        {
            return;
        }

        var currentLevel = isMainVfo ? MainVolumeLevel : SubVolumeLevel;
        var previousLevel = isMainVfo ? _mainVolumeBeforeMute : _subVolumeBeforeMute;
        var isMuted = isMainVfo ? IsMainVolumeMuted : IsSubVolumeMuted;

        if (isMuted)
        {
            if (previousLevel <= 0)
            {
                return;
            }

            if (isMainVfo)
            {
                IsMainVolumeMuted = false;
                SetVolumeLevelFromUser(0, previousLevel);
            }
            else
            {
                IsSubVolumeMuted = false;
                SetVolumeLevelFromUser(1, previousLevel);
            }

            return;
        }

        if (currentLevel > 0)
        {
            if (isMainVfo)
            {
                _mainVolumeBeforeMute = currentLevel;
                IsMainVolumeMuted = true;
                SetVolumeLevelFromUser(0, 0);
            }
            else
            {
                _subVolumeBeforeMute = currentLevel;
                IsSubVolumeMuted = true;
                SetVolumeLevelFromUser(1, 0);
            }
        }
    }

    public void SetVolumeLevelFromUser(int vfo, double level)
    {
        if (vfo is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(vfo), "VFO must be 0 (MAIN) or 1 (SUB).");
        }

        if (!IsConnected)
        {
            return;
        }

        var normalizedLevel = Math.Clamp(level, 0, 255);
        var currentLevel = vfo == 0 ? MainVolumeLevel : SubVolumeLevel;
        if (currentLevel == normalizedLevel)
        {
            return;
        }

        if (vfo == 0)
        {
            MainVolumeLevel = normalizedLevel;
        }
        else
        {
            SubVolumeLevel = normalizedLevel;
        }

        SendAudioGain(vfo, normalizedLevel);
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await StopTransmitMeterPollingAsync();
            _cat.AutoInformationReceived -= OnAutoInformationReceived;
            _cat.AutoInformationError -= OnAutoInformationError;
            Exception? disconnectException = null;
            try
            {
                _cat.Disconnect();
            }
            catch (Exception exception)
            {
                disconnectException = exception;
            }

            _cat.CatFrameLogged -= OnCatFrameLogged;
            ClearRadioReadings();
            IsConnected = false;
            ConnectionStatus = "NIEPOŁĄCZONY";
            StatusMessage = disconnectException is null
                ? "Połączenie z portem szeregowym zostało zamknięte."
                : $"Port został zamknięty, ale nie udało się wyłączyć Auto Information: {disconnectException.Message}";
            return;
        }

        await ConnectAsync();
    }

    public async Task TryConnectOnStartupAsync()
    {
        if (IsConnected || IsConnecting)
        {
            return;
        }

        RefreshComPorts();
        if (SelectedComPort is not null)
        {
            await ConnectAsync(retryOnStartup: true);
        }
    }

    public void RefreshComPorts()
    {
        var previouslySelectedPort = SelectedComPort?.PortName;
        if (!OperatingSystem.IsWindows())
        {
            AvailableComPorts.Clear();
            SelectedComPort = null;
            StatusMessage = "Skanowanie portów CAT jest dostępne tylko w systemie Windows.";
            return;
        }

        try
        {
            var scannedPorts = ComPortScanner.Scan();
            AvailableComPorts.Clear();
            foreach (var port in scannedPorts)
            {
                AvailableComPorts.Add(port);
            }

            SelectedComPort = AvailableComPorts.FirstOrDefault(port =>
                    string.Equals(port.PortName, previouslySelectedPort, StringComparison.OrdinalIgnoreCase))
                ?? AvailableComPorts.FirstOrDefault(port => port.IsStandard)
                ?? AvailableComPorts.FirstOrDefault();

            if (SelectedComPort is null)
            {
                StatusMessage = "Nie wykryto portu CAT Silicon Labs typu Enhanced ani Standard.";
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SecurityException
                or PlatformNotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            Trace.TraceError($"Unable to scan CAT COM ports: {exception}");
            AvailableComPorts.Clear();
            SelectedComPort = null;
            StatusMessage = $"Nie udało się zeskanować portów COM: {exception.Message}";
        }
    }

    private async Task ConnectAsync(bool retryOnStartup = false)
    {
        if (SelectedComPort is not ComPortOption selectedComPort)
        {
            StatusMessage = "Wybierz wykryty port COM Silicon Labs.";
            return;
        }

        IsConnecting = true;
        var maximumAttempts = retryOnStartup ? 3 : 1;
        try
        {
            if (retryOnStartup)
            {
                await Task.Delay(500);
            }

            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                ConnectionStatus = "ŁĄCZENIE";
                StatusMessage = attempt == 1
                    ? $"Próba połączenia z {selectedComPort.PortName}..."
                    : $"Ponawianie połączenia ({attempt}/{maximumAttempts})...";
                _cat.CatFrameLogged -= OnCatFrameLogged;
                _cat.CatFrameLogged += OnCatFrameLogged;

                try
                {
                    var portName = selectedComPort.PortName;
                    var baudRate = int.Parse(SelectedBaudRate);
                    var connectionReadings = await Task.Run(() =>
                    {
                        _cat.Connect(portName, baudRate);
                        var meters = _cat.ReadMeters();
                        var readings = _cat.ReadRadioState();
                        var transmitVfo = _cat.ReadTransmitVfo();
                        var vfoModes = _cat.ReadVfoModes();
                        var audioGains = _cat.ReadAudioGains();
                        bool? isPeakMeterDetector = null;
                        string? meterDetectorError = null;
                        (int Watts, Dictionary<int, int> Limits, string? Warning)? rfPowerSettings = null;
                        string? rfPowerError = null;
                        try
                        {
                            isPeakMeterDetector = _cat.ReadPeakMeterDetector();
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
                        {
                            meterDetectorError = exception.Message;
                        }

                        try
                        {
                            rfPowerSettings = _cat.ReadRfPowerSettings();
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
                        {
                            rfPowerError = exception.Message;
                        }

                        return (meters, readings, transmitVfo, vfoModes, audioGains, isPeakMeterDetector, meterDetectorError, rfPowerSettings, rfPowerError);
                    });
                    _mainMeterCode = connectionReadings.meters.Main;
                    _subMeterCode = connectionReadings.meters.Sub;
                    UpdateMeterSelection(MainMeters, _mainMeterCode);
                    UpdateMeterSelection(SubMeters, _subMeterCode);
                    ApplyRadioReadings(
                        connectionReadings.readings,
                        connectionReadings.transmitVfo,
                        connectionReadings.vfoModes,
                        connectionReadings.audioGains);
                    IsPeakMeterDetector = connectionReadings.isPeakMeterDetector;
                    _rfPowerLimits.Clear();
                    _rfPowerWatts = null;
                    if (connectionReadings.rfPowerSettings is { } powerSettings)
                    {
                        foreach (var (item, limit) in powerSettings.Limits)
                        {
                            _rfPowerLimits.Add(item, limit);
                        }

                        ApplyRfPowerFromRadio(powerSettings.Watts);
                    }
                    _cat.AutoInformationReceived += OnAutoInformationReceived;
                    _cat.AutoInformationError += OnAutoInformationError;
                    _cat.EnableAutoInformation();
                    IsConnected = true;
                    ConnectionStatus = "POŁĄCZONY";
                    StatusMessage = connectionReadings.meterDetectorError is null
                        ? $"Połączono z {selectedComPort.DisplayName} ({SelectedBaudRate} bps). Auto Information jest włączone."
                        : $"Połączono, ale nie udało się odczytać AVG/PEAK: {connectionReadings.meterDetectorError}";
                    if (connectionReadings.rfPowerError is not null)
                    {
                        StatusMessage = $"{_statusMessage} Nie udało się odczytać mocy SPA-1: {connectionReadings.rfPowerError}";
                    }
                    else if (connectionReadings.rfPowerSettings?.Warning is string powerWarning)
                    {
                        StatusMessage = $"{_statusMessage} Nie udało się odczytać niektórych limitów mocy: {powerWarning}";
                    }
                    StartTransmitMeterPolling();
                    RequestDLevelForCurrentFrequency();
                    RequestNoiseControlLevelsForCurrentFrequency();
                    return;
                }
                catch (Exception exception)
                {
                    _cat.AutoInformationReceived -= OnAutoInformationReceived;
                    _cat.AutoInformationError -= OnAutoInformationError;
                    var errorMessage = exception.Message;
                    try
                    {
                        _cat.Disconnect();
                    }
                    catch (Exception disconnectException)
                    {
                        errorMessage += $" Błąd zamykania portu: {disconnectException.Message}";
                    }

                    _cat.CatFrameLogged -= OnCatFrameLogged;
                    IsConnected = false;

                    if (attempt < maximumAttempts)
                    {
                        ConnectionStatus = "ŁĄCZENIE";
                        StatusMessage = $"Nie udało się połączyć: {errorMessage} Ponawianie...";
                        await Task.Delay(750 * attempt);
                        continue;
                    }

                    ConnectionStatus = "BŁĄD POŁĄCZENIA";
                    StatusMessage = errorMessage;
                }
            }
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanToggleConnection() => !IsConnecting && (IsConnected || SelectedComPort is not null);

    private void StartTransmitMeterPolling()
    {
        _transmitMeterPollingCancellation = new CancellationTokenSource();
        _transmitMeterPollingTask = PollTransmitMetersAsync(_transmitMeterPollingCancellation.Token);
    }

    [RelayCommand]
    private Task IncreaseDLevelAsync() => ChangeDLevelAsync(0.5m);

    [RelayCommand]
    private Task DecreaseDLevelAsync() => ChangeDLevelAsync(-0.5m);

    private async Task ChangeDLevelAsync(decimal step)
    {
        if (!IsConnected || DLevel is null
            || (step > 0 ? !CanIncreaseDLevel : !CanDecreaseDLevel))
        {
            return;
        }

        IsDLevelUpdating = true;
        try
        {
            await _dLevelOperation.WaitAsync();
            try
            {
                if (!IsConnected || DLevel is not decimal latest
                    || (step > 0 ? latest >= 30m : latest <= -30m))
                {
                    return;
                }

                var requested = Math.Clamp(latest + step, -30m, 30m);
                _cat.SetDLevel(requested);
                DLevel = requested;
            }
            finally
            {
                _dLevelOperation.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {
            DLevel = null;
            StatusMessage = $"Nie udało się ustawić D-LEVEL: {exception.Message}";
        }
        finally
        {
            IsDLevelUpdating = false;
        }
    }

    private void RequestDLevelForCurrentFrequency()
    {
        try
        {
            _cat.RequestDLevel();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            DLevel = null;
            StatusMessage = $"Nie udało się odczytać D-LEVEL: {exception.Message}";
        }
    }

    [RelayCommand]
    private Task IncreaseNoiseBlankerLevelAsync() => ChangeNoiseBlankerLevelAsync(1);

    [RelayCommand]
    private Task DecreaseNoiseBlankerLevelAsync() => ChangeNoiseBlankerLevelAsync(-1);

    private Task ChangeNoiseBlankerLevelAsync(int step)
    {
        if (!IsConnected || NoiseBlankerLevel is not int level
            || (step > 0 ? !CanIncreaseNoiseBlankerLevel : !CanDecreaseNoiseBlankerLevel))
        {
            return Task.CompletedTask;
        }

        IsNoiseBlankerUpdating = true;
        try
        {
            var requested = Math.Clamp(level + step, 0, 10);
            _cat.SetNoiseBlankerLevel(requested);
            NoiseBlankerLevel = requested;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {
            NoiseBlankerLevel = null;
            StatusMessage = $"Nie udało się ustawić poziomu NB: {exception.Message}";
        }
        finally
        {
            IsNoiseBlankerUpdating = false;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task IncreaseDigitalNoiseReductionLevelAsync() => ChangeDigitalNoiseReductionLevelAsync(1);

    [RelayCommand]
    private Task DecreaseDigitalNoiseReductionLevelAsync() => ChangeDigitalNoiseReductionLevelAsync(-1);

    private Task ChangeDigitalNoiseReductionLevelAsync(int step)
    {
        if (!IsConnected || DigitalNoiseReductionLevel is not int level
            || (step > 0 ? !CanIncreaseDigitalNoiseReductionLevel : !CanDecreaseDigitalNoiseReductionLevel))
        {
            return Task.CompletedTask;
        }

        IsDigitalNoiseReductionUpdating = true;
        try
        {
            var requested = Math.Clamp(level + step, 0, 10);
            _cat.SetDigitalNoiseReductionLevel(requested);
            DigitalNoiseReductionLevel = requested;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {
            DigitalNoiseReductionLevel = null;
            StatusMessage = $"Nie udało się ustawić poziomu DNR: {exception.Message}";
        }
        finally
        {
            IsDigitalNoiseReductionUpdating = false;
        }

        return Task.CompletedTask;
    }

    private void RequestNoiseControlLevelsForCurrentFrequency()
    {
        try
        {
            _cat.RequestNoiseBlankerLevel();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            NoiseBlankerLevel = null;
            StatusMessage = $"Nie udało się odczytać poziomu NB: {exception.Message}";
        }

        try
        {
            _cat.RequestDigitalNoiseReductionLevel();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            DigitalNoiseReductionLevel = null;
            StatusMessage = $"Nie udało się odczytać poziomu DNR: {exception.Message}";
        }
    }

    private async Task PollTransmitMetersAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsTransmitting)
                {
                    await Task.Run(_cat.RequestTransmitMeterReadings, cancellationToken);
                }

                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsConnected)
                {
                    StatusMessage = $"Błąd odczytu mierników TX CAT: {exception.Message}";
                }
            });
        }
    }

    private async Task StopTransmitMeterPollingAsync()
    {
        var cancellation = _transmitMeterPollingCancellation;
        if (cancellation is null)
        {
            return;
        }

        _transmitMeterPollingCancellation = null;
        cancellation.Cancel();
        if (_transmitMeterPollingTask is not null)
        {
            await _transmitMeterPollingTask;
        }

        cancellation.Dispose();
        _transmitMeterPollingTask = null;
    }

    private void ApplyRadioReadings(
        (int MainFrequencyHz, int SubFrequencyHz, bool IsTransmitting) readings,
        int transmitVfo,
        (char Main, char Sub) vfoModes,
        (int Main, int Sub) audioGains)
    {
        var mainMode = GetModeDisplay(vfoModes.Main)
            ?? throw new InvalidDataException($"Nieobsługiwany kod CAT MD0: {vfoModes.Main}.");
        var subMode = GetModeDisplay(vfoModes.Sub)
            ?? throw new InvalidDataException($"Nieobsługiwany kod CAT MD1: {vfoModes.Sub}.");

        UpdateFrequency(0, readings.MainFrequencyHz);
        UpdateFrequency(1, readings.SubFrequencyHz);
        UpdateMode(0, vfoModes.Main, mainMode);
        UpdateMode(1, vfoModes.Sub, subMode);
        SetAudioGainFromRadio(0, audioGains.Main);
        SetAudioGainFromRadio(1, audioGains.Sub);
        IsTransmitting = readings.IsTransmitting;
        TransmitVfo = transmitVfo;
        ClearTransmitMeterReadings();
        ClearSMeterReading();
    }

    private void OnAutoInformationReceived(string message)
    {
        Dispatcher.UIThread.Post(() => ApplyAutoInformation(message));
    }

    private void OnAutoInformationError(Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsConnected)
            {
                StatusMessage = $"Błąd odbioru Auto Information CAT: {exception.Message}";
            }
        });
    }

    private void OnCatFrameLogged(string frame)
    {
        lock (_catLogFileSync)
        {
            var singleLineFrame = frame.Replace("\r", "\\r").Replace("\n", "\\n");
            var line = $"{DateTime.Now:HH:mm:ss.fff} {DescribeCatFrame(singleLineFrame)}";
            if (_catLogFilePath is not null)
            {
                try
                {
                    File.AppendAllText(_catLogFilePath, line + Environment.NewLine);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
                {
                    Trace.TraceError($"Unable to write CAT log file: {exception}");
                    _catLogFilePath = null;
                    Dispatcher.UIThread.Post(() =>
                        CatLogFileError = $"Nie można zapisać logu CAT; dalsze wpisy nie będą zapisywane: {exception.Message}");
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                _catLogLines.Enqueue(line);
                while (_catLogLines.Count > MaximumCatLogLines)
                {
                    _catLogLines.Dequeue();
                    CatLogOldestLineRemoved?.Invoke();
                }

                CatLog = string.Join(Environment.NewLine, _catLogLines);
            });
        }
    }

    private static string DescribeCatFrame(string frame)
    {
        const string receivePrefix = "[RX] ";
        if (!frame.StartsWith(receivePrefix, StringComparison.Ordinal))
        {
            return frame;
        }

        var message = frame.Substring(receivePrefix.Length).TrimEnd(';');
        if (message.Equals("TX", StringComparison.OrdinalIgnoreCase))
        {
            return $"{frame} — {Localize("zdarzenie rozpoczęcia nadawania", "transmit start event")}";
        }

        if (message.Equals("RX", StringComparison.OrdinalIgnoreCase))
        {
            return $"{frame} — {Localize("zdarzenie powrotu do odbioru", "return to receive event")}";
        }

        if (message.StartsWith("RI", StringComparison.OrdinalIgnoreCase)
            && message.Length == 10 && message[5] is '0' or '1' or '2')
        {
            return $"{frame} — {Localize("status radia, TX", "radio status, TX")}: {(message[5] == '1' ? Localize("aktywne", "active") : Localize("nieaktywne", "inactive"))}";
        }

        if (TryParseTransmitVfo(message, out var transmitVfo))
        {
            return $"{frame} — {Localize("VFO nadawcze", "transmit VFO")}: {(transmitVfo == 0 ? "MAIN" : "SUB")}";
        }

        if (TryParseVfoMode(message, out var modeVfo, out var modeDisplay))
        {
            return $"{frame} — {Localize("tryb", "mode")} {(modeVfo == 0 ? "MAIN" : "SUB")}: {modeDisplay}";
        }

        if (TryParseVfoAudioGain(message, out var audioGainVfo, out var audioGain))
        {
            return $"{frame} — {Localize("głośność", "volume")} {(audioGainVfo == 0 ? "MAIN" : "SUB")}: {FormatAudioGainDisplay(audioGain)} (CAT: {audioGain}/255)";
        }

        if (TryParseVfoInformation(message, out var informationVfo, out var informationFrequency, out var informationMode))
        {
            return $"{frame} — {Localize("stan VFO", "VFO state")} {(informationVfo == 0 ? "MAIN" : "SUB")}: {FormatFrequency(informationFrequency)}, {GetModeDisplay(informationMode)}";
        }

        if (CatSerialConnection.TryParseMeterDetector(message, out var isPeak))
        {
            return $"{frame} — {Localize("detektor miernika", "meter detector")}: {(isPeak ? "PEAK" : "AVG")}";
        }

        if (CatSerialConnection.TryParseRfPower(message, out var rfPower))
        {
            return $"{frame} — {Localize("ustawiona moc RF", "RF power set to")}: {rfPower} W";
        }

        if (CatSerialConnection.TryParseRfPowerLimit(message, out var powerLimitItem, out var maximumRfPower))
        {
            return $"{frame} — {Localize("limit mocy RF, pozycja CAT", "RF power limit, CAT item")} {powerLimitItem}: {maximumRfPower} W";
        }

        if (CatSerialConnection.TryParseDLevel(message, out var dLevel))
        {
            return $"{frame} — D-LEVEL: {dLevel:+0.0;-0.0;+0.0} dB";
        }

        if (CatSerialConnection.TryParseNoiseControlLevel(message, "NL", out var noiseBlankerLevel))
        {
            return $"{frame} — {Localize("poziom NB", "NB level")}: {noiseBlankerLevel}";
        }

        if (CatSerialConnection.TryParseNoiseControlLevel(message, "RL", out var digitalNoiseReductionLevel))
        {
            return $"{frame} — {Localize("poziom DNR", "DNR level")}: {digitalNoiseReductionLevel}";
        }

        if ((message.StartsWith("FA", StringComparison.OrdinalIgnoreCase)
                || message.StartsWith("FB", StringComparison.OrdinalIgnoreCase))
            && message.Length == 11
            && int.TryParse(message.AsSpan(2, 9), out var frequency))
        {
            var vfo = message.StartsWith("FA", StringComparison.OrdinalIgnoreCase) ? "MAIN" : "SUB";
            return $"{frame} — {Localize("częstotliwość VFO", "VFO frequency")} {vfo}: {FormatFrequency(frequency)}";
        }

        if (message.StartsWith("RM", StringComparison.OrdinalIgnoreCase)
            && message.Length == 9
            && int.TryParse(message.AsSpan(2, 1), out var meterCode)
            && int.TryParse(message.AsSpan(3, 3), out var value)
            && int.TryParse(message.AsSpan(6, 3), out var additionalValue))
        {
            return meterCode switch
            {
                4 => $"{frame} — ALC: {ConvertAlcReading(value)}% {Localize($"(wartość CAT: {value}, dodatkowa: {additionalValue})", $"(CAT value: {value}, additional: {additionalValue})")}",
                5 => $"{frame} — {Localize("moc PO", "PO power")}: {Math.Round(ConvertPowerReading(value), MidpointRounding.AwayFromZero):0} W {Localize($"(wartość CAT: {value}, dodatkowa: {additionalValue})", $"(CAT value: {value}, additional: {additionalValue})")}",
                6 => $"{frame} — SWR: {ConvertSwrReading(value):0.0}:1 {Localize($"(wartość CAT: {value}, dodatkowa: {additionalValue})", $"(CAT value: {value}, additional: {additionalValue})")}",
                1 => $"{frame} — {Localize($"S-metr: P2={value} ({FormatSMeterValue(value)}; dodatkowa: {additionalValue})", $"S-meter: P2={value} ({FormatSMeterValue(value)}; additional: {additionalValue})")}",
                _ => $"{frame} — {Localize($"odczyt miernika CAT, kod: {meterCode}, wartość: {value}, dodatkowa: {additionalValue}", $"CAT meter reading, code: {meterCode}, value: {value}, additional: {additionalValue}")}"
            };
        }

        if (message.StartsWith("MS", StringComparison.OrdinalIgnoreCase)
            && message.Length == 4
            && int.TryParse(message.AsSpan(2, 1), out var mainMeter)
            && int.TryParse(message.AsSpan(3, 1), out var subMeter)
            && mainMeter is >= 0 and <= 5 && subMeter is >= 0 and <= 5)
        {
            return $"{frame} — {Localize("wybrane mierniki", "selected meters")}: MAIN {MeterOptionViewModel.GetLabel(mainMeter)}, SUB {MeterOptionViewModel.GetLabel(subMeter)}";
        }

        return $"{frame} — {Localize("nierozpoznana ramka CAT", "unrecognized CAT frame")}";
    }

    private void ApplyAutoInformation(string message)
    {
        try
        {
            if (message.StartsWith("SS04", StringComparison.OrdinalIgnoreCase))
            {
                if (!CatSerialConnection.TryParseDLevel(message, out var level))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT D-LEVEL: {message};");
                }

                DLevel = level;
                return;
            }

            if (message.StartsWith("NL", StringComparison.OrdinalIgnoreCase))
            {
                if (!CatSerialConnection.TryParseNoiseControlLevel(message, "NL", out var level))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT poziomu NB: {message};");
                }

                NoiseBlankerLevel = level;
                return;
            }

            if (message.StartsWith("RL", StringComparison.OrdinalIgnoreCase))
            {
                if (!CatSerialConnection.TryParseNoiseControlLevel(message, "RL", out var level))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT poziomu DNR: {message};");
                }

                DigitalNoiseReductionLevel = level;
                return;
            }

            if (message.Equals("TX", StringComparison.OrdinalIgnoreCase))
            {
                SetTransmitState(true);
                return;
            }

            if (message.Equals("RX", StringComparison.OrdinalIgnoreCase))
            {
                SetTransmitState(false);
                return;
            }

            if (message.StartsWith("AG", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseVfoAudioGain(message, out var vfo, out var audioGain))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT AG: {message};");
                }

                RememberAudioGainStateBeforeRadioUpdate(vfo);
                SetAudioGainFromRadio(vfo, audioGain);
                return;
            }

            if (message.StartsWith("PC", StringComparison.OrdinalIgnoreCase))
            {
                if (!CatSerialConnection.TryParseRfPower(message, out var watts))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT mocy SPA-1: {message};");
                }

                ApplyRfPowerFromRadio(watts);
                _rfPowerReadCompletion?.TrySetResult(watts);
                return;
            }

            if (message.StartsWith("EX0307", StringComparison.OrdinalIgnoreCase)
                && message.Length >= 8
                && int.TryParse(message.AsSpan(6, 2), out var powerLimitItem)
                && powerLimitItem is >= 5 and <= 11)
            {
                if (!CatSerialConnection.TryParseRfPowerLimit(message, out var item, out var watts))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT limitu mocy: {message};");
                }

                _rfPowerLimits[item] = watts;
                RefreshRfPowerControls();
                return;
            }

            if (message.StartsWith("EX030513", StringComparison.OrdinalIgnoreCase))
            {
                if (!CatSerialConnection.TryParseMeterDetector(message, out var isPeak))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT detektora miernika: {message};");
                }

                if (IsConnected)
                {
                    IsPeakMeterDetector = isPeak;
                    if (isPeak == _requestedPeakMeterDetector)
                    {
                        _meterDetectorReadCompletion?.TrySetResult(isPeak);
                    }
                }

                return;
            }

            if (message.StartsWith("MD", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseVfoMode(message, out var vfo, out var modeDisplay))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT MD: {message};");
                }

                UpdateMode(vfo, message[3], modeDisplay);

                return;
            }

            if (message.StartsWith("IF", StringComparison.OrdinalIgnoreCase)
                || message.StartsWith("OI", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseVfoInformation(message, out var vfo, out var frequency, out var modeCode)
                    || GetModeDisplay(modeCode) is not string modeDisplay)
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT informacji VFO: {message};");
                }

                var vfoSwapDetected = DetectVfoSwap(vfo, frequency);
                if (vfoSwapDetected)
                {
                    ApplyVfoSwap();
                }

                UpdateFrequency(vfo, frequency);
                UpdateMode(vfo, modeCode, modeDisplay);
                if (vfoSwapDetected)
                {
                    RefreshPinnedFrequencySelections();
                }

                return;
            }

            if (message.StartsWith("FT", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseTransmitVfo(message, out var transmitVfo))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT FT: {message};");
                }

                TransmitVfo = transmitVfo;
                return;
            }

            if (message.StartsWith("RI", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Length != 10 || !message.StartsWith("RI0", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(message.AsSpan(3, 7), out _)
                    || message[5] is not ('0' or '1' or '2'))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT RI: {message};");
                }

                SetTransmitState(message[5] == '1');
                return;
            }

            if (message.StartsWith("FA", StringComparison.OrdinalIgnoreCase)
                || message.StartsWith("FB", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Length != 11
                    || !int.TryParse(message.AsSpan(2, 9), NumberStyles.None, CultureInfo.InvariantCulture, out var frequency))
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT częstotliwości: {message};");
                }

                var vfo = message.StartsWith("FA", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                var vfoSwapDetected = DetectVfoSwap(vfo, frequency);
                if (vfoSwapDetected)
                {
                    ApplyVfoSwap();
                }

                UpdateFrequency(vfo, frequency);
                if (vfoSwapDetected)
                {
                    RefreshPinnedFrequencySelections();
                }

                return;
            }

            if (message.StartsWith("RM", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Length != 9
                    || !int.TryParse(message.AsSpan(2, 1), out var meterCode)
                    || !int.TryParse(message.AsSpan(3, 3), out var value)
                    || !int.TryParse(message.AsSpan(6, 3), out _)
                    || value is < 0 or > 255)
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT miernika: {message};");
                }

                if (IsTransmitting && meterCode == 5)
                {
                    UpdatePowerReading(value);
                }
                else if (IsTransmitting && meterCode == 6)
                {
                    UpdateSwrReading(value);
                }
                else if (IsTransmitting && meterCode == 4)
                {
                    UpdateAlcReading(value);
                }
                else if (!IsTransmitting && meterCode == 1)
                {
                    UpdateSMeterReading(value);
                }

                return;
            }

            if (message.StartsWith("MS", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Length != 4
                    || !int.TryParse(message.AsSpan(2, 1), out var mainMeter)
                    || !int.TryParse(message.AsSpan(3, 1), out var subMeter)
                    || mainMeter is < 0 or > 5 || subMeter is < 0 or > 5)
                {
                    throw new InvalidDataException($"Nieoczekiwana ramka CAT ustawień mierników: {message};");
                }

                _mainMeterCode = mainMeter;
                _subMeterCode = subMeter;
                UpdateMeterSelection(MainMeters, _mainMeterCode);
                UpdateMeterSelection(SubMeters, _subMeterCode);
            }
        }
        catch (InvalidDataException exception)
        {
            StatusMessage = $"Błąd danych Auto Information CAT: {exception.Message}";
        }
    }

    private void ClearTransmitMeterReadings()
    {
        PowerOutputReading = "-- W";
        SwrReading = "--";
        _swrValue = 0;
        OnPropertyChanged(nameof(SwrIndicatorBrush));
        AlcReading = "-- %";
        AlcPercent = 0;
    }

    private void UpdateSwrReading(int raw)
    {
        _swrValue = ConvertSwrReading(raw);
        SwrReading = $"{_swrValue:0.0}:1";
        OnPropertyChanged(nameof(SwrIndicatorBrush));
    }

    private void SetTransmitState(bool isTransmitting)
    {
        IsTransmitting = isTransmitting;
        if (isTransmitting)
        {
            ClearSMeterReading();
        }
        else
        {
            ClearTransmitMeterReadings();
        }
    }

    private void UpdatePowerReading(int raw)
    {
        PowerOutputReading = $"{Math.Round(ConvertPowerReading(raw), MidpointRounding.AwayFromZero):0} W";
    }

    private void UpdateAlcReading(int raw)
    {
        AlcPercent = ConvertAlcReading(raw);
        AlcReading = $"{AlcPercent}%";
    }

    private void ClearRadioReadings()
    {
        _meterDetectorReadCompletion?.TrySetCanceled();
        _meterDetectorReadCompletion = null;
        CancelRfPowerChange();
        _rfPowerLimits.Clear();
        _rfPowerWatts = null;
        _rfPowerContext = null;
        RefreshRfPowerControls();
        DLevel = null;
        NoiseBlankerLevel = null;
        DigitalNoiseReductionLevel = null;
        IsPeakMeterDetector = null;
        MainFrequencyDisplay = "--";
        SubFrequencyDisplay = "--";
        _mainFrequencyHz = null;
        _subFrequencyHz = null;
        _mainModeCode = null;
        _subModeCode = null;
        _pendingVfoSwap = null;
        _pendingAudioGainUpdate = null;
        OnPropertyChanged(nameof(CanPinMainFrequency));
        OnPropertyChanged(nameof(CanPinSubFrequency));
        MainModulationDisplay = "--";
        SubModulationDisplay = "--";
        SetAudioGainFromRadio(0, 0);
        SetAudioGainFromRadio(1, 0);
        TransmitVfo = null;
        IsTransmitting = false;
        ClearTransmitMeterReadings();
        ClearSMeterReading();
    }

    private void ClearSMeterReading()
    {
        SMeterRaw = 0;
        SMeterReading = "--";
    }

    private void UpdateSMeterReading(int raw)
    {
        SMeterRaw = raw;
        SMeterReading = FormatSMeterValue(raw);
    }

    private static string FormatFrequency(int frequencyHz) =>
        $"{frequencyHz / 1_000_000}.{frequencyHz % 1_000_000 / 1000:000} {frequencyHz % 1000:000} MHz";

    private void UpdateFrequency(int vfo, int frequencyHz)
    {
        var frequencyChanged = (vfo == 0 ? _mainFrequencyHz : _subFrequencyHz) != frequencyHz;
        if (frequencyChanged)
        {
            DLevel = null;
        }

        if (frequencyChanged && vfo == 0)
        {
            NoiseBlankerLevel = null;
            DigitalNoiseReductionLevel = null;
        }

        if (vfo == 0)
        {
            _mainFrequencyHz = frequencyHz;
            MainFrequencyDisplay = FormatFrequency(frequencyHz);
            UpdatePinnedFrequencySelection(MainPinnedFrequencies, IsConnected ? frequencyHz : null, _mainModeCode);
            OnPropertyChanged(nameof(CanPinMainFrequency));
        }
        else
        {
            _subFrequencyHz = frequencyHz;
            SubFrequencyDisplay = FormatFrequency(frequencyHz);
            UpdatePinnedFrequencySelection(SubPinnedFrequencies, IsConnected ? frequencyHz : null, _subModeCode);
            OnPropertyChanged(nameof(CanPinSubFrequency));
        }

        UpdateRfPowerContext();
        if (frequencyChanged && IsConnected)
        {
            RequestDLevelForCurrentFrequency();
            if (vfo == 0)
            {
                RequestNoiseControlLevelsForCurrentFrequency();
            }
        }
    }

    private void UpdateMode(int vfo, char modeCode, string modeDisplay)
    {
        modeCode = char.ToUpperInvariant(modeCode);
        if (vfo == 0)
        {
            _mainModeCode = modeCode;
            MainModulationDisplay = modeDisplay;
            UpdatePinnedFrequencySelection(MainPinnedFrequencies, IsConnected ? _mainFrequencyHz : null, modeCode);
            OnPropertyChanged(nameof(CanPinMainFrequency));
        }
        else
        {
            _subModeCode = modeCode;
            SubModulationDisplay = modeDisplay;
            UpdatePinnedFrequencySelection(SubPinnedFrequencies, IsConnected ? _subFrequencyHz : null, modeCode);
            OnPropertyChanged(nameof(CanPinSubFrequency));
        }

        UpdateRfPowerContext();
    }

    [RelayCommand]
    private void PinMainFrequency() => PinFrequency(0, _mainFrequencyHz, _mainModeCode, MainPinnedFrequencies);

    [RelayCommand]
    private void PinSubFrequency() => PinFrequency(1, _subFrequencyHz, _subModeCode, SubPinnedFrequencies);

    private void PinFrequency(int vfo, int? frequencyHz, char? modeCode, ObservableCollection<PinnedFrequencyViewModel> frequencies)
    {
        if (!IsConnected || frequencyHz is not int frequency || modeCode is not char mode
            || GetModeDisplay(mode) is not string modeDisplay)
        {
            return;
        }

        var existing = frequencies.FirstOrDefault(item => item.FrequencyHz == frequency);
        var callSign = existing?.CallSign ?? string.Empty;
        if (existing is not null)
        {
            frequencies.Remove(existing);
        }

        frequencies.Insert(0, CreatePinnedFrequency(vfo, frequency, mode, modeDisplay, frequencies, callSign));
        if (frequencies.Count > MaximumPinnedFrequencies)
        {
            frequencies.RemoveAt(frequencies.Count - 1);
        }

        UpdatePinnedFrequencySelection(frequencies, frequency, mode);
    }

    private PinnedFrequencyViewModel CreatePinnedFrequency(
        int vfo,
        int frequencyHz,
        char modeCode,
        string modeDisplay,
        ObservableCollection<PinnedFrequencyViewModel> frequencies,
        string callSign = "") =>
        new(
            frequencyHz,
            modeCode,
            modeDisplay,
            () => SelectPinnedFrequency(vfo, frequencyHz, modeCode),
            item => frequencies.Remove(item),
            callSign);

    private void SwapPinnedFrequencyCollections()
    {
        var mainFrequencies = MainPinnedFrequencies.ToArray();
        var subFrequencies = SubPinnedFrequencies.ToArray();
        MainPinnedFrequencies.Clear();
        SubPinnedFrequencies.Clear();

        foreach (var frequency in subFrequencies)
        {
            MainPinnedFrequencies.Add(CreatePinnedFrequency(
                0, frequency.FrequencyHz, frequency.ModeCode, frequency.ModeDisplay, MainPinnedFrequencies, frequency.CallSign));
        }

        foreach (var frequency in mainFrequencies)
        {
            SubPinnedFrequencies.Add(CreatePinnedFrequency(
                1, frequency.FrequencyHz, frequency.ModeCode, frequency.ModeDisplay, SubPinnedFrequencies, frequency.CallSign));
        }
    }

    private void ApplyVfoSwap()
    {
        SwapPinnedFrequencyCollections();
        if (!ApplyRadioReportedAudioGainSwap())
        {
            SwapAudioGainStateForVfoSwap();
        }

        _pendingAudioGainUpdate = null;
    }

    private void SwapAudioGainStateForVfoSwap()
    {
        RestoreAudioGainState(new AudioGainState(
            SubVolumeLevel,
            MainVolumeLevel,
            IsSubVolumeMuted,
            IsMainVolumeMuted,
            _subVolumeBeforeMute,
            _mainVolumeBeforeMute));
    }

    private void RememberAudioGainStateBeforeRadioUpdate(int vfo)
    {
        var now = Environment.TickCount64;
        if (_pendingAudioGainUpdate is PendingAudioGainUpdate pending
            && now - pending.ReceivedAtMilliseconds <= VfoSwapDetectionWindowMilliseconds)
        {
            _pendingAudioGainUpdate = pending with { UpdatedVfoMask = pending.UpdatedVfoMask | (1 << vfo) };
            return;
        }

        _pendingAudioGainUpdate = new PendingAudioGainUpdate(
            CaptureAudioGainState(),
            1 << vfo,
            now);
    }

    private AudioGainState CaptureAudioGainState() =>
        new(
            MainVolumeLevel,
            SubVolumeLevel,
            IsMainVolumeMuted,
            IsSubVolumeMuted,
            _mainVolumeBeforeMute,
            _subVolumeBeforeMute);

    private bool ApplyRadioReportedAudioGainSwap()
    {
        if (_pendingAudioGainUpdate is not PendingAudioGainUpdate pending)
        {
            return false;
        }

        if (Environment.TickCount64 - pending.ReceivedAtMilliseconds > VfoSwapDetectionWindowMilliseconds)
        {
            return false;
        }

        var current = CaptureAudioGainState();
        var swapped = SwapAudioGainState(pending.State);
        var mainWasReported = (pending.UpdatedVfoMask & 1) != 0;
        var subWasReported = (pending.UpdatedVfoMask & 2) != 0;
        var mainMatchesSwapped = mainWasReported
            && ToCatAudioGain(current.MainLevel) == ToCatAudioGain(swapped.MainLevel)
            && current.MainMuted == swapped.MainMuted;
        var subMatchesSwapped = subWasReported
            && ToCatAudioGain(current.SubLevel) == ToCatAudioGain(swapped.SubLevel)
            && current.SubMuted == swapped.SubMuted;
        if ((!mainWasReported || mainMatchesSwapped)
            && (!subWasReported || subMatchesSwapped)
            && (mainMatchesSwapped || subMatchesSwapped))
        {
            RestoreAudioGainState(swapped with
            {
                MainLevel = mainMatchesSwapped ? current.MainLevel : swapped.MainLevel,
                SubLevel = subMatchesSwapped ? current.SubLevel : swapped.SubLevel
            });
            return true;
        }

        return false;
    }

    private static AudioGainState SwapAudioGainState(AudioGainState state) =>
        new(
            state.SubLevel,
            state.MainLevel,
            state.SubMuted,
            state.MainMuted,
            state.SubLevelBeforeMute,
            state.MainLevelBeforeMute);

    private static int ToCatAudioGain(double level) =>
        Math.Clamp((int)Math.Round(level, MidpointRounding.AwayFromZero), 0, 255);

    private void RestoreAudioGainState(AudioGainState state)
    {
        _mainVolumeBeforeMute = state.MainLevelBeforeMute;
        _subVolumeBeforeMute = state.SubLevelBeforeMute;
        _isApplyingRadioAudioGain = true;
        try
        {
            MainVolumeLevel = state.MainLevel;
            SubVolumeLevel = state.SubLevel;
            IsMainVolumeMuted = state.MainMuted;
            IsSubVolumeMuted = state.SubMuted;
        }
        finally
        {
            _isApplyingRadioAudioGain = false;
        }
    }

    private bool DetectVfoSwap(int vfo, int frequencyHz)
    {
        var now = Environment.TickCount64;
        if (_pendingVfoSwap is PendingVfoSwap pending)
        {
            if (now - pending.StartedAtMilliseconds <= VfoSwapDetectionWindowMilliseconds)
            {
                if (vfo == pending.ExpectedVfo && frequencyHz == pending.ExpectedFrequencyHz)
                {
                    _pendingVfoSwap = null;
                    return true;
                }

                if (vfo == pending.FirstVfo && frequencyHz == pending.FirstFrequencyHz)
                {
                    return false;
                }
            }

            _pendingVfoSwap = null;
        }

        if (_mainFrequencyHz is not int mainFrequency
            || _subFrequencyHz is not int subFrequency)
        {
            return false;
        }

        var current = vfo == 0
            ? mainFrequency
            : subFrequency;
        var other = vfo == 0
            ? subFrequency
            : mainFrequency;

        if (frequencyHz != other || frequencyHz == current)
        {
            return false;
        }

        _pendingVfoSwap = new PendingVfoSwap(vfo, frequencyHz, 1 - vfo, current, now);
        return false;
    }

    private void RefreshPinnedFrequencySelections()
    {
        UpdatePinnedFrequencySelection(MainPinnedFrequencies, IsConnected ? _mainFrequencyHz : null, _mainModeCode);
        UpdatePinnedFrequencySelection(SubPinnedFrequencies, IsConnected ? _subFrequencyHz : null, _subModeCode);
    }

    private static void UpdatePinnedFrequencySelection(ObservableCollection<PinnedFrequencyViewModel> frequencies, int? frequencyHz, char? modeCode)
    {
        foreach (var item in frequencies)
        {
            item.IsSelected = item.FrequencyHz == frequencyHz && item.ModeCode == modeCode;
        }
    }

    private void SelectPinnedFrequency(int vfo, int frequencyHz, char modeCode)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            _cat.SetFrequencyAndMode(vfo, frequencyHz, modeCode);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusMessage = $"Nie udało się w pełni ustawić częstotliwości i modulacji {(vfo == 0 ? "MAIN" : "SUB")}: {exception.Message}";
        }
    }

    private static string FormatSMeterValue(int raw)
    {
        if (raw <= 125)
        {
            var sLevel = (int)Math.Round(raw * 9.0 / 125, MidpointRounding.AwayFromZero);
            return $"S{sLevel}";
        }

        var dbOverS9 = raw <= 172
            ? (raw - 125) * 20.0 / 47
            : raw <= 220
                ? 20 + (raw - 172) * 20.0 / 48
                : 40 + (raw - 220) * 20.0 / 35;
        return $"S9+{Math.Round(dbOverS9, MidpointRounding.AwayFromZero):0} dB";
    }

    private static double ConvertPowerReading(int raw)
    {
        var first = PowerCalibration[0];
        var second = PowerCalibration[1];
        if (raw <= first.Raw)
        {
            return Math.Clamp(
                first.Watts + (raw - first.Raw) * (second.Watts - first.Watts) / (second.Raw - first.Raw),
                0,
                100);
        }

        for (var index = 1; index < PowerCalibration.Length; index++)
        {
            var lower = PowerCalibration[index - 1];
            var upper = PowerCalibration[index];
            if (raw <= upper.Raw)
            {
                return lower.Watts + (raw - lower.Raw) * (upper.Watts - lower.Watts) / (upper.Raw - lower.Raw);
            }
        }

        var last = PowerCalibration[PowerCalibration.Length - 1];
        var previous = PowerCalibration[PowerCalibration.Length - 2];
        var extrapolated = last.Watts + (raw - last.Raw) * (last.Watts - previous.Watts) / (last.Raw - previous.Raw);
        return Math.Clamp(extrapolated, 0, 100);
    }

    private static double ConvertSwrReading(int raw)
    {
        var first = SwrCalibration[0];
        if (raw <= first.Raw)
        {
            return first.Swr;
        }

        for (var index = 1; index < SwrCalibration.Length; index++)
        {
            var lower = SwrCalibration[index - 1];
            var upper = SwrCalibration[index];
            if (raw <= upper.Raw)
            {
                return lower.Swr + (raw - lower.Raw) * (upper.Swr - lower.Swr) / (upper.Raw - lower.Raw);
            }
        }

        var last = SwrCalibration[SwrCalibration.Length - 1];
        var previous = SwrCalibration[SwrCalibration.Length - 2];
        return last.Swr + (raw - last.Raw) * (last.Swr - previous.Swr) / (last.Raw - previous.Raw);
    }

    private static int ConvertAlcReading(int raw) =>
        Math.Clamp((int)Math.Round(raw * 100.0 / 255, MidpointRounding.AwayFromZero), 0, 100);

    private static bool TryParseTransmitVfo(string message, out int transmitVfo)
    {
        transmitVfo = -1;
        if (message.Length != 3
            || !message.StartsWith("FT", StringComparison.OrdinalIgnoreCase)
            || message[2] is not ('0' or '1'))
        {
            return false;
        }

        transmitVfo = message[2] - '0';
        return true;
    }

    private static bool TryParseVfoMode(string message, out int vfo, out string modeDisplay)
    {
        vfo = -1;
        modeDisplay = string.Empty;
        if (message.Length != 4
            || !message.StartsWith("MD", StringComparison.OrdinalIgnoreCase)
            || message[2] is not ('0' or '1')
            || GetModeDisplay(message[3]) is not string display)
        {
            return false;
        }

        vfo = message[2] - '0';
        modeDisplay = display;
        return true;
    }

    private static bool TryParseVfoInformation(string message, out int vfo, out int frequencyHz, out char modeCode)
    {
        vfo = -1;
        frequencyHz = 0;
        modeCode = '\0';
        if (message.Length != 29
            || (!message.StartsWith("IF", StringComparison.OrdinalIgnoreCase)
                && !message.StartsWith("OI", StringComparison.OrdinalIgnoreCase))
            || message[16] is not ('+' or '-')
            || !int.TryParse(message.AsSpan(7, 9), NumberStyles.None, CultureInfo.InvariantCulture, out frequencyHz)
            || GetModeDisplay(message[23]) is null)
        {
            return false;
        }

        vfo = message.StartsWith("IF", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        modeCode = char.ToUpperInvariant(message[23]);
        return true;
    }

    private static bool TryParseVfoAudioGain(string message, out int vfo, out int audioGain)
    {
        vfo = -1;
        audioGain = 0;
        if (message.Length != 6
            || !message.StartsWith("AG", StringComparison.OrdinalIgnoreCase)
            || message[2] is not ('0' or '1')
            || !int.TryParse(message.AsSpan(3, 3), out audioGain)
            || audioGain is < 0 or > 255)
        {
            return false;
        }

        vfo = message[2] - '0';
        return true;
    }

    private void SetAudioGainFromRadio(int vfo, int level)
    {
        _isApplyingRadioAudioGain = true;
        try
        {
            if (vfo == 0)
            {
                MainVolumeLevel = level;
                IsMainVolumeMuted = level == 0;
                if (level > 0)
                {
                    _mainVolumeBeforeMute = level;
                }
            }
            else
            {
                SubVolumeLevel = level;
                IsSubVolumeMuted = level == 0;
                if (level > 0)
                {
                    _subVolumeBeforeMute = level;
                }
            }
        }
        finally
        {
            _isApplyingRadioAudioGain = false;
        }
    }

    private static string FormatAudioGainDisplay(int level) =>
        $"{(int)Math.Round(level * 100.0 / 255, MidpointRounding.AwayFromZero)}%";

    private void UpdateRfPowerContext()
    {
        var frequency = TransmitVfo == 0 ? _mainFrequencyHz : TransmitVfo == 1 ? _subFrequencyHz : null;
        var mode = TransmitVfo == 0 ? _mainModeCode : TransmitVfo == 1 ? _subModeCode : null;
        int? band = frequency switch
        {
            > 0 and < 30_000_000 => 5,
            >= 50_000_000 and < 60_000_000 => 6,
            >= 70_000_000 and < 80_000_000 => 7,
            >= 136_000_000 and < 174_000_000 => 8,
            >= 400_000_000 and < 480_000_000 => 9,
            _ => null
        };
        (int Vfo, int Band, bool IsAm)? context =
            TransmitVfo is int vfo && band is int bandItem && mode is char modeCode
                ? (vfo, bandItem, modeCode is '5' or 'D')
                : null;
        if (_rfPowerContext == context)
        {
            return;
        }

        _rfPowerContext = context;
        if (IsConnected && _rfPowerLimits.Count > 0)
        {
            CancelRfPowerChange();
            _rfPowerWatts = null;
            RefreshRfPowerControls();
            var cancellation = new CancellationTokenSource();
            _rfPowerChangeCancellation = cancellation;
            _ = ReadRfPowerForContextAsync(cancellation);
        }
        else
        {
            RefreshRfPowerControls();
        }
    }

    private void ApplyRfPowerFromRadio(int watts)
    {
        _rfPowerWatts = watts;
        RefreshRfPowerControls();
    }

    private void RefreshRfPowerControls()
    {
        _isApplyingRadioRfPower = true;
        try
        {
            OnPropertyChanged(nameof(MaximumRfPower));
            OnPropertyChanged(nameof(CanSetRfPower));
            OnPropertyChanged(nameof(RfPowerReading));
            OnPropertyChanged(nameof(IsRfPower5Selected));
            OnPropertyChanged(nameof(IsRfPower10Selected));
            OnPropertyChanged(nameof(IsRfPower15Selected));
            OnPropertyChanged(nameof(IsRfPower20Selected));
            OnPropertyChanged(nameof(IsRfPower25Selected));
            OnPropertyChanged(nameof(IsRfPower30Selected));
            OnPropertyChanged(nameof(IsRfPower50Selected));
            OnPropertyChanged(nameof(IsRfPower100Selected));
            if (_rfPowerChangeCancellation is null)
            {
                RfPowerSliderValue = Math.Clamp(_rfPowerWatts ?? 5, 5, MaximumRfPower);
            }
        }
        finally
        {
            _isApplyingRadioRfPower = false;
        }

        SelectRfPowerPresetCommand.NotifyCanExecuteChanged();
    }

    private void CancelRfPowerChange()
    {
        var cancellation = _rfPowerChangeCancellation;
        _rfPowerChangeCancellation = null;
        cancellation?.Cancel();
        _rfPowerReadCompletion?.TrySetCanceled();
        _rfPowerReadCompletion = null;
    }

    private async Task ReadRfPowerForContextAsync(CancellationTokenSource cancellation)
    {
        try
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _rfPowerReadCompletion = completion;
            _cat.RequestRfPower();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusMessage = $"Nie udało się odczytać mocy RF: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_rfPowerChangeCancellation, cancellation))
            {
                _rfPowerChangeCancellation = null;
                _rfPowerReadCompletion = null;
                RefreshRfPowerControls();
            }

            cancellation.Dispose();
        }
    }

    partial void OnRfPowerSliderValueChanged(double value)
    {
        if (_isApplyingRadioRfPower || !CanSetRfPower)
        {
            return;
        }

        var watts = Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 5, MaximumRfPower);
        CancelRfPowerChange();
        var cancellation = new CancellationTokenSource();
        _rfPowerChangeCancellation = cancellation;
        _ = SetRfPowerAsync(watts, cancellation);
    }

    private async Task SetRfPowerAsync(int watts, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(150, cancellation.Token);
            if (!CanSetRfPower || watts > MaximumRfPower)
            {
                return;
            }

            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _rfPowerReadCompletion = completion;
            _cat.SetRfPower(watts);
            _cat.RequestRfPower();
            var actual = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellation.Token);
            if (actual != watts)
            {
                StatusMessage = $"Radio ustawiło {actual} W zamiast żądanych {watts} W.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusMessage = $"Nie udało się potwierdzić mocy RF: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_rfPowerChangeCancellation, cancellation))
            {
                _rfPowerChangeCancellation = null;
                _rfPowerReadCompletion = null;
                RefreshRfPowerControls();
            }

            cancellation.Dispose();
        }
    }

    private bool CanSelectRfPowerPreset(string? value) =>
        CanSetRfPower && int.TryParse(value, out var watts) && watts >= 5 && watts <= MaximumRfPower;

    [RelayCommand(CanExecute = nameof(CanSelectRfPowerPreset))]
    private void SelectRfPowerPreset(string? value)
    {
        if (CanSelectRfPowerPreset(value) && int.TryParse(value, out var watts))
        {
            if (RfPowerSliderValue == watts && _rfPowerWatts != watts)
            {
                OnRfPowerSliderValueChanged(watts);
            }
            else
            {
                RfPowerSliderValue = watts;
            }
        }
    }

    private void SendAudioGain(int vfo, double level)
    {
        if (_isApplyingRadioAudioGain || !IsConnected)
        {
            return;
        }

        try
        {
            _cat.SetAudioGain(vfo, Math.Clamp((int)Math.Round(level, MidpointRounding.AwayFromZero), 0, 255));
        }
        catch (Exception exception)
        {
            StatusMessage = $"Nie udało się ustawić głośności {(vfo == 0 ? "MAIN" : "SUB")}: {exception.Message}";
        }
    }

    [RelayCommand]
    private Task SelectAverageMeterDetectorAsync() => SelectMeterDetectorAsync(false);

    [RelayCommand]
    private Task SelectPeakMeterDetectorAsync() => SelectMeterDetectorAsync(true);

    private async Task SelectMeterDetectorAsync(bool isPeak)
    {
        if (!CanSelectMeterDetector || IsPeakMeterDetector == isPeak)
        {
            return;
        }

        IsMeterDetectorUpdating = true;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requestedPeakMeterDetector = isPeak;
        _meterDetectorReadCompletion = completion;
        try
        {
            IsPeakMeterDetector = null;
            _cat.SetPeakMeterDetector(isPeak);
            _cat.RequestMeterDetector();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException) when (completion.Task.IsCanceled)
        {
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusMessage = $"Nie udało się potwierdzić trybu AVG/PEAK: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_meterDetectorReadCompletion, completion))
            {
                _meterDetectorReadCompletion = null;
            }

            IsMeterDetectorUpdating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTuneAntenna))]
    private void StartAntennaTuning()
    {
        try
        {
            _cat.StartAntennaTuning();
            StatusMessage = "Wysłano AC103; — żądanie strojenia anteny.";
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusMessage = $"Nie udało się uruchomić strojenia anteny: {exception.Message}";
        }
    }

    private static string? GetModeDisplay(char modeCode) =>
        char.ToUpperInvariant(modeCode) switch
        {
            '1' => "LSB",
            '2' => "USB",
            '3' => "CW-U",
            '4' => "FM",
            '5' => "AM",
            '6' => "RTTY-L",
            '7' => "CW-L",
            '8' => "DATA-L",
            '9' => "RTTY-U",
            'A' => "DATA-FM",
            'B' => "FM-N",
            'C' => "DATA-U",
            'D' => "AM-N",
            'E' => "PSK",
            'F' => "DATA-FM-N",
            'H' => "C4FM-DN",
            'I' => "C4FM-VW",
            _ => null
        };

    private static string Localize(string polish, string english) =>
        LanguageService.IsEnglish ? english : polish;

    private static IBrush GetVfoStateBrush(bool isTransmitVfo) =>
        new SolidColorBrush(isTransmitVfo
            ? Color.FromRgb(229, 57, 53)
            : Color.FromRgb(182, 214, 108));

    private static IBrush GetFrequencyBrush(bool isTransmittingVfo, bool isReceiveVfo) =>
        isTransmittingVfo
            ? TransmitIndicatorRedBrush
            : isReceiveVfo
                ? new SolidColorBrush(Color.FromRgb(211, 211, 211))
                : new SolidColorBrush(Color.FromRgb(33, 150, 243));

    private ObservableCollection<MeterOptionViewModel> CreateMeterOptions(int vfo)
    {
        var meters = new ObservableCollection<MeterOptionViewModel>();
        foreach (var meter in MeterOptionViewModel.AvailableMeters)
        {
            meters.Add(new MeterOptionViewModel(meter.Label, meter.Code, () => SelectMeter(vfo, meter.Code)));
        }

        return meters;
    }

    private void SelectMeter(int vfo, int meterCode)
    {
        try
        {
            var mainMeter = vfo == 0 ? meterCode : _mainMeterCode
                ?? throw new InvalidOperationException("MAIN meter setting is not available.");
            var subMeter = vfo == 1 ? meterCode : _subMeterCode
                ?? throw new InvalidOperationException("SUB meter setting is not available.");
            _cat.SetMeters(mainMeter, subMeter);
            _mainMeterCode = mainMeter;
            _subMeterCode = subMeter;

            StatusMessage = $"Wysłano MS{mainMeter}{subMeter}; MAIN: {MeterOptionViewModel.GetLabel(mainMeter)}, SUB: {MeterOptionViewModel.GetLabel(subMeter)}.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }

        UpdateMeterSelection(MainMeters, _mainMeterCode);
        UpdateMeterSelection(SubMeters, _subMeterCode);
    }

    private static void UpdateMeterSelection(ObservableCollection<MeterOptionViewModel> meters, int? selectedCode)
    {
        foreach (var meter in meters)
        {
            meter.IsSelected = meter.Code == selectedCode;
        }
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectionIndicatorBrush));
        OnPropertyChanged(nameof(CanSelectComPort));
        OnPropertyChanged(nameof(CanSelectBaudRate));
        OnPropertyChanged(nameof(CanSelectMeterDetector));
        RefreshDLevelControls();
        RefreshNoiseControlLevels();
        StartAntennaTuningCommand.NotifyCanExecuteChanged();
        RefreshRfPowerControls();
        OnPropertyChanged(nameof(CanPinMainFrequency));
        OnPropertyChanged(nameof(CanPinSubFrequency));
        UpdatePinnedFrequencySelection(MainPinnedFrequencies, value ? _mainFrequencyHz : null, _mainModeCode);
        UpdatePinnedFrequencySelection(SubPinnedFrequencies, value ? _subFrequencyHz : null, _subModeCode);
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPeakMeterDetectorChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsAverageMeterDetector));
        OnPropertyChanged(nameof(IsPeakMeterDetectorSelected));
    }

    partial void OnIsMeterDetectorUpdatingChanged(bool value) =>
        OnPropertyChanged(nameof(CanSelectMeterDetector));

    partial void OnDLevelChanged(decimal? value) => RefreshDLevelControls();

    partial void OnIsDLevelUpdatingChanged(bool value) => RefreshDLevelControls();

    partial void OnNoiseBlankerLevelChanged(int? value) => RefreshNoiseControlLevels();

    partial void OnIsNoiseBlankerUpdatingChanged(bool value) => RefreshNoiseControlLevels();

    partial void OnDigitalNoiseReductionLevelChanged(int? value) => RefreshNoiseControlLevels();

    partial void OnIsDigitalNoiseReductionUpdatingChanged(bool value) => RefreshNoiseControlLevels();

    private void RefreshNoiseControlLevels()
    {
        OnPropertyChanged(nameof(NoiseBlankerLevelReading));
        OnPropertyChanged(nameof(CanIncreaseNoiseBlankerLevel));
        OnPropertyChanged(nameof(CanDecreaseNoiseBlankerLevel));
        OnPropertyChanged(nameof(DigitalNoiseReductionLevelReading));
        OnPropertyChanged(nameof(CanIncreaseDigitalNoiseReductionLevel));
        OnPropertyChanged(nameof(CanDecreaseDigitalNoiseReductionLevel));
    }

    private void RefreshDLevelControls()
    {
        OnPropertyChanged(nameof(DLevelReading));
        OnPropertyChanged(nameof(CanIncreaseDLevel));
        OnPropertyChanged(nameof(CanDecreaseDLevel));
    }

    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(CanSelectComPort));
        OnPropertyChanged(nameof(CanSelectBaudRate));
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedComPortChanged(ComPortOption? value) =>
        ToggleConnectionCommand.NotifyCanExecuteChanged();

    partial void OnIsTransmittingChanged(bool value)
    {
        OnPropertyChanged(nameof(TransmitStatusText));
        OnPropertyChanged(nameof(TransmitStatusBrush));
        OnPropertyChanged(nameof(MainFrequencyBrush));
        OnPropertyChanged(nameof(SubFrequencyBrush));
        OnPropertyChanged(nameof(CanTuneAntenna));
        StartAntennaTuningCommand.NotifyCanExecuteChanged();
    }

    partial void OnMainVolumeLevelChanged(double value)
    {
        if (!_isApplyingRadioAudioGain)
        {
            IsMainVolumeMuted = value <= 0;
            if (value > 0)
            {
                _mainVolumeBeforeMute = value;
            }
        }

    }

    partial void OnSubVolumeLevelChanged(double value)
    {
        if (!_isApplyingRadioAudioGain)
        {
            IsSubVolumeMuted = value <= 0;
            if (value > 0)
            {
                _subVolumeBeforeMute = value;
            }
        }

    }

    partial void OnTransmitVfoChanged(int? value)
    {
        OnPropertyChanged(nameof(MainVfoStateText));
        OnPropertyChanged(nameof(SubVfoStateText));
        OnPropertyChanged(nameof(MainVfoStateBrush));
        OnPropertyChanged(nameof(SubVfoStateBrush));
        OnPropertyChanged(nameof(MainFrequencyBrush));
        OnPropertyChanged(nameof(SubFrequencyBrush));
        OnPropertyChanged(nameof(MainFrequencyFontSize));
        OnPropertyChanged(nameof(SubFrequencyFontSize));
        UpdateRfPowerContext();
    }

    partial void OnAlcPercentChanged(int value) =>
        OnPropertyChanged(nameof(AlcIndicatorBrush));

    partial void OnSMeterRawChanged(int value)
    {
        OnPropertyChanged(nameof(SMeterSRangeValue));
        OnPropertyChanged(nameof(SMeterOverRangeValue));
    }

    public void Dispose()
    {
        CancelRfPowerChange();
        _meterDetectorReadCompletion?.TrySetCanceled();
        _meterDetectorReadCompletion = null;
        _transmitMeterPollingCancellation?.Cancel();
        _cat.AutoInformationReceived -= OnAutoInformationReceived;
        _cat.AutoInformationError -= OnAutoInformationError;
        try
        {
            _cat.Dispose();
        }
        finally
        {
            _cat.CatFrameLogged -= OnCatFrameLogged;
        }
    }
}
