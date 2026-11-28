using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using FTX1Control.Models;
using FTX1Control.Services;
using FTX1Control.ViewModels;

const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static MainViewModel CreateModel(string settingsPath)
{
    var storeType = typeof(MainViewModel).Assembly.GetType("FTX1Control.Services.CatLogSettingsStore")!;
    var store = Activator.CreateInstance(storeType, settingsPath)!;
    return (MainViewModel)Activator.CreateInstance(
        typeof(MainViewModel), PrivateInstance, null, new[] { store }, null)!;
}

var isolatedSettingsPath = Path.Combine(Path.GetTempPath(), "FTX1Control-tests", Guid.NewGuid().ToString("N"), "settings.txt");
var viewModel = CreateModel(isolatedSettingsPath);
var wasEnglish = LanguageService.IsEnglish;
try
{
    LanguageService.SetLanguage(0, savePreference: false);
    var pinnedFrequency = new PinnedFrequencyViewModel(7_100_000, '2', "USB", () => { }, _ => { }, "K1ABC");
    Assert(!pinnedFrequency.IsCallSignDisplayVisible, "An unselected pinned callsign should be hidden.");
    pinnedFrequency.IsSelected = true;
    Assert(pinnedFrequency.IsCallSignDisplayVisible, "A selected pinned callsign should be shown.");
    pinnedFrequency.CallSign = " ";
    Assert(!pinnedFrequency.IsCallSignDisplayVisible, "A blank pinned callsign should be hidden.");
    pinnedFrequency.CallSign = "K1ABC";
    Assert(pinnedFrequency.IsCallSignDisplayVisible, "Editing the selected pinned callsign did not show it.");
    pinnedFrequency.IsSelected = false;
    Assert(!pinnedFrequency.IsCallSignDisplayVisible, "Deselecting a pinned frequency did not hide its callsign.");

    var volumeViewModel = CreateModel(Path.Combine(
        Path.GetTempPath(), "FTX1Control-volume-icon-tests", Guid.NewGuid().ToString("N"), "settings.txt"));
    try
    {
        Assert(volumeViewModel.IsMainVolumeMutedIconVisible && !volumeViewModel.IsMainVolumeMaxIconVisible,
            "Zero MAIN volume did not select the muted icon.");
        volumeViewModel.MainVolumeLevel = 1;
        Assert(volumeViewModel.IsMainVolumeMaxIconVisible && !volumeViewModel.IsMainVolumeMutedIconVisible,
            "Non-zero MAIN volume did not select the maximum icon.");
        volumeViewModel.MainVolumeLevel = 255;
        Assert(volumeViewModel.IsMainVolumeMaxIconVisible && !volumeViewModel.IsMainVolumeMutedIconVisible,
            "Maximum MAIN volume did not select the maximum icon.");
        volumeViewModel.SubVolumeLevel = 255;
        Assert(volumeViewModel.IsSubVolumeMaxIconVisible && !volumeViewModel.IsSubVolumeMutedIconVisible,
            "Non-zero SUB volume did not select the maximum icon.");
        volumeViewModel.SubVolumeLevel = 0;
        Assert(volumeViewModel.IsSubVolumeMutedIconVisible && !volumeViewModel.IsSubVolumeMaxIconVisible,
            "Zero SUB volume did not select the muted icon.");
    }
    finally
    {
        volumeViewModel.Dispose();
    }

    var connectionIndicatorChanges = 0;
    viewModel.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(MainViewModel.ConnectionIndicatorBrush)) connectionIndicatorChanges++;
    };
    Assert(((ISolidColorBrush)viewModel.ConnectionIndicatorBrush).Color == Color.FromRgb(239, 83, 80),
        "The disconnected indicator is not red.");
    if (viewModel.StatusMessage == "Nie wykryto portu CAT Silicon Labs typu Enhanced ani Standard.")
    {
        Assert(((ISolidColorBrush)viewModel.StatusMessageBrush).Color == Color.FromRgb(231, 76, 60),
            "The missing port message is not red.");
    }

    var statusError = typeof(MainViewModel).GetMethod("SetStatusError", PrivateInstance)!;
    var brushChanges = 0;
    viewModel.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(MainViewModel.StatusMessageBrush)) brushChanges++;
    };
    viewModel.StatusMessage = "Test status";
    statusError.Invoke(viewModel, new object[] { "Test status" });
    Assert(((ISolidColorBrush)viewModel.StatusMessageBrush).Color == Color.FromRgb(231, 76, 60),
        "An error with unchanged text is not red.");
    Assert(brushChanges >= 1, "Changing status severity did not notify the UI.");
    statusError.Invoke(viewModel, new object[] { "Nie wykryto portu CAT Silicon Labs typu Enhanced ani Standard." });
    LanguageService.SetLanguage(1, savePreference: false);
    Assert(viewModel.StatusMessage.StartsWith("No Silicon Labs", StringComparison.Ordinal)
        && ((ISolidColorBrush)viewModel.StatusMessageBrush).Color == Color.FromRgb(231, 76, 60),
        "The translated missing port message is not red.");
    var identificationError = new UnsupportedRadioException("ID1234").Message;
    Assert(LanguageService.TranslateStatus(identificationError) ==
        "Incorrect radio identification: ID1234; Expected ID0840.",
        "The identification error was not translated.");
    viewModel.StatusMessage = "Wybierz wykryty port COM Silicon Labs.";
    Assert(((ISolidColorBrush)viewModel.StatusMessageBrush).Color == Color.FromRgb(195, 206, 202),
        "An informational message did not restore the normal color.");
    LanguageService.SetLanguage(0, savePreference: false);
    var applyAutoInformation = typeof(MainViewModel).GetMethod("ApplyAutoInformation", PrivateInstance)!;
    applyAutoInformation.Invoke(viewModel, new object[] { "RL003" });
    Assert(viewModel.DigitalNoiseReductionLevel == 3, "The RL0 DNR frame was not applied.");
    viewModel.StatusMessage = "Test status";
    applyAutoInformation.Invoke(viewModel, new object[] { "RL103" });
    applyAutoInformation.Invoke(viewModel, new object[] { "RL100" });
    Assert(viewModel.DigitalNoiseReductionLevel == 3 && viewModel.StatusMessage == "Test status"
        && ((ISolidColorBrush)viewModel.StatusMessageBrush).Color == Color.FromRgb(195, 206, 202),
        "An RL1 Auto Information frame was treated as a malformed RL0 DNR frame.");
    typeof(MainViewModel).GetField("_catOperationsCancellation", PrivateInstance)!
        .SetValue(viewModel, new CancellationTokenSource());
    viewModel.IsConnected = true;
    Assert(((ISolidColorBrush)viewModel.ConnectionIndicatorBrush).Color == Color.FromRgb(182, 214, 108)
        && connectionIndicatorChanges == 1, "The connected indicator did not turn green.");
    var run = typeof(MainViewModel).GetMethods(PrivateInstance)
        .Single(method => method.Name == "RunCatAsync" && !method.IsGenericMethod);

    var rejected = (Task)run.Invoke(viewModel, new object[]
    {
        (Action)(() => throw new InvalidDataException("Rejected")),
        CancellationToken.None
    })!;
    try { await rejected; throw new Exception("Invalid response did not fail."); }
    catch (InvalidDataException) { }
    Assert(viewModel.IsConnected, "A non-timeout error disconnected the radio.");

    var timedOut = (Task)run.Invoke(viewModel, new object[]
    {
        (Action)(() => throw new TimeoutException("Test timeout")),
        CancellationToken.None
    })!;
    try { await timedOut; throw new Exception("Timeout was not propagated."); }
    catch (TimeoutException) { }

    var disconnect = (Task?)typeof(MainViewModel).GetField("_disconnectTask", PrivateInstance)!.GetValue(viewModel);
    Assert(disconnect is not null, "Timeout did not start disconnecting.");
    await disconnect!.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(!viewModel.IsConnected && !viewModel.IsConnecting, "Timeout did not finish disconnecting.");
    Assert(((ISolidColorBrush)viewModel.ConnectionIndicatorBrush).Color == Color.FromRgb(239, 83, 80)
        && connectionIndicatorChanges == 2, "The disconnected indicator did not turn red.");
    Assert(viewModel.ConnectionStatus == "BŁĄD POŁĄCZENIA", "Connection error status is missing.");
    Assert(viewModel.StatusMessage.Contains("Test timeout", StringComparison.Ordinal), "Timeout details are missing.");
    Assert(((ISolidColorBrush)viewModel.StatusMessageBrush).Color == Color.FromRgb(231, 76, 60), "Timeout status is not red.");

    viewModel.StatusMessage = "A later status";
    Assert(viewModel.StatusMessage.Contains("Test timeout", StringComparison.Ordinal), "A later status hid the timeout.");
    LanguageService.SetLanguage(1, savePreference: false);
    Assert(viewModel.StatusMessage.StartsWith("Lost connection to the radio:", StringComparison.Ordinal), "Timeout was not localized.");

    typeof(MainViewModel).GetMethod("SetCatTimeoutMessage", PrivateInstance)!.Invoke(viewModel, new object?[] { null });
    Assert(((ISolidColorBrush)viewModel.StatusMessageBrush).Color == Color.FromRgb(195, 206, 202), "Normal status color was not restored.");

    LanguageService.SetLanguage(0, savePreference: false);
    typeof(MainViewModel).GetField("_disconnectTask", PrivateInstance)!.SetValue(viewModel, null);
    typeof(MainViewModel).GetField("_catOperationsCancellation", PrivateInstance)!
        .SetValue(viewModel, new CancellationTokenSource());
    typeof(MainViewModel).GetField("_transmitMeterPollingCancellation", PrivateInstance)!
        .SetValue(viewModel, new CancellationTokenSource());
    viewModel.IsConnected = true;
    var startPolling = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task PollWithTimeout()
    {
        await startPolling.Task;
        var operation = (Task)run.Invoke(viewModel, new object[]
        {
            (Action)(() => throw new TimeoutException("Polling timeout")),
            CancellationToken.None
        })!;
        try { await operation; }
        catch (TimeoutException) { }
    }

    var polling = PollWithTimeout();
    typeof(MainViewModel).GetField("_transmitMeterPollingTask", PrivateInstance)!.SetValue(viewModel, polling);
    startPolling.SetResult(true);
    await polling.WaitAsync(TimeSpan.FromSeconds(3));
    var pollingDisconnect = (Task)typeof(MainViewModel).GetField("_disconnectTask", PrivateInstance)!.GetValue(viewModel)!;
    await pollingDisconnect.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(!viewModel.IsConnected && viewModel.StatusMessage.Contains("Polling timeout", StringComparison.Ordinal),
        "A polling timeout did not disconnect cleanly.");

    typeof(MainViewModel).GetMethod("SetCatTimeoutMessage", PrivateInstance)!.Invoke(viewModel, new object?[] { null });
    typeof(MainViewModel).GetField("_disconnectTask", PrivateInstance)!.SetValue(viewModel, null);
    typeof(MainViewModel).GetField("_catOperationsCancellation", PrivateInstance)!
        .SetValue(viewModel, new CancellationTokenSource());
    viewModel.IsConnected = true;
    using var started = new ManualResetEventSlim();
    using var finish = new ManualResetEventSlim();
    var pending = (Task)run.Invoke(viewModel, new object[]
    {
        (Action)(() =>
        {
            started.Set();
            finish.Wait();
            throw new TimeoutException("Disconnect timeout");
        }),
        CancellationToken.None
    })!;
    Assert(started.Wait(TimeSpan.FromSeconds(3)), "The request did not start.");
    var manualDisconnect = (Task)typeof(MainViewModel).GetMethod("ToggleConnectionAsync", PrivateInstance)!
        .Invoke(viewModel, null)!;
    Assert(viewModel.IsConnecting && viewModel.ConnectButtonText == "Rozłączanie...",
        "The disconnecting state was not displayed.");
    finish.Set();
    try { await pending; }
    catch (TimeoutException) { }
    await manualDisconnect.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(!viewModel.IsConnected && viewModel.StatusMessage.Contains("Disconnect timeout", StringComparison.Ordinal),
        "A timeout during manual disconnect was lost.");
}
finally
{
    viewModel.Dispose();
    LanguageService.SetLanguage(wasEnglish ? 1 : 0, savePreference: false);
}

