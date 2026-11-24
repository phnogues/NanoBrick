using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Remote.Connections;

namespace Remote;

/// <summary>
/// Drives up to 4 receivers (selector 1 to 4) × 2 outputs (A, B) = 8 motors with the transmitter's text
/// commands: "2a+ 3" (3 notches, receiver 2, output A), "4sb" (stop), "x" (stop all), "m 12" (receivers
/// present), "k 1" (keep-alive), "p 0", "f". Plus the 4-output battery box (white remote): on / off, an
/// output runs while its button is held ("hd+ 3" repeated, then "hx d" on release).
/// </summary>
public partial class MainWindow : Window
{
    private const string DefaultPort = "COM5";
    private const int StopButtonTenths = 3;
    private const int MaxLogLines = 300;

    private readonly Dictionary<string, ReceiverOutput> _outputs = new();   // "1a" … "4b"

    // While connected, the transmitter keeps the receivers locked (steady light)
    private readonly DispatcherTimer _keepAliveTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    // Battery box: while a button is held, its command (300 ms) is sent again every 200 ms
    private readonly DispatcherTimer _batteryBoxRepeatTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string? _heldBatteryBoxCommand;   // e.g. "hd+ 3", or null

    private IConnection? _connection;
    private CancellationTokenSource? _connectCancellation;

    /// <summary>One output of a receiver, i.e. one motor.</summary>
    private sealed class ReceiverOutput
    {
        public required int Receiver { get; init; }
        public required char Letter { get; init; }
        public required TextBlock SpeedDisplay { get; init; }
        public required CheckBox Reverse { get; init; }

        /// <summary>Estimated speed: the receiver sends nothing back, so notches are counted.</summary>
        public int Speed { get; set; }

        public string Key => $"{Receiver}{Letter}";
    }

    public MainWindow()
    {
        InitializeComponent();
        RefreshPorts();

        for (int receiver = 1; receiver <= 4; receiver++) ReceiversPanel.Children.Add(CreateReceiverCard(receiver));
        foreach (char letter in "abcd") BatteryBoxPanel.Children.Add(CreateBatteryBoxOutput(letter));

        _keepAliveTimer.Tick += async (_, _) => await SendAsync("k 1", log: false);
        _batteryBoxRepeatTimer.Tick += async (_, _) =>
        {
            if (_heldBatteryBoxCommand is not null) await SendAsync(_heldBatteryBoxCommand, log: false);
        };
    }

    private bool IsConnected => _connection is not null;

    // ---------- UI construction ----------

    private GroupBox CreateReceiverCard(int receiver)
    {
        var outputs = new StackPanel();
        foreach (char letter in "ab") outputs.Children.Add(CreateReceiverOutput(receiver, letter));

        return new GroupBox
        {
            Header = new TextBlock { Text = $"Receiver {receiver}  (selector on {receiver})", FontWeight = FontWeights.SemiBold },
            Content = outputs,
            Background = (Brush)FindResource("GreyBrush"),
        };
    }

