using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Remote.Connections;

namespace Remote;

/// <summary>
/// The page starts blank: the user adds one card per remote to imitate (grey remote = receiver with a 1–4 selector,
/// white remote = 4-output battery box). The list is saved in %AppData%\nanoBrick\remotes.json.
/// Receiver commands: "2a+ 3" (3 notches, receiver 2, output A), "4sb" (stop), "x" (stop all), "m 12" (receivers
/// present, derived from the cards), "k 1" (keep-alive), "p 0", "f". Battery box: an output runs while its button
/// is held ("hd+ 3" repeated, then "hx d" on release).
/// </summary>
public partial class MainWindow : Window
{
    private const string DefaultPort = "COM5";
    private const int StopButtonTenths = 3;
    private const int MaxLogLines = 300;
    private const int FullSpeedSteps = 25;    // ~20–25 notches = full speed on the receiver
    private const double GaugeWidth = 96;

    private readonly List<RemoteCard> _cards = new();

    // While connected, the transmitter keeps the receivers locked (steady light)
    private readonly DispatcherTimer _keepAliveTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    // Battery box: while a button is held, its command (300 ms) is sent again every 200 ms
    private readonly DispatcherTimer _batteryBoxRepeatTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string? _heldBatteryBoxCommand;   // e.g. "hd+ 3", or null

    private IConnection? _connection;
    private CancellationTokenSource? _connectCancellation;

    /// <summary>One card on the page: its settings, its frame, and the controls that need a connection.</summary>
    private sealed class RemoteCard
    {
        public required RemoteSettings Settings { get; init; }
        public required FrameworkElement Frame { get; init; }
        public required UIElement Controls { get; init; }
        public List<ReceiverOutput> Outputs { get; } = new();   // grey receiver only (A, B)
    }

    /// <summary>One output of a receiver, i.e. one motor.</summary>
    private sealed class ReceiverOutput
    {
        public required int Receiver { get; init; }
        public required char Letter { get; init; }
        public required TextBlock SpeedDisplay { get; init; }
        public required Border Gauge { get; init; }
        public required ToggleButton Reverse { get; init; }

        /// <summary>Estimated speed: the receiver sends nothing back, so notches are counted.</summary>
        public int Speed { get; set; }

        public string Key => $"{Receiver}{Letter}";
    }

    public MainWindow()
    {
        InitializeComponent();
        RefreshPorts();

        foreach (RemoteSettings settings in RemoteSettingsStore.Load()) AddCard(settings);
        UpdateHints();

        _keepAliveTimer.Tick += async (_, _) => await SendAsync("k 1", log: false);
        _batteryBoxRepeatTimer.Tick += async (_, _) =>
        {
            if (_heldBatteryBoxCommand is not null) await SendAsync(_heldBatteryBoxCommand, log: false);
        };
    }

    private bool IsConnected => _connection is not null;

    // ---------- Adding and removing remotes ----------

    private async void OnAddRemoteClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AddRemoteWindow(_cards.Select(c => c.Settings).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        AddCard(dialog.Result);
        await OnRemotesChangedAsync();
    }

