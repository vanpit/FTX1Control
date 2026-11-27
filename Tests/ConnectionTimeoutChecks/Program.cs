using System;
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

var viewModel = new MainViewModel();
var logPath = (string?)typeof(MainViewModel).GetField("_catLogFilePath", PrivateInstance)!.GetValue(viewModel);
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
    if (logPath is not null && File.Exists(logPath)) File.Delete(logPath);
}

Console.WriteLine("Connection timeout UI checks passed.");
