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
//   ha+ [n]  ha- [n] ... hd+ [n]  hd- [n]   output 1 to 4 (a–d) in one direction for n × 100 ms (default 3)
//   hs1 [n]  hs2 [n]       left / right square button
//   hx [a-d]               stop one battery box output, or all of them
//   The box pairs with the first remote it hears.
//
// Answers: "OK <command>", "FULL" when too many notches are queued, "? …" for an unknown command,
// "ERROR nRF24L01 not found …" to every command when the radio did not answer at startup (wiring).
// Test it with the PC app ../Remote (Bluetooth mode), "nRF Connect" or "Serial Bluetooth Terminal".
//
// Wiring (nRF24 adapter powered with 5 V): CE -> 4, CSN -> 5, SCK -> 18, MISO -> 19, MOSI -> 23.

using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Threading;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;
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
        private const string RadioMissing = "ERROR nRF24L01 not found: check the wiring and the power supply";

        private const int AdvertisingCheckMs = 250, AfterDisconnectionMs = 500, ReadvertiseMs = 10000;

        private static readonly object ReceiveLock = new object();
        private static readonly object LinkLock = new object();
        private static bool _linkUp, _linkDropped;
        private static int _idleMs;
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
            if (radio.Initialize(Jm039Protocol.AddressRegister, DefaultPowerLevel))
            {
                _transmitter = new Transmitter(radio);
                _transmitter.Start();
                _interpreter = new CommandInterpreter(_transmitter, radio);
            }
            else
            {
                // Start Bluetooth anyway, so that the app can show what is wrong (see Reply)
                Debug.WriteLine(RadioMissing);
            }

            _bluetooth = new NordicSpp();
            _bluetooth.ReceivedData += OnDataReceived;
            _bluetooth.ConnectedEvent += OnConnectionChanged;
            _bluetooth.Start(BluetoothName);
            BluetoothLEServer.Instance.Session.SessionStatusChanged += OnLinkChanged;
            new Thread(KeepAdvertising).Start();
            Debug.WriteLine("NanoBrick ready, waiting for a Bluetooth connection...");

            Thread.Sleep(Timeout.Infinite);
        }

        private static void OnConnectionChanged(IBluetoothSpp sender, EventArgs e)
        {
            Debug.WriteLine(sender.IsConnected ? "Connected" : "Disconnected");
            if (sender.IsConnected) return;

            if (_transmitter != null) _transmitter.SetKeepAlive(false);   // let the original remotes take over
        }

        // Real Bluetooth link state (NordicSpp's "connected" only means "subscribed to TX notifications":
        // it turns false when the client unsubscribes, while the link is still open).
        private static void OnLinkChanged(object sender, GattSessionStatusChangedEventArgs e)
        {
            bool up = e.Status == GattSessionStatus.Active;
            Debug.WriteLine("Link " + (up ? "up" : "down"));
            if (!up && _transmitter != null) _transmitter.SetKeepAlive(false);   // out of range, app killed…
            lock (LinkLock)
            {
                _linkUp = up;
                _linkDropped = !up;
                _idleMs = 0;
            }
        }

        // The native stack (nanoFramework 1.18, NimBLE) does not reliably advertise again by itself:
        // - after a disconnection, it often stays silent;
        // - after a failed connection attempt (BLE_GAP_EVENT_CONNECT with an error), it never advertises
        //   again and raises no managed event at all.
        // So, while no client is connected, advertising is restarted 0.5 s after the link drops, then every
        // 10 s. Never while a link is open: Stop() frees the native GATT table under the client's feet (the
        // previous version did it on the "unsubscribed" event, and Windows could soon no longer find the service).
        private static void KeepAdvertising()
        {
            while (true)
            {
                Thread.Sleep(AdvertisingCheckMs);
                lock (LinkLock)
                {
                    if (_linkUp) continue;
                    _idleMs += AdvertisingCheckMs;
                    bool due = _linkDropped ? _idleMs >= AfterDisconnectionMs : _idleMs >= ReadvertiseMs;
                    if (!due) continue;
                    _linkDropped = false;
                    _idleMs = 0;
                }

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
            }
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
                if (command.Length > 0) Reply(_interpreter != null ? _interpreter.Execute(command) : RadioMissing);
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
