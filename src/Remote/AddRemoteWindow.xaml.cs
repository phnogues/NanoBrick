using System.Windows;
using System.Windows.Controls;

namespace Remote;

/// <summary>
/// Asks for the protocol of a new remote. A selector position can only be used once (one receiver per position),
/// and there is only one battery box: its frames carry no identifier, so every box would obey the same commands.
/// </summary>
public partial class AddRemoteWindow : Window
{
    private string _lastDefaultName = "";

    public RemoteSettings? Result { get; private set; }

    public AddRemoteWindow(IReadOnlyCollection<RemoteSettings> existing)
    {
        InitializeComponent();

        // Used positions are greyed out; the first free one is preselected
        foreach (RadioButton button in PositionButtons)
        {
            int position = int.Parse((string)button.Tag);
            bool used = existing.Any(r => r.Kind == RemoteKind.GreyReceiver && r.Position == position);
            button.IsEnabled = !used;
            if (used) button.ToolTip = "Already on the page";
        }
        RadioButton? firstFree = PositionButtons.FirstOrDefault(b => b.IsEnabled);
        if (firstFree is not null) firstFree.IsChecked = true;

        bool hasBatteryBox = existing.Any(r => r.Kind == RemoteKind.BatteryBox);
        GreyOption.IsEnabled = firstFree is not null;
        BatteryBoxOption.IsEnabled = !hasBatteryBox;
        if (!GreyOption.IsEnabled) BatteryBoxOption.IsChecked = true;

        if (firstFree is null) ShowHint("All 4 selector positions are already used.");
        else if (hasBatteryBox) ShowHint("There is already a battery box. Only one is possible: its radio frames carry no identifier, so every box would obey the same buttons.");

        UpdateState();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private IEnumerable<RadioButton> PositionButtons => PositionPanel.Children.OfType<RadioButton>();

    private RemoteKind SelectedKind => BatteryBoxOption.IsChecked == true ? RemoteKind.BatteryBox : RemoteKind.GreyReceiver;

    private int SelectedPosition =>
        PositionButtons.FirstOrDefault(b => b.IsChecked == true)?.Tag is string tag ? int.Parse(tag) : 1;

    private void ShowHint(string text)
    {
        HintText.Text = text;
        HintBox.Visibility = Visibility.Visible;
    }

    private void OnKindChanged(object sender, RoutedEventArgs e) => UpdateState();

    private void OnPositionChanged(object sender, RoutedEventArgs e) => UpdateState();

    // Suggests a name, unless the user already typed their own
    private void UpdateState()
    {
        if (NameBox is null || PositionRow is null) return;   // events raised during InitializeComponent

        PositionRow.Visibility = SelectedKind == RemoteKind.GreyReceiver ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = GreyOption.IsEnabled || BatteryBoxOption.IsEnabled;

        string defaultName = RemoteSettings.DefaultName(SelectedKind, SelectedPosition);
        if (NameBox.Text.Length == 0 || NameBox.Text == _lastDefaultName) NameBox.Text = defaultName;
        _lastDefaultName = defaultName;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        RemoteKind kind = SelectedKind;
        string name = NameBox.Text.Trim();
        Result = new RemoteSettings
        {
            Kind = kind,
            Position = kind == RemoteKind.GreyReceiver ? SelectedPosition : 0,
            Name = name.Length > 0 ? name : RemoteSettings.DefaultName(kind, SelectedPosition),
        };
        DialogResult = true;
    }
}
