// nanoBrick — .NET nanoFramework firmware for ESP32 + nRF24L01+
// =============================================================
//
// The ESP32 imitates up to 4 grey JM039TX remotes at once (one per receiver selector position, so
// 4 receivers × 2 outputs = 8 motors) and the white remote of the 4-output battery box.
//
// It advertises over Bluetooth LE as "NanoBrick" (Nordic UART service) and takes text commands,
// one per line. A leading digit (1 to 4) selects the receiver; without it, the current position (c):
//
//   [p]a+ [n]  [p]a- [n]   n notches of the left wheel (output A), direction 1 / 2      (default 1)
//   [p]b+ [n]  [p]b- [n]   n notches of the right wheel (output B)
//   [p]sa [n]  [p]sb [n]   left / right orange stop button held n × 100 ms              (default 3)
//   [p]w <n>               neutral frames for n × 100 ms
//   x                      stop every output of the served receivers, and the battery box
//   k 1|0                  keep-alive: served receivers stay locked for 15 s, extended by every command
//   m <1234>               receivers served by the keep-alive and by x (default 1234)
//   c <1-4>                current position
//   p <0-3>                radio power -18 / -12 / -6 / 0 dBm (default 0: louder saturates the receivers)
//   f                      status: longest transmission round, packets sent / failed
//
// 4-output battery box (white remote): on / off, full speed, no receiver to select.
//   ha+ [n]  ha- [n] ... hd+ [n]  hd- [n]   output A to D in one direction for n × 100 ms   (default 3)
//   hs1 [n]  hs2 [n]       left / right square button
//   hx [a-d]               stop one battery box output, or all of them
//   The box pairs with the first remote it hears.
//
// Answers: "OK <command>", "FULL" when too many notches are queued, "? …" for an unknown command.
// Test it with the PC app ../Remote (Bluetooth mode), "nRF Connect" or "Serial Bluetooth Terminal".
//
// Wiring (nRF24 adapter powered with 5 V): CE -> 4, CSN -> 5, SCK -> 18, MISO -> 19, MOSI -> 23.

using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Threading;
using nanoFramework.Device.Bluetooth.Spp;
using nanoFramework.Hardware.Esp32;
using NanoBrick.Hardware;
using NanoBrick.Protocols;
using NanoBrick.Transmission;

namespace NanoBrick
{
    public static class Program
    {
        // Wiring
        private const int CePin = 4, CsnPin = 5, SckPin = 18, MisoPin = 19, MosiPin = 23;
        private const int SpiBus = 1;

        // Settings
        private const string BluetoothName = "NanoBrick";
        private const int DefaultPowerLevel = 0;   // -18 dBm
        private const int MaxPendingChars = 64;

        private static readonly object ReceiveLock = new object();
        private static NordicSpp _bluetooth;
        private static Transmitter _transmitter;
        private static CommandInterpreter _interpreter;
        private static string _pending = string.Empty;

        public static void Main()
        {
            Configuration.SetPinFunction(MosiPin, DeviceFunction.SPI1_MOSI);
            Configuration.SetPinFunction(MisoPin, DeviceFunction.SPI1_MISO);
            Configuration.SetPinFunction(SckPin, DeviceFunction.SPI1_CLOCK);

            GpioPin ce = new GpioController().OpenPin(CePin, PinMode.Output);
            var radio = new Nrf24Radio(SpiBus, CsnPin, ce);
            if (!radio.Initialize(Jm039Protocol.AddressRegister, DefaultPowerLevel))
            {
                Debug.WriteLine("nRF24L01 not found: check the wiring and the power supply");
                Thread.Sleep(Timeout.Infinite);
            }

            _transmitter = new Transmitter(radio);
            _transmitter.Start();
            _interpreter = new CommandInterpreter(_transmitter, radio);

            _bluetooth = new NordicSpp();
            _bluetooth.ReceivedData += OnDataReceived;
            _bluetooth.ConnectedEvent += OnConnectionChanged;
            _bluetooth.Start(BluetoothName);
            Debug.WriteLine("NanoBrick ready, waiting for a Bluetooth connection...");

            Thread.Sleep(Timeout.Infinite);
        }

        private static void OnConnectionChanged(IBluetoothSpp sender, EventArgs e)
        {
            Debug.WriteLine(sender.IsConnected ? "Connected" : "Disconnected");
            if (sender.IsConnected) return;

            _transmitter.SetKeepAlive(false);   // let the original remotes take over

            // After a disconnection, advertising does not always restart by itself: restart it,
            // from another thread (not from the Bluetooth stack's event).
            new Thread(() =>
            {
                Thread.Sleep(500);
                try
                {
                    _bluetooth.Stop();
                    Thread.Sleep(200);
                    _bluetooth.Start(BluetoothName);
                    Debug.WriteLine("Bluetooth advertising restarted");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Could not restart Bluetooth: " + ex.Message);
                }
            }).Start();
        }

        // Bluetooth data can arrive in pieces: split it into lines
        private static void OnDataReceived(IBluetoothSpp sender, SppReceivedDataEventArgs e)
        {
            string[] lines;
            lock (ReceiveLock)
            {
                _pending += e.DataString;
                int end = _pending.LastIndexOf('\n');
                if (end < 0)
                {
                    if (_pending.Length > MaxPendingChars) _pending = string.Empty;   // garbage: drop it
                    return;
                }
                lines = _pending.Substring(0, end).Split('\n');
                _pending = _pending.Substring(end + 1);
            }

            foreach (string line in lines)
            {
                string command = line.Trim();
                if (command.Length > 0) Reply(_interpreter.Execute(command));
            }
        }

        private static void Reply(string message)
        {
            Debug.WriteLine("-> " + message);
            if (_bluetooth != null && _bluetooth.IsConnected)
            {
                _bluetooth.SendString(message + "\n");
            }
        }
    }
}