    private DockPanel CreateReceiverOutput(int receiver, char letter)
    {
        var speedDisplay = new TextBlock
        {
            Text = "0", Width = 52, FontSize = 24, FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var reverse = new CheckBox
        {
            Content = "⟲", ToolTip = "Reverse the direction",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        };
        var output = new ReceiverOutput { Receiver = receiver, Letter = letter, SpeedDisplay = speedDisplay, Reverse = reverse };
        _outputs[output.Key] = output;

        string name = char.ToUpper(letter).ToString();
        var stop = CreateButton("STOP", "StopButton", $"Stop receiver {receiver}, output {name}", async () => await StopAsync(output));
        stop.Width = 80;
        DockPanel.SetDock(stop, Dock.Right);
        DockPanel.SetDock(reverse, Dock.Right);

        var wheel = new StackPanel { Orientation = Orientation.Horizontal };
        wheel.Children.Add(CreateButton("−", "WheelButton", "Slower / backward", async () => await StepAsync(output, -1)));
        wheel.Children.Add(speedDisplay);
        wheel.Children.Add(CreateButton("+", "WheelButton", "Faster / forward", async () => await StepAsync(output, 1)));

        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.Bold, Width = 24, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(stop);
        row.Children.Add(reverse);
        row.Children.Add(wheel);
        return row;
    }

    // A battery box output: ◀ and ▶ must be held down, like on the white remote
    private StackPanel CreateBatteryBoxOutput(char letter)
    {
        string name = char.ToUpper(letter).ToString();

        Button CreateHoldButton(string text, char direction)
        {
            var button = new Button { Content = text, Style = (Style)FindResource("WheelButton"), ToolTip = $"Output {name}: hold to run" };
            button.PreviewMouseLeftButtonDown += async (_, _) => await PressBatteryBoxAsync($"h{letter}{direction} 3");
            button.PreviewMouseLeftButtonUp += async (_, _) => await ReleaseBatteryBoxAsync(letter);
            button.MouseLeave += async (_, _) =>
            {
                if (_heldBatteryBoxCommand?[1] == letter) await ReleaseBatteryBoxAsync(letter);
            };
            return button;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4) };
        row.Children.Add(CreateHoldButton("◀", '-'));
        row.Children.Add(new TextBlock { Text = name, FontSize = 22, FontWeight = FontWeights.Bold, Width = 32, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(CreateHoldButton("▶", '+'));
        return row;
    }

    private Button CreateButton(string text, string styleKey, string toolTip, Action onClick)
    {
        var button = new Button { Content = text, Style = (Style)FindResource(styleKey), ToolTip = toolTip };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------- Connection ----------

    private void RefreshPorts()
    {
        string[] ports = SerialConnection.AvailablePorts();
        PortList.ItemsSource = ports;
        PortList.SelectedItem = ports.Contains(DefaultPort) ? DefaultPort : ports.LastOrDefault();
    }

    private void OnRefreshPorts(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        IConnection connection;
        if (SerialOption.IsChecked == true)
        {
            if (PortList.SelectedItem is not string port)
            {
                ShowStatus("Choose a COM port", Brushes.OrangeRed);
                return;
            }
            connection = new SerialConnection(port);
        }
        else
        {
            connection = new BluetoothConnection();
        }

        ConnectButton.IsEnabled = false;
        ShowStatus($"Connecting over {connection.Description}…", Brushes.Orange);
        _connectCancellation = new CancellationTokenSource();
        try
        {
            connection.LineReceived += line => Dispatcher.BeginInvoke(() => AddToLog("← " + line));
            connection.Lost += () => Dispatcher.BeginInvoke(async () =>
            {
                if (_connection != connection) return;
                AddToLog("Connection lost");
                await DisconnectAsync();
            });
            await connection.ConnectAsync(_connectCancellation.Token);
            _connection = connection;
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync();
            ShowStatus("Failed: " + ex.Message, Brushes.Red);
            AddToLog("Connection failed: " + ex.Message);
            ConnectButton.IsEnabled = true;
            return;
        }

        ConnectButton.Content = "Disconnect";
        ConnectButton.IsEnabled = true;
        SetControlsEnabled(true);
        ShowStatus($"Connected over {connection.Description}", Brushes.ForestGreen);

        await SendAsync("m " + SelectedReceivers());
        await SendAsync("p " + PowerLevel.SelectedIndex);
        await SendAsync("k 1");
        _keepAliveTimer.Start();
    }

    private async Task DisconnectAsync()
    {
        _keepAliveTimer.Stop();
        _batteryBoxRepeatTimer.Stop();
        _heldBatteryBoxCommand = null;

        IConnection? connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            try
            {
                await connection.SendAsync("k 0");   // hand control back to the original remotes
            }
            catch
            {
                // Already disconnected
            }
            await connection.DisposeAsync();
        }
        _connectCancellation?.Cancel();

        ConnectButton.Content = "Connect";
        ConnectButton.IsEnabled = true;
        SetControlsEnabled(false);
        ShowStatus("Not connected", (Brush)FindResource("DarkGreyBrush"));
    }

    private void SetControlsEnabled(bool connected)
    {
        ReceiversPanel.IsEnabled = SettingsPanel.IsEnabled = BatteryBoxPanel.IsEnabled = connected;
        SerialOption.IsEnabled = BluetoothOption.IsEnabled = PortList.IsEnabled = !connected;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!IsConnected) return;
        e.Cancel = true;   // disconnect cleanly first (k 0), then close for real
        await DisconnectAsync();
        Close();
    }

    // ---------- Commands ----------

    private int StepsPerClickValue() => int.Parse((string)((ComboBoxItem)StepsPerClick.SelectedItem).Content);

    private string SelectedReceivers() => (string)((ComboBoxItem)PresentReceivers.SelectedItem).Tag;

    private async Task StepAsync(ReceiverOutput output, int direction)
    {
        if (!IsConnected) return;
        int steps = StepsPerClickValue();
        int wireDirection = output.Reverse.IsChecked == true ? -direction : direction;
        await SendAsync($"{output.Key}{(wireDirection > 0 ? "+" : "-")} {steps}");
        output.Speed += direction * steps;
        UpdateSpeedDisplay(output);
    }

    private async Task StopAsync(ReceiverOutput output)
    {
        if (!IsConnected) return;
        await SendAsync($"{output.Receiver}s{output.Letter} {StopButtonTenths}");
        output.Speed = 0;
        UpdateSpeedDisplay(output);
    }

    private async Task StopAllAsync()
    {
        if (!IsConnected) return;
        _batteryBoxRepeatTimer.Stop();
        _heldBatteryBoxCommand = null;
        await SendAsync("x");
        foreach (ReceiverOutput output in _outputs.Values)
        {
            output.Speed = 0;
            UpdateSpeedDisplay(output);
        }
    }

    private async Task PressBatteryBoxAsync(string command)
    {
        if (!IsConnected) return;
        _heldBatteryBoxCommand = command;
        await SendAsync(command);
        _batteryBoxRepeatTimer.Start();
    }

    private async Task ReleaseBatteryBoxAsync(char letter)
    {
        if (_heldBatteryBoxCommand is null) return;
        _heldBatteryBoxCommand = null;
        _batteryBoxRepeatTimer.Stop();
        await SendAsync($"hx {letter}");
    }

    private async void OnStopAllClick(object sender, RoutedEventArgs e) => await StopAllAsync();

    private async void OnPresentReceiversChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsConnected) await SendAsync("m " + SelectedReceivers());
    }

    private async void OnPowerLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsConnected) await SendAsync("p " + PowerLevel.SelectedIndex);
    }

    private async void OnStatusClick(object sender, RoutedEventArgs e) => await SendAsync("f");

    private async void OnSendRawCommand(object sender, RoutedEventArgs e)
    {
        string command = RawCommand.Text.Trim();
        if (command.Length == 0 || !IsConnected) return;
        await SendAsync(command);
        RawCommand.Clear();
    }

    // Keyboard shortcuts for receiver 1: W or Z / S = output A, ↑ / ↓ = output B, A and P = stops, Space = stop all
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsConnected || RawCommand.IsKeyboardFocusWithin) return;
        switch (e.Key)
        {
            case Key.W:
            case Key.Z: await StepAsync(_outputs["1a"], 1); break;
            case Key.S: await StepAsync(_outputs["1a"], -1); break;
            case Key.Up: await StepAsync(_outputs["1b"], 1); e.Handled = true; break;
            case Key.Down: await StepAsync(_outputs["1b"], -1); e.Handled = true; break;
            case Key.A: await StopAsync(_outputs["1a"]); break;
            case Key.P: await StopAsync(_outputs["1b"]); break;
            case Key.Space: await StopAllAsync(); e.Handled = true; break;
        }
    }

    // ---------- Helpers ----------

    private async Task SendAsync(string command, bool log = true)
    {
        if (_connection is null) return;
        if (log) AddToLog("→ " + command);
        try
        {
            await _connection.SendAsync(command);
        }
        catch (Exception ex)
        {
            AddToLog("Send error: " + ex.Message);
        }
    }

    private void AddToLog(string message)
    {
        if (IsRepetitiveAnswer(message)) return;
        Log.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (Log.Items.Count > MaxLogLines) Log.Items.RemoveAt(0);
        Log.ScrollIntoView(Log.Items[^1]);
    }

    // Answers to the keep-alive and to held battery box buttons would flood the log
    private static bool IsRepetitiveAnswer(string message) =>
        message == "← OK k 1" || (message.StartsWith("← OK h") && message.EndsWith(" 3"));

    private void ShowStatus(string text, Brush color)
    {
        StatusText.Text = text;
        StatusLight.Fill = color;
    }

    private static void UpdateSpeedDisplay(ReceiverOutput output) =>
        output.SpeedDisplay.Text = output.Speed > 0 ? "+" + output.Speed : output.Speed.ToString();
}