Console.WriteLine("Connection timeout UI checks passed.");

var warningTestRoot = Path.Combine(Path.GetTempPath(), "FTX1Control-warning-check-" + Guid.NewGuid().ToString("N"));
var warningPath = Path.Combine(warningTestRoot, "warning.txt");
var warningModels = new List<MainViewModel>();
MainViewModel CreateWarningModel(string? settingsPath = null)
{
    var assembly = typeof(MainViewModel).Assembly;
    var logStore = Activator.CreateInstance(assembly.GetType("FTX1Control.Services.CatLogSettingsStore")!,
        Path.Combine(warningTestRoot, "log.txt"))!;
    var warningStore = Activator.CreateInstance(assembly.GetType("FTX1Control.Services.HfPowerWarningSettingsStore")!,
        settingsPath ?? warningPath)!;
    var model = (MainViewModel)Activator.CreateInstance(
        typeof(MainViewModel), PrivateInstance, null, new[] { logStore, warningStore }, null)!;
    warningModels.Add(model);
    return model;
}

try
{
    {
        var warningModel = CreateWarningModel();
        Assert(warningModel.HfPowerWarningLimit == 25, "The default HF warning limit is incorrect.");
        Assert(warningModel.RfPowerBarValue == 0, "The power bar should be empty without a radio reading.");
        var barNotifications = 0;
        warningModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.RfPowerBarValue)) barNotifications++;
        };
        var updateFrequency = typeof(MainViewModel).GetMethod("UpdateFrequency", PrivateInstance)!;
        var updateMode = typeof(MainViewModel).GetMethod("UpdateMode", PrivateInstance)!;
        var applyPower = typeof(MainViewModel).GetMethod("ApplyRfPowerFromRadio", PrivateInstance)!;
        updateFrequency.Invoke(warningModel, new object[] { 0, 30_000_000 });
        updateMode.Invoke(warningModel, new object[] { 0, 'C', "DATA-U" });
        warningModel.TransmitVfo = 0;
        var warningLimits = (Dictionary<int, int>)typeof(MainViewModel)
            .GetField("_rfPowerLimits", PrivateInstance)!.GetValue(warningModel)!;
        warningLimits[5] = 100;
        Assert(warningModel.MaximumRfPower == 100, "30 MHz was not recognized as the HF power context.");
        applyPower.Invoke(warningModel, new object[] { 25 });
        Assert(warningModel.RfPowerBarValue == 0, "The power bar should be empty while disconnected.");
        warningModel.IsConnected = true;
        Assert(warningModel.RfPowerBarValue == 25 && barNotifications > 0,
            "The power bar did not update from the radio reading.");
        applyPower.Invoke(warningModel, new object[] { 100 });
        Assert(warningModel.RfPowerBarValue == 100, "The power bar did not reach its 100 W maximum.");
        warningLimits[5] = 30;
        var refreshPower = typeof(MainViewModel).GetMethod("RefreshRfPowerControls", PrivateInstance)!;
        refreshPower.Invoke(warningModel, null);
        applyPower.Invoke(warningModel, new object[] { 15 });
        Assert(warningModel.MaximumRfPower == 30 && warningModel.RfPowerBarValue == 15,
            "The power bar did not use the band limit for half scale.");
        applyPower.Invoke(warningModel, new object[] { 30 });
        Assert(warningModel.RfPowerBarValue == warningModel.MaximumRfPower,
            "The power bar was not full at the band limit.");
        warningLimits.Clear();
        refreshPower.Invoke(warningModel, null);
        Assert(warningModel.RfPowerBarValue == 0, "The power bar should be empty when the band limit is unknown.");
        applyPower.Invoke(warningModel, new object[] { 25 });
        Assert(!warningModel.IsRfPowerWarning, "Power equal to the limit triggered a warning.");
        Assert(warningModel.RfPowerOverLimitReading == string.Empty, "Equal power reported an excess.");
        applyPower.Invoke(warningModel, new object[] { 26 });
        Assert(warningModel.IsRfPowerWarning && warningModel.RfPowerOverLimitReading == "+1 W",
            "DATA-U at 30 MHz did not report the power above the limit.");
        var warningNotifications = 0;
        var excessNotifications = 0;
        warningModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRfPowerWarning)) warningNotifications++;
            if (e.PropertyName == nameof(MainViewModel.RfPowerOverLimitReading)) excessNotifications++;
        };
        applyPower.Invoke(warningModel, new object[] { 30 });
        Assert(warningModel.RfPowerOverLimitReading == "+5 W" && excessNotifications > 0,
            "A new radio reading did not update the power excess.");
        applyPower.Invoke(warningModel, new object[] { 26 });
        excessNotifications = 0;
        updateMode.Invoke(warningModel, new object[] { 0, '2', "USB" });
        Assert(!warningModel.IsRfPowerWarning && warningNotifications > 0,
            "Changing mode within the same RF power context did not clear the warning.");
        updateMode.Invoke(warningModel, new object[] { 0, 'C', "DATA-U" });
        updateFrequency.Invoke(warningModel, new object[] { 0, 30_000_001 });
        Assert(!warningModel.IsRfPowerWarning, "A frequency above 30 MHz triggered the warning.");
        updateFrequency.Invoke(warningModel, new object[] { 1, 7_100_000 });
        updateMode.Invoke(warningModel, new object[] { 1, 'C', "DATA-U" });
        warningModel.TransmitVfo = 1;
        Assert(warningModel.IsRfPowerWarning, "The SUB TRX VFO did not trigger the warning.");
        warningModel.HfPowerWarningLimit = 26;
        Assert(!warningModel.IsRfPowerWarning && warningModel.RfPowerOverLimitReading == string.Empty
            && excessNotifications > 0 && File.ReadAllText(warningPath) == "26",
            "Changing the limit did not update and save the warning.");
        warningModel.HfPowerWarningLimit = 5;
        Assert(warningModel.IsRfPowerWarning && warningModel.RfPowerOverLimitReading == "+21 W",
            "The minimum limit did not update the power excess.");
        warningModel.HfPowerWarningLimit = 100;
        Assert(!warningModel.IsRfPowerWarning, "The maximum limit did not clear the warning.");
        warningModel.HfPowerWarningLimit = 5;
        warningModel.IsConnected = false;
        Assert(!warningModel.IsRfPowerWarning, "The warning remained active after disconnecting.");
        warningModel.HfPowerWarningLimit = 100;
        typeof(MainViewModel).GetMethod("ClearRadioReadings", PrivateInstance)!.Invoke(warningModel, null);
        Assert(warningModel.RfPowerBarValue == 0, "The power bar was not cleared with the radio readings.");
    }

    {
        var meterModel = CreateWarningModel();
        var updateFrequency = typeof(MainViewModel).GetMethod("UpdateFrequency", PrivateInstance)!;
        var updateMode = typeof(MainViewModel).GetMethod("UpdateMode", PrivateInstance)!;
        var updatePower = typeof(MainViewModel).GetMethod("UpdatePowerReading", PrivateInstance)!;
        var updateSwr = typeof(MainViewModel).GetMethod("UpdateSwrReading", PrivateInstance)!;
        var updateAlc = typeof(MainViewModel).GetMethod("UpdateAlcReading", PrivateInstance)!;
        Assert(meterModel.PowerOutputMeterValue is null && meterModel.SwrMeterValue is null
            && meterModel.AlcMeterValue is null, "Meters should not show markers before readings arrive.");
        updateFrequency.Invoke(meterModel, new object[] { 0, 14_000_000 });
        updateMode.Invoke(meterModel, new object[] { 0, 'C', "DATA-U" });
        meterModel.TransmitVfo = 0;
        var limits = (Dictionary<int, int>)typeof(MainViewModel)
            .GetField("_rfPowerLimits", PrivateInstance)!.GetValue(meterModel)!;
        limits[5] = 30;
        typeof(MainViewModel).GetMethod("ApplyRfPowerFromRadio", PrivateInstance)!.Invoke(meterModel, new object[] { 20 });
        meterModel.IsConnected = true;
        Assert(meterModel.PowerOutputMeterMaximum == 30 && meterModel.PowerOutputMeterMaximumReading == "30 W",
            "The PO scale did not use the available band power.");
        updatePower.Invoke(meterModel, new object[] { 97 });
        Assert(meterModel.PowerOutputMeterValue == 20 && meterModel.PowerOutputReading == "20 W",
            "The PO marker did not follow the calibrated power reading.");
        updateSwr.Invoke(meterModel, new object[] { 65 });
        Assert(meterModel.SwrMeterValue == 1.5
            && ((ISolidColorBrush)meterModel.SwrIndicatorBrush).Color == Color.FromRgb(233, 239, 237),
            "SWR 1.5 should remain below the orange threshold.");
        updateSwr.Invoke(meterModel, new object[] { 66 });
        Assert(meterModel.SwrMeterValue > 1.5
            && ((ISolidColorBrush)meterModel.SwrIndicatorBrush).Color == Color.FromRgb(213, 154, 66),
            "SWR above 1.5 should be orange.");
        updateSwr.Invoke(meterModel, new object[] { 113 });
        Assert(meterModel.SwrMeterValue == 2.0
            && ((ISolidColorBrush)meterModel.SwrIndicatorBrush).Color == Color.FromRgb(213, 154, 66),
            "SWR 2.0 should remain orange.");
        updateSwr.Invoke(meterModel, new object[] { 114 });
        Assert(meterModel.SwrMeterValue > 2.0
            && ((ISolidColorBrush)meterModel.SwrIndicatorBrush).Color == Color.FromRgb(229, 57, 53),
            "SWR above 2.0 should be red.");
        updateAlc.Invoke(meterModel, new object[] { 0 });
        Assert(meterModel.AlcMeterValue == 0 && meterModel.AlcReading == "0%",
            "A zero ALC reading was confused with missing data.");
        updateAlc.Invoke(meterModel, new object[] { 128 });
        Assert(meterModel.AlcMeterValue == 50 && meterModel.AlcReading == "50%",
            "The ALC marker did not follow the converted percentage.");
        typeof(MainViewModel).GetMethod("ClearTransmitMeterReadings", PrivateInstance)!.Invoke(meterModel, null);
        Assert(meterModel.PowerOutputMeterValue is null && meterModel.SwrMeterValue is null
            && meterModel.AlcMeterValue is null, "Transmit meter markers did not clear on RX.");
    }

    {
        var bandModel = CreateWarningModel();
        var updateFrequency = typeof(MainViewModel).GetMethod("UpdateFrequency", PrivateInstance)!;
        var bandChanges = 0;
        bandModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TransmitAmateurBand)) bandChanges++;
        };
        updateFrequency.Invoke(bandModel, new object[] { 0, 14_000_000 });
        bandModel.TransmitVfo = 0;
        Assert(bandModel.TransmitAmateurBand == "--", "A disconnected radio displayed an amateur band.");
        bandModel.IsConnected = true;
        Assert(bandModel.TransmitAmateurBand == "20 m", "The TRX amateur band was not displayed.");
        var bands = new (int Lower, int Upper, string Name)[]
        {
            (135_700, 137_800, "2200 m"), (472_000, 479_000, "630 m"),
            (1_810_000, 2_000_000, "160 m"), (3_500_000, 3_800_000, "80 m"),
            (5_351_500, 5_366_500, "60 m"), (7_000_000, 7_200_000, "40 m"),
            (10_100_000, 10_150_000, "30 m"), (14_000_000, 14_350_000, "20 m"),
            (18_068_000, 18_168_000, "17 m"), (21_000_000, 21_450_000, "15 m"),
            (24_890_000, 24_990_000, "12 m"), (28_000_000, 29_700_000, "10 m"),
            (50_000_000, 52_000_000, "6 m"), (70_000_000, 70_500_000, "4 m"),
            (144_000_000, 146_000_000, "2 m"), (430_000_000, 440_000_000, "70 cm")
        };
        foreach (var band in bands)
        {
            updateFrequency.Invoke(bandModel, new object[] { 0, band.Lower });
            Assert(bandModel.TransmitAmateurBand == band.Name, $"Incorrect lower edge for {band.Name}.");
            updateFrequency.Invoke(bandModel, new object[] { 0, band.Upper });
            Assert(bandModel.TransmitAmateurBand == band.Name, $"Incorrect upper edge for {band.Name}.");
            updateFrequency.Invoke(bandModel, new object[] { 0, band.Upper + 1 });
            Assert(bandModel.TransmitAmateurBand == "--", $"Frequency above {band.Name} was treated as amateur band.");
        }
        Assert(bandChanges > 0, "Band changes were not reported to the UI.");
        updateFrequency.Invoke(bandModel, new object[] { 1, 7_100_000 });
        bandModel.TransmitVfo = 1;
        Assert(bandModel.TransmitAmateurBand == "40 m", "The band did not follow the TRX VFO.");
        bandModel.IsConnected = false;
        Assert(bandModel.TransmitAmateurBand == "--", "The band remained visible after disconnecting.");
    }

    {
        var tuneModel = CreateWarningModel();
        var updateFrequency = typeof(MainViewModel).GetMethod("UpdateFrequency", PrivateInstance)!;
        var commandChanges = 0;
        tuneModel.StartAntennaTuningCommand.CanExecuteChanged += (_, _) => commandChanges++;
        tuneModel.IsConnected = true;
        Assert(!tuneModel.CanTuneAntenna && !tuneModel.StartAntennaTuningCommand.CanExecute(null),
            "Antenna tuning was enabled without a known TRX VFO.");
        tuneModel.TransmitVfo = 0;
        updateFrequency.Invoke(tuneModel, new object[] { 0, 30_000_000 });
        Assert(tuneModel.CanTuneAntenna && tuneModel.StartAntennaTuningCommand.CanExecute(null),
            "Antenna tuning was not enabled at the HF boundary.");
        var previousChanges = commandChanges;
        updateFrequency.Invoke(tuneModel, new object[] { 0, 30_000_001 });
        Assert(!tuneModel.CanTuneAntenna && !tuneModel.StartAntennaTuningCommand.CanExecute(null)
            && commandChanges > previousChanges, "Moving above HF did not disable antenna tuning.");
        updateFrequency.Invoke(tuneModel, new object[] { 1, 7_100_000 });
        Assert(!tuneModel.CanTuneAntenna, "The receive VFO enabled antenna tuning.");
        tuneModel.TransmitVfo = 1;
        Assert(tuneModel.CanTuneAntenna && tuneModel.StartAntennaTuningCommand.CanExecute(null),
            "Switching TRX to an HF VFO did not enable antenna tuning.");
        tuneModel.IsTransmitting = true;
        Assert(!tuneModel.CanTuneAntenna && !tuneModel.StartAntennaTuningCommand.CanExecute(null),
            "Antenna tuning was enabled during transmission.");
        tuneModel.IsTransmitting = false;
        Assert(tuneModel.CanTuneAntenna, "Antenna tuning did not return after transmission ended.");
        tuneModel.IsConnected = false;
        Assert(!tuneModel.CanTuneAntenna, "Antenna tuning remained enabled after disconnecting.");
    }

    {
        var amModel = CreateWarningModel();
        var updateFrequency = typeof(MainViewModel).GetMethod("UpdateFrequency", PrivateInstance)!;
        var updateMode = typeof(MainViewModel).GetMethod("UpdateMode", PrivateInstance)!;
        updateFrequency.Invoke(amModel, new object[] { 0, 50_000_000 });
        updateMode.Invoke(amModel, new object[] { 0, '5', "AM" });
        amModel.TransmitVfo = 0;
        var limits = (Dictionary<int, int>)typeof(MainViewModel)
            .GetField("_rfPowerLimits", PrivateInstance)!.GetValue(amModel)!;
        limits[6] = 100;
        typeof(MainViewModel).GetMethod("ApplyRfPowerFromRadio", PrivateInstance)!.Invoke(amModel, new object[] { 25 });
        amModel.IsConnected = true;
        Assert(amModel.RfPowerBarValue == 0, "The power bar should be empty when the AM limit is unknown.");
        limits[10] = 25;
        typeof(MainViewModel).GetMethod("RefreshRfPowerControls", PrivateInstance)!.Invoke(amModel, null);
        Assert(amModel.MaximumRfPower == 25 && amModel.RfPowerBarValue == 25,
            "The power bar did not use the lower AM limit.");
    }

    {
        var restarted = CreateWarningModel();
        Assert(restarted.HfPowerWarningLimit == 100, "The HF warning limit was not restored.");
    }
    File.WriteAllText(warningPath, "101");
    {
        var invalid = CreateWarningModel();
        Assert(invalid.HasHfPowerWarningSettingsError && invalid.HfPowerWarningLimit == 25,
            "An invalid saved warning limit was not reported.");
    }
    {
        var inaccessible = CreateWarningModel(warningTestRoot);
        inaccessible.HfPowerWarningLimit = 30;
        Assert(inaccessible.HasHfPowerWarningSettingsError,
            "Failure to save the warning limit was not reported.");
    }
}
finally
{
    foreach (var model in warningModels) model.Dispose();
    if (Directory.Exists(warningTestRoot))
    {
        Directory.Delete(warningTestRoot, recursive: true);
    }
}

