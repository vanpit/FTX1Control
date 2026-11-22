using System.IO.Ports;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Globalization;
using System.Diagnostics;

namespace FTX1Control.Models;

public sealed class CatSerialConnection : IDisposable
{
    private SerialPort? _serialPort;

    private readonly object _sync = new();
    private readonly object _receiveSync = new();
    private readonly StringBuilder _receiveBuffer = new();
    private readonly HashSet<int> _pendingMeterResponses = new();
    private bool _autoInformationEnabled;

    public event Action<string>? AutoInformationReceived;
    public event Action<Exception>? AutoInformationError;
    public event Action<string>? CatFrameLogged;

    public void Connect(string portName, int baudRate)
    {
        Disconnect();
        lock (_receiveSync)
        {
            _receiveBuffer.Clear();
            _pendingMeterResponses.Clear();
        }

        var serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };

        try
        {
            serialPort.Open();
            _serialPort = serialPort;
        }
        catch
        {
            serialPort.Dispose();
            throw;
        }
    }

    public (int Main, int Sub) ReadMeters()
    {
        lock (_sync)
        {
            var response = Query("MS;");
            if (response.Length != 4 || !response.StartsWith("MS", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(response.AsSpan(2, 1), out var mainMeter)
                || !int.TryParse(response.AsSpan(3, 1), out var subMeter)
                || mainMeter is < 0 or > 5 || subMeter is < 0 or > 5)
            {
                throw new InvalidDataException($"Unexpected CAT meter response: {response};");
            }

            return (mainMeter, subMeter);
        }
    }

    public (int MainFrequencyHz, int SubFrequencyHz, bool IsTransmitting) ReadRadioState()
    {
        lock (_sync)
        {
            var status = Query("RI0;");
            if (status.Length != 10 || !status.StartsWith("RI0", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(status.AsSpan(3, 7), out _)
                || status[5] is not ('0' or '1' or '2'))
            {
                throw new InvalidDataException($"Unexpected CAT radio status response: {status};");
            }

            var mainFrequency = ReadFrequency("FA");
            var subFrequency = ReadFrequency("FB");
            var isTransmitting = status[5] == '1';
            return (mainFrequency, subFrequency, isTransmitting);
        }
    }

    public int ReadTransmitVfo()
    {
        lock (_sync)
        {
            var response = Query("FT;");
            if (response.Length != 3
                || !response.StartsWith("FT", StringComparison.OrdinalIgnoreCase)
                || response[2] is not ('0' or '1'))
            {
                throw new InvalidDataException($"Unexpected CAT transmit VFO response: {response};");
            }

            return response[2] - '0';
        }
    }

    public (char Main, char Sub) ReadVfoModes()
    {
        lock (_sync)
        {
            return (ReadVfoMode("MD0"), ReadVfoMode("MD1"));
        }
    }

    public (int Main, int Sub) ReadAudioGains()
    {
        lock (_sync)
        {
            return (ReadAudioGain("AG0"), ReadAudioGain("AG1"));
        }
    }

    public (int Watts, Dictionary<int, int> Limits, string? Warning) ReadRfPowerSettings()
    {
        lock (_sync)
        {
            var response = Query("PC;");
            if (!TryParseRfPower(response, out var watts))
            {
                throw new InvalidDataException($"Nieobsługiwana odpowiedź CAT mocy SPA-1: {response};");
            }

            var limits = new Dictionary<int, int>();
            var errors = new List<string>();
            for (var item = 5; item <= 11; item++)
            {
                var command = $"EX0307{item:D2}";
                try
                {
                    response = Query($"{command};");
                    if (!TryParseRfPowerLimit(response, out var responseItem, out var limit) || responseItem != item)
                    {
                        throw new InvalidDataException($"Nieoczekiwana odpowiedź: {response};");
                    }

                    limits.Add(item, limit);
                }
                catch (Exception exception) when (exception is InvalidDataException or TimeoutException)
                {
                    errors.Add($"{command}: {exception.Message}");
                }
            }

            return (watts, limits, errors.Count == 0 ? null : string.Join(" ", errors));
        }
    }

    public static bool TryParseRfPower(string response, out int watts)
    {
        watts = 0;
        return response.Length == 6
            && response.StartsWith("PC2", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(response.AsSpan(3, 3), NumberStyles.None, CultureInfo.InvariantCulture, out watts)
            && watts is >= 5 and <= 100;
    }

    public static bool TryParseRfPowerLimit(string response, out int item, out int watts)
    {
        item = 0;
        watts = 0;
        return response.Length == 11
            && response.StartsWith("EX0307", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(response.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out item)
            && item is >= 5 and <= 11
            && int.TryParse(response.AsSpan(8, 3), NumberStyles.None, CultureInfo.InvariantCulture, out watts)
            && watts >= 5 && watts <= (item switch { 5 or 6 => 100, 7 or 8 or 9 => 50, 10 => 25, _ => 13 });
    }

    public void SetRfPower(int watts)
    {
        if (watts is < 5 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(watts));
        }

        lock (_sync)
        {
            var serialPort = GetOpenPort();
            var command = $"PC2{watts:D3};";
            LogFrame("TX", command);
            serialPort.Write(command);
        }
    }

    public void RequestRfPower()
    {
        lock (_sync)
        {
            var serialPort = GetOpenPort();
            LogFrame("TX", "PC;");
            serialPort.Write("PC;");
        }
    }

    public void RequestDLevel()
    {
        lock (_sync)
        {
            var serialPort = GetOpenPort();
            LogFrame("TX", "SS04;");
            serialPort.Write("SS04;");
        }
    }

    public void RequestNoiseBlankerLevel()
    {
        lock (_sync)
        {
            var serialPort = GetOpenPort();
            LogFrame("TX", "NL0;");
            serialPort.Write("NL0;");
        }
    }

    public void SetNoiseBlankerLevel(int level) => SetNoiseControlLevel("NL", level);

    public void RequestDigitalNoiseReductionLevel()
    {
        lock (_sync)
        {
            var serialPort = GetOpenPort();
            LogFrame("TX", "RL0;");
            serialPort.Write("RL0;");
        }
    }

    public void SetDigitalNoiseReductionLevel(int level) => SetNoiseControlLevel("RL", level);

    public static bool TryParseNoiseControlLevel(string response, string command, out int level)
    {
        level = 0;
        var digits = command switch
        {
            "NL" => 3,
            "RL" => 2,
            _ => 0
        };
        return digits != 0
            && response.Length == 3 + digits
            && response.StartsWith($"{command}0", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(response.AsSpan(3, digits), NumberStyles.None, CultureInfo.InvariantCulture, out level)
            && level is >= 0 and <= 10;
    }

    private void SetNoiseControlLevel(string command, int level)
    {
        if (level is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(level), $"{command} must be between 0 and 10.");
        }

        lock (_sync)
        {
            var serialPort = GetOpenPort();
            var frame = command switch
            {
                "NL" => $"NL0{level:D3};",
                "RL" => $"RL0{level:D2};",
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
            LogFrame("TX", frame);
            serialPort.Write(frame);
        }
    }

    public void SetDLevel(decimal level)
    {
        if (level is < -30m or > 30m || level * 2 != decimal.Truncate(level * 2))
        {
            throw new ArgumentOutOfRangeException(nameof(level), "D-LEVEL must be between -30 and +30 dB in 0.5 dB steps.");
        }

        lock (_sync)
        {
            var serialPort = GetOpenPort();
            var command = $"SS04{level.ToString("+00.0;-00.0;+00.0", CultureInfo.InvariantCulture)};";
            LogFrame("TX", command);
            serialPort.Write(command);
        }
    }

    public static bool TryParseDLevel(string response, out decimal level)
    {
        level = 0;
        if (response.Length != 9
            || !response.StartsWith("SS04", StringComparison.OrdinalIgnoreCase)
            || response[4] is not ('+' or '-')
            || response[5] is < '0' or > '9'
            || response[6] is < '0' or > '9'
            || response[7] != '.'
            || response[8] is not ('0' or '5')
            || !decimal.TryParse(response.AsSpan(4, 5), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out level)
            || level is < -30m or > 30m)
        {
            level = 0;
            return false;
        }

        return true;
    }

    public bool ReadPeakMeterDetector()
    {
        lock (_sync)
        {
            var response = Query("EX030513;");
            if (!TryParseMeterDetector(response, out var isPeak))
            {
                throw new InvalidDataException($"Unexpected CAT meter detector response: {response};");
            }

            return isPeak;
        }
    }

    public void SetPeakMeterDetector(bool isPeak)
    {
        lock (_sync)
        {
            var serialPort = GetOpenPort();
            var command = $"EX030513{(isPeak ? '1' : '0')};";
            LogFrame("TX", command);
            serialPort.Write(command);
        }
    }

    public void RequestMeterDetector()
    {
        lock (_sync)
        {
            var serialPort = GetOpenPort();
            LogFrame("TX", "EX030513;");
            serialPort.Write("EX030513;");
        }
    }

    public void StartAntennaTuning()
    {
        lock (_sync)
        {
            var serialPort = GetOpenPort();
            LogFrame("TX", "AC103;");
            serialPort.Write("AC103;");
        }
    }

    public static bool TryParseMeterDetector(string response, out bool isPeak)
    {
        isPeak = false;
        if (response.Length != 9
            || !response.StartsWith("EX030513", StringComparison.OrdinalIgnoreCase)
            || response[8] is not ('0' or '1'))
        {
            return false;
        }

        isPeak = response[8] == '1';
        return true;
    }

    public void EnableAutoInformation()
    {
        lock (_sync)
        {
            var serialPort = GetOpenPort();
            serialPort.DataReceived += OnDataReceived;
            _autoInformationEnabled = true;
            LogFrame("TX", "AI1;");
            serialPort.Write("AI1;");
        }
    }

    public void RequestTransmitMeterReadings()
    {
        lock (_sync)
        {
            var serialPort = GetOpenPort();
            lock (_receiveSync)
            {
                _pendingMeterResponses.Clear();
                _pendingMeterResponses.Add(4);
                _pendingMeterResponses.Add(5);
                _pendingMeterResponses.Add(6);
            }

            LogFrame("TX", "RM4;");
            LogFrame("TX", "RM5;");
            LogFrame("TX", "RM6;");
            try
            {
                serialPort.Write("RM4;RM5;RM6;");
            }
            catch
            {
                lock (_receiveSync)
                {
                    _pendingMeterResponses.Clear();
                }

                throw;
            }
        }
    }

    public void SetMeters(int mainMeter, int subMeter)
    {
        if (mainMeter is < 0 or > 5 || subMeter is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(mainMeter), "Meter codes must be between 0 and 5.");
        }

        lock (_sync)
        {
            var serialPort = GetOpenPort();
            var command = $"MS{mainMeter}{subMeter};";
            LogFrame("TX", command);
            serialPort.Write(command);
        }
    }

    public void SetAudioGain(int vfo, int level)
    {
        if (vfo is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(vfo), "VFO must be 0 (MAIN) or 1 (SUB).");
        }

        if (level is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "AF gain must be between 0 and 255.");
        }

        lock (_sync)
        {
            var serialPort = GetOpenPort();
            var command = $"AG{vfo}{level:D3};";
            LogFrame("TX", command);
            serialPort.Write(command);
        }
    }

    public void SetFrequencyAndMode(int vfo, int frequencyHz, char modeCode)
    {
        if (vfo is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(vfo), "VFO must be 0 (MAIN) or 1 (SUB).");
        }

        if (frequencyHz is < 0 or > 999_999_999)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Frequency must fit in nine digits.");
        }

        modeCode = char.ToUpperInvariant(modeCode);
        if (modeCode is not (>= '1' and <= '9') and not (>= 'A' and <= 'F') and not ('H' or 'I'))
        {
            throw new ArgumentOutOfRangeException(nameof(modeCode), "Unsupported CAT mode code.");
        }

        lock (_sync)
        {
            var serialPort = GetOpenPort();
            var frequencyCommand = $"{(vfo == 0 ? "FA" : "FB")}{frequencyHz:D9};";
            var modeCommand = $"MD{vfo}{modeCode};";
            LogFrame("TX", frequencyCommand);
            serialPort.Write(frequencyCommand);
            LogFrame("TX", modeCommand);
            serialPort.Write(modeCommand);
        }
    }

    private int ReadFrequency(string command)
    {
        var response = Query($"{command};");
        if (response.Length != 11 || !response.StartsWith(command, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(response.AsSpan(2, 9), NumberStyles.None, CultureInfo.InvariantCulture, out var frequency))
        {
            throw new InvalidDataException($"Unexpected CAT {command} frequency response: {response};");
        }

        return frequency;
    }

    private char ReadVfoMode(string command)
    {
        var response = Query($"{command};");
        if (response.Length != 4 || !response.StartsWith(command, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unexpected CAT {command} mode response: {response};");
        }

        return response[3];
    }

    private int ReadAudioGain(string command)
    {
        var response = Query($"{command};");
        if (response.Length != 6
            || !response.StartsWith(command, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(response.AsSpan(3, 3), out var level)
            || level is < 0 or > 255)
        {
            throw new InvalidDataException($"Unexpected CAT {command} audio gain response: {response};");
        }

        return level;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs eventArgs)
    {
        var serialPort = _serialPort;
        if (serialPort?.IsOpen != true)
        {
            return;
        }

        try
        {
            var data = serialPort.ReadExisting();
            if (data.Length == 0)
            {
                return;
            }

            List<string> messages = new();
            lock (_receiveSync)
            {
                _receiveBuffer.Append(data);
                var separatorIndex = _receiveBuffer.ToString().IndexOf(';');
                while (separatorIndex >= 0)
                {
                    if (separatorIndex > 256)
                    {
                        _receiveBuffer.Clear();
                        throw new InvalidDataException("CAT Auto Information frame exceeded the maximum length.");
                    }

                    messages.Add(_receiveBuffer.ToString(0, separatorIndex).Trim());
                    _receiveBuffer.Remove(0, separatorIndex + 1);
                    separatorIndex = _receiveBuffer.ToString().IndexOf(';');
                }

                if (_receiveBuffer.Length > 256)
                {
                    _receiveBuffer.Clear();
                    throw new InvalidDataException("CAT Auto Information frame exceeded the maximum length.");
                }
            }

            foreach (var message in messages)
            {
                LogFrame("RX", $"{message};");
                if (ShouldDispatchAutoInformation(message))
                {
                    AutoInformationReceived?.Invoke(message);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
        {
            AutoInformationError?.Invoke(exception);
        }
    }

    private string Query(string command)
    {
        var serialPort = GetOpenPort();
        LogFrame("TX", command);
        serialPort.Write(command);
        var originalTimeout = serialPort.ReadTimeout;
        try
        {
            return ReadMatchingResponse(command, remainingMilliseconds =>
            {
                serialPort.ReadTimeout = remainingMilliseconds;
                return serialPort.ReadTo(";");
            }, originalTimeout);
        }
        finally
        {
            serialPort.ReadTimeout = originalTimeout;
        }
    }

    private string ReadMatchingResponse(string command, Func<int, string> readFrame, int timeoutMilliseconds)
    {
        var expectedPrefix = command.TrimEnd(';');
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var remaining = timeoutMilliseconds - stopwatch.ElapsedMilliseconds;
            if (remaining <= 0)
            {
                throw new TimeoutException($"Przekroczono czas oczekiwania na odpowiedź CAT dla {command}");
            }

            string response;
            try
            {
                response = readFrame((int)remaining).Trim();
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"Przekroczono czas oczekiwania na odpowiedź CAT dla {command}", exception);
            }

            LogFrame("RX", $"{response};");
            if (response == "?")
            {
                throw new InvalidDataException($"Radio odrzuciło polecenie CAT {command}");
            }

            if (response.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return response;
            }
        }
    }

    private void LogFrame(string direction, string frame) =>
        CatFrameLogged?.Invoke($"[{direction}] {frame}");

    private bool ShouldDispatchAutoInformation(string message)
    {
        if (!TryGetTransmitMeterCode(message, out var meterCode))
        {
            return true;
        }

        lock (_receiveSync)
        {
            return _pendingMeterResponses.Remove(meterCode);
        }
    }

    private static bool TryGetTransmitMeterCode(string message, out int meterCode)
    {
        meterCode = 0;
        if (message.Length != 9
            || !message.StartsWith("RM", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(message.AsSpan(2, 1), out meterCode)
            || meterCode is not (4 or 5 or 6)
            || !int.TryParse(message.AsSpan(3, 3), out var value)
            || !int.TryParse(message.AsSpan(6, 3), out _)
            || value is < 0 or > 255)
        {
            return false;
        }

        return true;
    }

    private SerialPort GetOpenPort()
    {
        if (_serialPort?.IsOpen != true)
        {
            throw new InvalidOperationException("Connect to the radio before sending CAT commands.");
        }

        return _serialPort;
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            if (_serialPort is null)
            {
                return;
            }

            var serialPort = _serialPort;
            _serialPort = null;
            serialPort.DataReceived -= OnDataReceived;
            lock (_receiveSync)
            {
                _pendingMeterResponses.Clear();
            }

            var disableAutoInformation = _autoInformationEnabled;
            _autoInformationEnabled = false;

            try
            {
                if (disableAutoInformation && serialPort.IsOpen)
                {
                    LogFrame("TX", "AI0;");
                    serialPort.Write("AI0;");
                }
            }
            finally
            {
                serialPort.Dispose();
            }
        }
    }

    public void Dispose() => Disconnect();
}