    private async Task RemoveCardAsync(RemoteCard card)
    {
        if (MessageBox.Show(this, $"Remove \"{card.Settings.Name}\"? Its outputs are stopped first.", "Remove a remote",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        if (IsConnected)
        {
            if (card.Settings.Kind == RemoteKind.GreyReceiver)
            {
                foreach (ReceiverOutput output in card.Outputs) await StopAsync(output);
            }
            else
            {
                await ReleaseBatteryBoxAsync(null);
                await SendAsync("hx");
            }
        }

        _cards.Remove(card);
        RemotesPanel.Children.Remove(card.Frame);
        await OnRemotesChangedAsync();
    }

    private async Task OnRemotesChangedAsync()
    {
        RemoteSettingsStore.Save(_cards.Select(c => c.Settings));
        UpdateHints();
        if (IsConnected) await SendAsync("m " + ServedReceivers());
    }

    private void UpdateHints()
    {
        EmptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemotesPanel.HorizontalAlignment = _cards.Count == 0 ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        ConnectHint.Visibility = _cards.Count > 0 && !IsConnected ? Visibility.Visible : Visibility.Collapsed;
    }

    // Cards are kept in a stable order: receivers by selector position, then the battery box
    private void AddCard(RemoteSettings settings)
    {
        RemoteCard card = settings.Kind == RemoteKind.GreyReceiver ? CreateReceiverCard(settings) : CreateBatteryBoxCard(settings);
        card.Controls.IsEnabled = IsConnected;

        _cards.Add(card);
        _cards.Sort((x, y) => SortKey(x.Settings).CompareTo(SortKey(y.Settings)));
        RemotesPanel.Children.Insert(_cards.IndexOf(card), card.Frame);   // the "add" tile stays last
    }

    private static int SortKey(RemoteSettings settings) => settings.Kind == RemoteKind.GreyReceiver ? settings.Position : 10;

    // ---------- UI construction ----------

    private Brush Res(string key) => (Brush)FindResource(key);

    private static TextBlock Glyph(string glyph, double size = 14) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A white card with a tinted header (icon, name, subtitle, badge, ✕) above the controls.</summary>
    private Border CreateFrame(RemoteSettings settings, string iconKey, Brush tint, string subtitle, UIElement? badge,
                               UIElement controls, Func<RemoteCard> getCard)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new ContentControl { Content = FindResource(iconKey), Width = 48, Height = 40, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(icon);

        var titles = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = settings.Name, FontSize = 18, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        titles.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Res("MutedBrush") });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        if (badge is not null)
        {
            Grid.SetColumn(badge, 2);
            header.Children.Add(badge);
        }

        var remove = new Button
        {
            Style = (Style)FindResource("GhostButton"), Content = Glyph("", 12), ToolTip = "Remove this remote",
            Foreground = Res("MutedBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, -4, -6, 0),
        };
        remove.Click += async (_, _) => await RemoveCardAsync(getCard());
        Grid.SetColumn(remove, 3);
        header.Children.Add(remove);

        var body = new StackPanel();
        body.Children.Add(new Border { Background = tint, CornerRadius = new CornerRadius(18, 18, 0, 0), Padding = new Thickness(16, 12, 14, 12), Child = header });
        body.Children.Add(new Border { Padding = new Thickness(14, 10, 14, 14), Child = controls });

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Child = body,
            Width = 440,
            Margin = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    // Selector badge: "1" in an orange circle, like the position of the switch on the receiver
    private UIElement CreateSelectorBadge(int position)
    {
        var badge = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Selector on {position} on the receiver" };
        badge.Children.Add(new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Res("OrangeBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = position.ToString(), Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        badge.Children.Add(new TextBlock { Text = "SELECTOR", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Res("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        return badge;
    }

    private RemoteCard CreateReceiverCard(RemoteSettings settings)
    {
        var outputs = new StackPanel();
        RemoteCard? card = null;
        Border frame = CreateFrame(settings, "GreyRemoteIcon", Res("OrangeTintBrush"), "Grey remote · 2 motors, speed by steps",
                                   CreateSelectorBadge(settings.Position), outputs, () => card!);
        card = new RemoteCard { Settings = settings, Frame = frame, Controls = outputs };

        foreach (char letter in "ab")
        {
            if (letter != 'a') outputs.Children.Add(new Border { Height = 1, Background = Res("LineBrush"), Margin = new Thickness(4, 8, 4, 8) });
            outputs.Children.Add(CreateReceiverOutput(card, settings.Position, letter));
        }
        return card;
    }

    private Grid CreateReceiverOutput(RemoteCard card, int receiver, char letter)
    {
        string name = char.ToUpper(letter).ToString();

        var speedDisplay = new TextBlock { Text = "0", FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        var gauge = new Border { Height = 6, Width = 0, CornerRadius = new CornerRadius(3), Background = Res("OrangeBrush"), HorizontalAlignment = HorizontalAlignment.Left };
        var track = new Border { Width = GaugeWidth, Height = 6, CornerRadius = new CornerRadius(3), Background = Res("SoftBrush"), Child = gauge, Margin = new Thickness(0, 2, 0, 0) };
        var speedPanel = new StackPanel { Width = GaugeWidth + 10, VerticalAlignment = VerticalAlignment.Center };
        speedPanel.Children.Add(speedDisplay);
        speedPanel.Children.Add(track);

        var reverse = new ToggleButton { Style = (Style)FindResource("ReverseToggle"), ToolTip = "Reverse the direction of this motor", Margin = new Thickness(10, 0, 8, 0) };
        var output = new ReceiverOutput { Receiver = receiver, Letter = letter, SpeedDisplay = speedDisplay, Gauge = gauge, Reverse = reverse };
        card.Outputs.Add(output);

        var letterBadge = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Res("InkBrush"), Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };

        var stop = CreateButton("STOP", "StopButton", $"Stop receiver {receiver}, output {name}", async () => await StopAsync(output));

        var row = new Grid();
        UIElement[] cells =
        {
            letterBadge,
            CreateButton("−", "WheelButton", "Slower / backward", async () => await StepAsync(output, -1)),
            speedPanel,
            CreateButton("+", "WheelButton", "Faster / forward", async () => await StepAsync(output, 1)),
            reverse,
            stop,
        };
        for (int i = 0; i < cells.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = i == cells.Length - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(cells[i], i);
            ((FrameworkElement)cells[i]).VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(cells[i]);
        }
        return row;
    }

    private RemoteCard CreateBatteryBoxCard(RemoteSettings settings)
    {
        var outputs = new UniformGrid { Columns = 2 };
        foreach (char letter in "abcd") outputs.Children.Add(CreateBatteryBoxOutput(letter));

        RemoteCard? card = null;
        Border frame = CreateFrame(settings, "BatteryBoxIcon", Res("BlueTintBrush"), "White remote · hold a button, full speed",
                                   null, outputs, () => card!);
        card = new RemoteCard { Settings = settings, Frame = frame, Controls = outputs };
        return card;
    }

    // A battery box output: ◀ and ▶ must be held down, like on the white remote
    private Border CreateBatteryBoxOutput(char letter)
    {
        string name = (letter - 'a' + 1).ToString();   // outputs are marked 1–4 on the box, a–d in the commands

        Button CreateHoldButton(string glyph, char direction)
        {
            var button = new Button { Content = glyph, Style = (Style)FindResource("HoldButton"), ToolTip = $"Output {name}: hold to run" };
            button.PreviewMouseLeftButtonDown += async (_, _) => await PressBatteryBoxAsync($"h{letter}{direction} 3");
            button.PreviewMouseLeftButtonUp += async (_, _) => await ReleaseBatteryBoxAsync(letter);
            button.MouseLeave += async (_, _) =>
            {
                if (_heldBatteryBoxCommand?[1] == letter) await ReleaseBatteryBoxAsync(letter);
            };
            return button;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(CreateHoldButton("", '-'));
        row.Children.Add(new TextBlock { Text = name, FontSize = 22, FontWeight = FontWeights.Bold, Width = 40, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(CreateHoldButton("", '+'));

        return new Border { Background = Res("SoftBrush"), CornerRadius = new CornerRadius(14), Padding = new Thickness(8), Margin = new Thickness(4), Child = row };
    }

    private Button CreateButton(string text, string styleKey, string toolTip, Action onClick)
    {
        var button = new Button { Content = text, Style = (Style)FindResource(styleKey), ToolTip = toolTip };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OnConsoleToggled(object sender, RoutedEventArgs e)
    {
        bool open = ConsoleToggle.IsChecked == true;
        ConsoleBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ConsoleChevron.Text = open ? "" : "";
        LastLogLine.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnConnectionKindChanged(object sender, RoutedEventArgs e)
    {
        if (PortPanel is not null) PortPanel.Visibility = SerialOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
            ShowStatus("Failed: " + ex.Message, Res("RedBrush"));
            AddToLog("Connection failed: " + ex.Message);
            ConnectButton.IsEnabled = true;
            return;
        }

        ConnectButton.Content = "Disconnect";
        ConnectButton.Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x6C));
        ConnectButton.IsEnabled = true;
        SetControlsEnabled(true);
        ShowStatus($"Connected · {connection.Description}", Res("GreenBrush"));

        await SendAsync("m " + ServedReceivers());
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
        ConnectButton.ClearValue(BackgroundProperty);
        ConnectButton.IsEnabled = true;
        SetControlsEnabled(false);
        ShowStatus("Not connected", Res("DarkGreyBrush"));
    }

    private void SetControlsEnabled(bool connected)
    {
        StopAllButton.IsEnabled = StatusButton.IsEnabled = connected;
        foreach (RemoteCard card in _cards) card.Controls.IsEnabled = connected;
        SerialOption.IsEnabled = BluetoothOption.IsEnabled = PortPanel.IsEnabled = !connected;
        UpdateHints();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!IsConnected) return;
        e.Cancel = true;   // disconnect cleanly first (k 0), then close for real
        await DisconnectAsync();
        Close();
    }

    // ---------- Commands ----------

    private int StepsPerClickValue() =>
        StepsPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string tag ? int.Parse(tag) : 3;

    // Only the receivers on the page are kept locked: each one costs transmission time on the ESP32.
    // "m 0" (no digit) serves none, when the page only has a battery box.
    private string ServedReceivers()
    {
        string positions = string.Concat(_cards
            .Where(c => c.Settings.Kind == RemoteKind.GreyReceiver)
            .Select(c => c.Settings.Position));
        return positions.Length > 0 ? positions : "0";
    }

    private IEnumerable<ReceiverOutput> AllOutputs => _cards.SelectMany(c => c.Outputs);

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
        foreach (ReceiverOutput output in AllOutputs)
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

    /// <summary>Releases the held battery box button; <paramref name="letter"/> null = whichever is held.</summary>
    private async Task ReleaseBatteryBoxAsync(char? letter)
    {
        if (_heldBatteryBoxCommand is null) return;
        char held = _heldBatteryBoxCommand[1];
        _heldBatteryBoxCommand = null;
        _batteryBoxRepeatTimer.Stop();
        await SendAsync($"hx {letter ?? held}");
    }

    private async void OnStopAllClick(object sender, RoutedEventArgs e) => await StopAllAsync();

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

    // Keyboard shortcuts for the first receiver on the page: W or Z / S = output A, ↑ / ↓ = output B,
    // A and P = stops, Space = stop all
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsConnected || RawCommand.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Space)
        {
            await StopAllAsync();
            e.Handled = true;
            return;
        }

        RemoteCard? first = _cards.FirstOrDefault(c => c.Settings.Kind == RemoteKind.GreyReceiver);
        if (first is null) return;
        ReceiverOutput a = first.Outputs[0], b = first.Outputs[1];
        switch (e.Key)
        {
            case Key.W:
            case Key.Z: await StepAsync(a, 1); break;
            case Key.S: await StepAsync(a, -1); break;
            case Key.Up: await StepAsync(b, 1); e.Handled = true; break;
            case Key.Down: await StepAsync(b, -1); e.Handled = true; break;
            case Key.A: await StopAsync(a); break;
            case Key.P: await StopAsync(b); break;
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
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        Log.Items.Add(line);
        LastLogLine.Text = line;
        if (Log.Items.Count > MaxLogLines) Log.Items.RemoveAt(0);
        Log.ScrollIntoView(Log.Items[^1]);
    }

    // Answers to the keep-alive and to held battery box buttons would flood the log
    private static bool IsRepetitiveAnswer(string message) =>
        message == "← OK k 1" || (message.StartsWith("← OK h") && message.EndsWith(" 3"));

    private void ShowStatus(string text, Brush color)
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        StatusLight.Fill = color;
    }

    // Number + gauge: orange forward, blue backward, full at ~25 notches
    private void UpdateSpeedDisplay(ReceiverOutput output)
    {
        output.SpeedDisplay.Text = output.Speed > 0 ? "+" + output.Speed : output.Speed.ToString();
        output.Gauge.Width = GaugeWidth * Math.Min(1.0, Math.Abs(output.Speed) / (double)FullSpeedSteps);
        output.Gauge.Background = Res(output.Speed >= 0 ? "OrangeBrush" : "BlueBrush");
    }
}