Console.WriteLine("HF power warning checks passed.");

var frequencyModel = CreateModel(isolatedSettingsPath);
try
{
    LanguageService.SetLanguage(0, savePreference: false);
    var updateFrequency = typeof(MainViewModel).GetMethod("UpdateFrequency", PrivateInstance)!;
    typeof(MainViewModel).GetMethod("UpdateMode", PrivateInstance)!
        .Invoke(frequencyModel, new object[] { 0, '2', "USB" });
    frequencyModel.TransmitVfo = 0;
    updateFrequency.Invoke(frequencyModel, new object[] { 0, 14000000 });
    updateFrequency.Invoke(frequencyModel, new object[] { 1, 7100000 });
    var limits = (Dictionary<int, int>)typeof(MainViewModel).GetField("_rfPowerLimits", PrivateInstance)!.GetValue(frequencyModel)!;
    limits[5] = 100;
    limits[6] = 100;
    typeof(MainViewModel).GetField("_catOperationsCancellation", PrivateInstance)!
        .SetValue(frequencyModel, new CancellationTokenSource());
    frequencyModel.IsConnected = true;

    var clock = Stopwatch.StartNew();
    var readErrors = new ConcurrentQueue<(string Message, long Time)>();
    frequencyModel.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(MainViewModel.StatusMessage)
            && frequencyModel.StatusMessage.StartsWith("Nie udało się odczytać", StringComparison.Ordinal))
        {
            readErrors.Enqueue((frequencyModel.StatusMessage, clock.ElapsedMilliseconds));
        }
    };

    updateFrequency.Invoke(frequencyModel, new object[] { 0, 50000000 });
    await Task.Delay(80);
    Assert(readErrors.IsEmpty, "A frequency read started before the debounce delay.");
    var lastChange = clock.ElapsedMilliseconds;
    updateFrequency.Invoke(frequencyModel, new object[] { 1, 7100001 });
    await Task.Delay(150);
    Assert(readErrors.IsEmpty, "A second frequency change did not reset the debounce delay.");

    var deadline = Stopwatch.StartNew();
    while (readErrors.Count < 4 && deadline.Elapsed < TimeSpan.FromSeconds(3))
    {
        await Task.Delay(10);
    }

    var errors = readErrors.ToArray();
    Assert(errors.Length == 4 && errors.All(error => error.Time - lastChange >= 195),
        "Debounced frequency reads did not run once after the final change.");
    Assert(errors.Any(error => error.Message.Contains("D-LEVEL", StringComparison.Ordinal))
        && errors.Any(error => error.Message.Contains("NB", StringComparison.Ordinal))
        && errors.Any(error => error.Message.Contains("DNR", StringComparison.Ordinal))
        && errors.Any(error => error.Message.Contains("mocy RF", StringComparison.Ordinal)),
        "The MAIN and SUB changes did not retain all required reads.");

    while (typeof(MainViewModel).GetField("_frequencyRefreshCancellation", PrivateInstance)!.GetValue(frequencyModel) is not null
        && deadline.Elapsed < TimeSpan.FromSeconds(3))
    {
        await Task.Delay(10);
    }
    updateFrequency.Invoke(frequencyModel, new object[] { 0, 50000001 });
    var errorCount = readErrors.Count;
    var disconnect = (Task)typeof(MainViewModel).GetMethod("BeginDisconnectAsync", PrivateInstance)!.Invoke(frequencyModel, null)!;
    await disconnect.WaitAsync(TimeSpan.FromSeconds(3));
    await Task.Delay(250);
    Assert(readErrors.Count == errorCount, "A pending frequency read ran after disconnect.");
}
finally
{
    frequencyModel.Dispose();
    LanguageService.SetLanguage(wasEnglish ? 1 : 0, savePreference: false);
}

