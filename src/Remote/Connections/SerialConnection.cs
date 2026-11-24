using System.IO.Ports;
using System.Text;

namespace Remote.Connections;

/// <summary>
/// USB cable, for the Arduino transmitter (tools/JM039TX): 115200 baud, one command per line.
/// DTR and RTS stay low so that opening the port does not reset the ESP32.
/// </summary>
public sealed class SerialConnection : IConnection
{
    private const int BaudRate = 115200;

    private readonly SerialPort _port;
    private readonly LineSplitter _lines = new();

    public event Action<string>? LineReceived;
    public event Action? Lost;

    public SerialConnection(string portName)
    {
        _port = new SerialPort(portName, BaudRate)
        {
            DtrEnable = false,
            RtsEnable = false,
            NewLine = "\n",
            Encoding = Encoding.ASCII,
        };
        _port.DataReceived += OnDataReceived;
        _port.ErrorReceived += (_, _) => Lost?.Invoke();
    }

    public string Description => $"USB cable ({_port.PortName})";

    public static string[] AvailablePorts() => SerialPort.GetPortNames().Order().ToArray();

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _port.Open();   // throws UnauthorizedAccessException if the port is in use (Arduino IDE, web page…)
        return Task.CompletedTask;
    }

    public Task SendAsync(string line)
    {
        try
        {
            _port.Write(line + "\n");
        }
        catch (Exception) when (!_port.IsOpen)
        {
            Lost?.Invoke();
        }
        return Task.CompletedTask;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        string chunk;
        try
        {
            chunk = _port.ReadExisting();
        }
        catch (Exception)
        {
            Lost?.Invoke();
            return;
        }

        foreach (string line in _lines.Append(chunk)) LineReceived?.Invoke(line);
    }

    public ValueTask DisposeAsync()
    {
        _port.DataReceived -= OnDataReceived;
        if (_port.IsOpen) _port.Close();
        _port.Dispose();
        return ValueTask.CompletedTask;
    }
}
