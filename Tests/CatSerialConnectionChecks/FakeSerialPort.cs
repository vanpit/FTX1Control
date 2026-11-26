using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Ports;

public enum Parity { None }
public enum StopBits { One }
public enum Handshake { None }
public delegate void SerialDataReceivedEventHandler(object sender, SerialDataReceivedEventArgs args);
public sealed class SerialDataReceivedEventArgs : EventArgs;

public sealed class SerialPort : IDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _writes = new();
    private string _input = string.Empty;

    public SerialPort(string name, int rate, Parity parity, int bits, StopBits stopBits) => Last = this;
    public static SerialPort Last { get; private set; } = null!;
    public Handshake Handshake { get; set; }
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }
    public int ReadTimeout { get; set; }
    public int WriteTimeout { get; set; }
    public bool IsOpen { get; private set; }
    public event SerialDataReceivedEventHandler? DataReceived;

    public void Open() => IsOpen = true;
    public void Dispose() => IsOpen = false;

    public void Write(string value)
    {
        lock (_gate)
        {
            _writes.Add(value);
        }
    }

    public string[] Writes
    {
        get { lock (_gate) return _writes.ToArray(); }
    }

    public string ReadExisting()
    {
        lock (_gate)
        {
            var value = _input;
            _input = string.Empty;
            return value;
        }
    }

    public string ReadTo(string separator)
    {
        lock (_gate)
        {
            while (!_input.Contains(separator))
            {
                if (!Monitor.Wait(_gate, ReadTimeout))
                {
                    throw new TimeoutException();
                }
            }

            var index = _input.IndexOf(separator, StringComparison.Ordinal);
            var value = _input[..index];
            _input = _input[(index + separator.Length)..];
            return value;
        }
    }

    public Task Inject(string value)
    {
        lock (_gate)
        {
            _input += value;
            Monitor.PulseAll(_gate);
        }

        return Task.Run(() => DataReceived?.Invoke(this, new SerialDataReceivedEventArgs()));
    }
}
