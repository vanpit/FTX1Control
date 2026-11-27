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
    viewModel.StatusMessage = "Wybierz wykryty port COM Silicon Labs.";
    Assert(((ISolidColorBrush)viewModel.StatusMessageBrush).Color == Color.FromRgb(195, 206, 202),
        "An informational message did not restore the normal color.");
    LanguageService.SetLanguage(0, savePreference: false);
    typeof(MainViewModel).GetField("_catOperationsCancellation", PrivateInstance)!
        .SetValue(viewModel, new CancellationTokenSource());
    viewModel.IsConnected = true;
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
