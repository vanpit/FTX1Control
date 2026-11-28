using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FTX1Control.Models;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task WaitFor(Func<bool> condition)
{
    for (var i = 0; i < 200; i++)
    {
        if (condition()) return;
        await Task.Delay(5);
    }

    throw new TimeoutException("Test condition was not reached.");
}

static (CatSerialConnection Connection, SerialPort Port, List<string> Received) Create()
{
    var connection = new CatSerialConnection();
    var received = new List<string>();
    connection.AutoInformationReceived += value => { lock (received) received.Add(value); };
    connection.Connect("COM1", 4800);
    connection.EnableAutoInformation();
    return (connection, SerialPort.Last, received);
}

{
    var (connection, port, _) = Create();
    using (connection)
    {
        await Task.Run(() => connection.SetFrequencyAndMode(0, 14_074_000, 'C'));
        Assert(port.Writes.Contains("FA014074000;") && port.Writes.Contains("MD0C;"),
            "Selecting a pinned frequency did not restore its frequency and emission.");
    }
}

{
    using var connection = new CatSerialConnection();
    var frames = new List<string>();
    connection.CatFrameLogged += frames.Add;
    connection.Connect("COM1", 4800);
    var port = SerialPort.Last;
    var identification = Task.Run(connection.VerifyRadioIdentification);
    await WaitFor(() => port.Writes.Contains("ID;"));
    await port.Inject("ID08");
    Assert(!identification.IsCompleted, "An incomplete identification was accepted.");
    await port.Inject("40;");
    await identification;
    Assert(frames.Contains("[TX] ID;") && frames.Contains("[RX] ID0840;"),
        "The identification exchange was not logged.");
}

foreach (var response in new[] { "ID1234", "ID084", "ID08400" })
{
    using var connection = new CatSerialConnection();
    connection.Connect("COM1", 4800);
    var port = SerialPort.Last;
    var identification = Task.Run(connection.VerifyRadioIdentification);
    await WaitFor(() => port.Writes.Contains("ID;"));
    await port.Inject(response + ";");
    try
    {
        await identification;
        throw new InvalidOperationException("An incorrect identification was accepted.");
    }
    catch (UnsupportedRadioException exception)
    {
        Assert(exception.Message.Contains(response, StringComparison.Ordinal)
            && exception.Message.Contains("ID0840", StringComparison.Ordinal),
            "The identification error did not include both actual and expected IDs.");
    }
}

{
    var (connection, port, received) = Create();
    using (connection)
    {
        var first = Task.Run(connection.RequestDLevel);
        await WaitFor(() => port.Writes.Contains("SS04;"));
        var second = Task.Run(connection.RequestNoiseBlankerLevel);
        var write = Task.Run(() => connection.SetAudioGain(0, 100));
        await Task.Delay(50);
        Assert(!port.Writes.Contains("NL0;") && !port.Writes.Contains("AG0100;"),
            "Another command was sent before the D-LEVEL response.");
        _ = port.Inject("FA000014074;SS04+01.0;");
        await first;
        await WaitFor(() => port.Writes.Contains("NL0;"));
        var third = Task.Run(connection.RequestDigitalNoiseReductionLevel);
        await Task.Delay(50);
        Assert(!port.Writes.Contains("RL0;"), "DNR was queried before the NB response.");
        _ = port.Inject("NL0005;");
        await second;
        await WaitFor(() => port.Writes.Contains("RL0;"));
        _ = port.Inject("RL005;");
        await Task.WhenAll(third, write);
        lock (received)
        {
            Assert(received.Contains("FA000014074") && received.Contains("SS04+01.0")
                && received.Contains("NL0005") && received.Contains("RL005"),
                "A query or unsolicited response was not dispatched.");
        }
    }
}

foreach (var isPeak in new[] { false, true })
{
    var (connection, port, received) = Create();
    using (connection)
    {
        var clock = new Stopwatch();
        long queryDelay = -1;
        var setCommand = $"EX030513{(isPeak ? '1' : '0')};";
        connection.CatFrameLogged += frame =>
        {
            if (frame == $"[TX] {setCommand}") clock.Start();
            if (frame == "[TX] EX030513;") queryDelay = clock.ElapsedMilliseconds;
        };
        var change = Task.Run(() => connection.SetAndRequestMeterDetector(isPeak));
        await WaitFor(() => port.Writes.Contains(setCommand));
        var otherCommand = Task.Run(() => connection.SetAudioGain(0, 42));
        await WaitFor(() => port.Writes.Contains("EX030513;"));
        Assert(queryDelay >= 200, "The detector was queried before the radio had time to apply the setting.");
        Assert(!port.Writes.Contains("AG0042;"), "Another command interrupted the detector change.");
        _ = port.Inject(setCommand);
        await Task.WhenAll(change, otherCommand);
        lock (received)
        {
            Assert(received.Contains(setCommand.TrimEnd(';')), "The detector readback was not dispatched.");
        }
    }
}