Console.WriteLine("Frequency refresh debounce checks passed.");

var testRoot = Path.Combine(Path.GetTempPath(), "FTX1Control-log-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var logModels = new List<MainViewModel>();
MainViewModel CreateLogModel(string path)
{
    var model = CreateModel(path);
    logModels.Add(model);
    return model;
}
try
{
    var settingsPath = Path.Combine(testRoot, "settings", "cat-log-folder.txt");
    var firstFolder = Path.Combine(testRoot, "first");
    var secondFolder = Path.Combine(testRoot, "second");
    Directory.CreateDirectory(firstFolder);
    Directory.CreateDirectory(secondFolder);
    var logField = typeof(MainViewModel).GetField("_catLogFilePath", PrivateInstance)!;
    var logFrame = typeof(MainViewModel).GetMethod("OnCatFrameLogged", PrivateInstance)!;

    {
        var model = CreateLogModel(settingsPath);
        Assert(!model.HasCatLogFolder && model.IsCatLogFolderUnset && logField.GetValue(model) is null,
            "File logging is not disabled by default.");
        logFrame.Invoke(model, new object[] { "[TX] FA;" });
        Assert(!File.Exists(settingsPath) && Directory.GetFiles(firstFolder).Length == 0,
            "A CAT frame created a file while file logging was disabled.");

        model.SetCatLogFolder(firstFolder);
        var firstLog = (string?)logField.GetValue(model);
        Assert(model.CatLogFolder == firstFolder && model.HasCatLogFolder && !model.HasCatLogFileError
            && firstLog is not null && File.Exists(firstLog), "Selecting a folder did not start file logging.");
        logFrame.Invoke(model, new object[] { "[TX] FA;" });
        Assert(File.ReadAllText(firstLog!).Contains("[TX] FA;", StringComparison.Ordinal),
            "The CAT frame was not written to the selected folder.");

        model.SetCatLogFolder("relative-folder");
        Assert(model.HasCatLogFileError && model.CatLogFolder == firstFolder
            && Equals(logField.GetValue(model), firstLog) && File.ReadAllText(settingsPath) == firstFolder,
            "An invalid folder changed the active or saved logging destination.");
        logFrame.Invoke(model, new object[] { "[TX] MD;" });
        Assert(File.ReadAllText(firstLog!).Contains("[TX] MD;", StringComparison.Ordinal),
            "A rejected folder stopped the previously active log.");

        var settingsDirectory = Path.GetDirectoryName(settingsPath)!;
        var savedSettingsDirectory = Path.Combine(testRoot, "saved-settings");
        Directory.Move(settingsDirectory, savedSettingsDirectory);
        File.WriteAllText(settingsDirectory, "not a directory");
        try
        {
            model.SetCatLogFolder(secondFolder);
            Assert(model.HasCatLogFileError && model.CatLogFolder == firstFolder
                && Equals(logField.GetValue(model), firstLog),
                "A settings write failure changed the active logging destination.");
        }
        finally
        {
            File.Delete(settingsDirectory);
            Directory.Move(savedSettingsDirectory, settingsDirectory);
        }
        Assert(File.ReadAllText(settingsPath) == firstFolder,
            "A settings write failure changed the saved logging destination.");

        model.SetCatLogFolder(secondFolder);
        var secondLog = (string?)logField.GetValue(model);
        logFrame.Invoke(model, new object[] { "[TX] FB;" });
        Assert(secondLog is not null && File.ReadAllText(secondLog).Contains("[TX] FB;", StringComparison.Ordinal)
            && !File.ReadAllText(firstLog!).Contains("[TX] FB;", StringComparison.Ordinal) && !model.HasCatLogFileError,
            "Changing folders did not redirect subsequent CAT frames.");
    }

    {
        var model = CreateLogModel(settingsPath);
        var restartedLog = (string?)logField.GetValue(model);
        Assert(model.CatLogFolder == secondFolder && restartedLog is not null && File.Exists(restartedLog),
            "The selected folder was not restored on restart.");
        model.SetCatLogFolder(null);
        logFrame.Invoke(model, new object[] { "[TX] PC;" });
        Assert(!model.HasCatLogFolder && logField.GetValue(model) is null
            && !File.ReadAllText(restartedLog!).Contains("[TX] PC;", StringComparison.Ordinal),
            "Disabling file logging did not stop subsequent writes.");
    }

    {
        var model = CreateLogModel(settingsPath);
        Assert(!model.HasCatLogFolder && logField.GetValue(model) is null,
            "The disabled setting was not restored on restart.");
        var missingFolder = Path.Combine(testRoot, "missing");
        model.SetCatLogFolder(missingFolder);
        Assert(model.CatLogFolder == missingFolder && model.HasCatLogFileError && logField.GetValue(model) is null,
            "A missing selected folder did not report a log creation error.");
    }

    {
        var model = CreateLogModel(settingsPath);
        Assert(model.HasCatLogFileError && logField.GetValue(model) is null,
            "An unavailable saved folder did not report an error on restart.");
        model.SetCatLogFolder(null);
        Assert(!model.HasCatLogFileError, "Disabling file logging did not clear the error.");
    }

    var invalidParent = Path.Combine(testRoot, "not-a-directory");
    File.WriteAllText(invalidParent, "test");
    {
        var model = CreateLogModel(Path.Combine(invalidParent, "settings.txt"));
        model.SetCatLogFolder(firstFolder);
        Assert(!model.HasCatLogFolder && model.HasCatLogFileError && logField.GetValue(model) is null,
            "Failure to save the folder setting was not reported.");
    }
}
finally
{
    foreach (var model in logModels) model.Dispose();
    Directory.Delete(testRoot, recursive: true);
}

Console.WriteLine("CAT log folder checks passed.");