{
    var (connection, port, _) = Create();
    using (connection)
    using (var cancellation = new CancellationTokenSource())
    {
        var meter = Task.Run(() => connection.RequestTransmitMeterReading(4, cancellation.Token));
        await WaitFor(() => port.Writes.Contains("RM4;"));
        cancellation.Cancel();
        var next = Task.Run(() => connection.SetAudioGain(0, 42));
        await Task.Delay(50);
        Assert(!port.Writes.Contains("AG0042;"), "Cancellation released an unanswered request.");
        _ = port.Inject("RM4001000;");
        await Task.WhenAll(meter, next);
        Assert(port.Writes.Contains("AG0042;"), "A command did not continue after the meter response.");
    }
}

{
    var (connection, port, received) = Create();
    using (connection)
    {
        var meter = Task.Run(() => connection.RequestTransmitMeterReading(4, CancellationToken.None));
        await WaitFor(() => port.Writes.Contains("RM4;"));
        var function = Task.Run(connection.ReadFuncKnobFunction);
        _ = port.Inject("RM5002000;");
        await Task.Delay(50);
        Assert(!port.Writes.Contains("SF0;") && !meter.IsCompleted, "An unrelated meter response released the query.");
        _ = port.Inject("RM4001000;");
        await meter;
        await WaitFor(() => port.Writes.Contains("SF0;"));
        _ = port.Inject("SF01;");
        Assert(await function == 1, "The FUNC query did not receive its response.");
        lock (received)
        {
            Assert(received.Contains("RM4001000") && !received.Contains("RM5002000"), "Unexpected meter dispatch.");
        }
    }
}

{
    var (connection, port, received) = Create();
    using (connection)
    {
        var detector = Task.Run(connection.RequestMeterDetector);
        await WaitFor(() => port.Writes.Contains("EX030513;"));
        var power = Task.Run(connection.RequestRfPower);
        await Task.Delay(50);
        Assert(!port.Writes.Contains("PC;"), "Power was queried before the detector response.");
        _ = port.Inject("EX0305131;");
        await detector;
        await WaitFor(() => port.Writes.Contains("PC;"));
        _ = port.Inject("PC2005;");
        await power;
        lock (received)
        {
            Assert(received.Contains("EX0305131") && received.Contains("PC2005"), "Readback responses were not dispatched.");
        }
    }
}

{
    var (connection, port, _) = Create();
    using (connection)
    {
        var request = Task.Run(connection.RequestRfPower);
        await WaitFor(() => port.Writes.Contains("PC;"));
        try { await request; throw new InvalidOperationException("Missing response did not time out."); }
        catch (TimeoutException) { }
        try { connection.SetAudioGain(0, 10); throw new Exception("A command was sent after a timeout."); }
        catch (InvalidOperationException) { }
        connection.Disconnect();
        Assert(!port.IsOpen, "The serial port remained open after disconnect.");
        Assert(!port.Writes.Contains("AG0010;") && !port.Writes.Contains("AI0;"),
            "A command was sent after an unanswered request.");
        connection.Connect("COM1", 4800);
        connection.SetAudioGain(0, 10);
        Assert(SerialPort.Last.Writes.Contains("AG0010;"), "Reconnection did not clear the fault.");
    }
}

{
    var (connection, port, received) = Create();
    using (connection)
    {
        await port.Inject("NL0");
        var query = Task.Run(connection.ReadFuncKnobFunction);
        await WaitFor(() => port.Writes.Contains("SF0;"));
        var notification = port.Inject("005;SF01;");
        Assert(await query == 1, "The FUNC query returned the wrong value.");
        await notification;
        lock (received) Assert(received.Contains("NL0005"), "A fragmented unsolicited response was lost.");
    }
}

{
    var (connection, port, received) = Create();
    using (connection)
    {
        await port.Inject("TX2;TX0;BI1;");
        await WaitFor(() => received.Count >= 3);
        lock (received)
        {
            Assert(received.Contains("TX2") && received.Contains("TX0") && received.Contains("BI1"),
                "Transmit or band-information Auto Information frames were lost.");
        }
    }
}

{
    var (connection, port, _) = Create();
    using (connection)
    {
        var settings = Task.Run(connection.ReadRfPowerSettings);
        await WaitFor(() => port.Writes.Contains("PC;"));
        _ = port.Inject("PC2005;");
        await WaitFor(() => port.Writes.Contains("EX030705;"));
        try { await settings; throw new InvalidOperationException("Missing power-limit response did not time out."); }
        catch (TimeoutException) { }
        Assert(!port.Writes.Contains("EX030706;"), "The next power-limit query followed a timeout.");
    }
}

Console.WriteLine("CAT command sequencing checks passed.");
