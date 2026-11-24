using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Remote.Liaisons;

namespace Remote;

/// <summary>
/// Pilote jusqu'à 4 récepteurs (sélecteur 1 à 4) × 2 sorties (A, B) = 8 moteurs, avec les mêmes ordres
/// texte que la page tools/JM039TX/remote.html : « 2a+ 3 » (3 crans, récepteur 2, sortie A), « 4sb » (stop),
/// « x » (tout arrêter), « m 12 » (récepteurs présents), « k 1 » (maintien), « p 0 », « f ».
/// Plus le boîtier batterie 4 sorties (télécommande blanche) : tout ou rien, la sortie tourne tant qu'on
/// maintient le bouton (« hd+ 3 » répété, puis « hx d » au relâchement).
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _maintien = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, Sortie> _sorties = new();   // "1a" … "4b"
    private ILiaison? _liaison;
    private CancellationTokenSource? _annulation;

    // Boîtier : tant qu'un bouton est maintenu, l'ordre (300 ms) est renvoyé toutes les 200 ms
    private readonly DispatcherTimer _repetitionBoitier = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string? _ordreBoitier;    // « hd+ 3 » en cours, ou null

    /// <summary>Une sortie d'un récepteur : un moteur.</summary>
    private sealed class Sortie
    {
        public required int Recepteur { get; init; }
        public required char Lettre { get; init; }
        public required TextBlock Affichage { get; init; }
        public required CheckBox Inverser { get; init; }
        public int Vitesse { get; set; }
        public string Cle => $"{Recepteur}{Lettre}";
    }

    public MainWindow()
    {
        InitializeComponent();
        RemplirPorts();
        for (int r = 1; r <= 4; r++) Recepteurs.Children.Add(CreerRecepteur(r));
        foreach (char lettre in "abcd") Boitier.Children.Add(CreerSortieBoitier(lettre));
        _repetitionBoitier.Tick += async (_, _) =>
        {
            if (_ordreBoitier is not null) await EnvoyerAsync(_ordreBoitier, journaliser: false);
        };
        // Tant qu'on est connecté, l'émetteur garde les récepteurs « accrochés » (lumière fixe)
        _maintien.Tick += async (_, _) => await EnvoyerAsync("k 1", journaliser: false);
    }

    // ---------- Construction de l'IHM ----------

    private GroupBox CreerRecepteur(int r)
    {
        var pile = new StackPanel();
        foreach (char lettre in "ab") pile.Children.Add(CreerSortie(r, lettre));
        return new GroupBox
        {
            Header = new TextBlock { Text = $"Récepteur {r}  (sélecteur sur {r})", FontWeight = FontWeights.SemiBold },
            Content = pile,
            Background = (Brush)FindResource("Gris"),
        };
    }

    private DockPanel CreerSortie(int r, char lettre)
    {
        var affichage = new TextBlock
        {
            Text = "0", Width = 52, FontSize = 24, FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var inverser = new CheckBox { Content = "⟲", ToolTip = "Inverser le sens", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var sortie = new Sortie { Recepteur = r, Lettre = lettre, Affichage = affichage, Inverser = inverser };
        _sorties[sortie.Cle] = sortie;

        Button Bouton(string texte, string style, RoutedEventHandler clic, string aide)
        {
            var b = new Button { Content = texte, Style = (Style)FindResource(style), ToolTip = aide };
            b.Click += clic;
            return b;
        }

        var ligne = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var titre = new TextBlock { Text = char.ToUpper(lettre).ToString(), FontSize = 20, FontWeight = FontWeights.Bold, Width = 24, VerticalAlignment = VerticalAlignment.Center };
        var stop = Bouton("STOP", "Stop", async (_, _) => await StopAsync(sortie), $"Stop récepteur {r}, sortie {char.ToUpper(lettre)}");
        stop.Width = 80;
        DockPanel.SetDock(stop, Dock.Right);
        DockPanel.SetDock(inverser, Dock.Right);
        ligne.Children.Add(titre);
        ligne.Children.Add(stop);
        ligne.Children.Add(inverser);
        var centre = new StackPanel { Orientation = Orientation.Horizontal };
        centre.Children.Add(Bouton("−", "Molette", async (_, _) => await CransAsync(sortie, -1), "Moins vite / arrière"));
        centre.Children.Add(affichage);
        centre.Children.Add(Bouton("+", "Molette", async (_, _) => await CransAsync(sortie, 1), "Plus vite / avant"));
        ligne.Children.Add(centre);
        return ligne;
    }

    // Une sortie du boîtier : ◀ et ▶ à maintenir enfoncés (comme la télécommande blanche)
    private StackPanel CreerSortieBoitier(char lettre)
    {
        var ligne = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4) };
        Button Bouton(string texte, char sens)
        {
            var b = new Button { Content = texte, Style = (Style)FindResource("Molette"), ToolTip = $"Sortie {char.ToUpper(lettre)} : maintenir pour tourner" };
            b.PreviewMouseLeftButtonDown += async (_, _) => await BoitierAppuyerAsync($"h{lettre}{sens} 3");
            b.PreviewMouseLeftButtonUp += async (_, _) => await BoitierRelacherAsync(lettre);
            b.MouseLeave += async (_, _) => { if (_ordreBoitier?[1] == lettre) await BoitierRelacherAsync(lettre); };
            return b;
        }
        ligne.Children.Add(Bouton("◀", '-'));
        ligne.Children.Add(new TextBlock { Text = char.ToUpper(lettre).ToString(), FontSize = 22, FontWeight = FontWeights.Bold, Width = 32, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        ligne.Children.Add(Bouton("▶", '+'));
        return ligne;
    }

    private async Task BoitierAppuyerAsync(string ordre)
    {
        if (_liaison is null) return;
        _ordreBoitier = ordre;
        await EnvoyerAsync(ordre);
        _repetitionBoitier.Start();
    }

    private async Task BoitierRelacherAsync(char lettre)
    {
        if (_ordreBoitier is null) return;
        _ordreBoitier = null;
        _repetitionBoitier.Stop();
        await EnvoyerAsync($"hx {lettre}");
    }

    // ---------- Connexion ----------

    private void RemplirPorts()
    {
        string[] ports = LiaisonSerie.PortsDisponibles();
        ListePorts.ItemsSource = ports;
        ListePorts.SelectedItem = ports.Contains("COM5") ? "COM5" : ports.LastOrDefault();
    }

    private void SurActualiserPorts(object sender, RoutedEventArgs e) => RemplirPorts();

    private async void SurConnexion(object sender, RoutedEventArgs e)
    {
        if (_liaison is not null)
        {
            await DeconnecterAsync();
            return;
        }

        ILiaison liaison;
        if (ChoixSerie.IsChecked == true)
        {
            if (ListePorts.SelectedItem is not string port)
            {
                AfficherEtat("Choisis un port COM", Brushes.OrangeRed);
                return;
            }
            liaison = new LiaisonSerie(port);
        }
        else
        {
            liaison = new LiaisonBluetooth();
        }

        BoutonConnexion.IsEnabled = false;
        AfficherEtat($"Connexion par {liaison.Description}…", Brushes.Orange);
        _annulation = new CancellationTokenSource();
        try
        {
            liaison.LigneRecue += ligne => Dispatcher.BeginInvoke(() => Journaliser("← " + ligne));
            liaison.Perdue += () => Dispatcher.BeginInvoke(async () =>
            {
                if (_liaison == liaison)
                {
                    Journaliser("Liaison perdue");
                    await DeconnecterAsync();
                }
            });
            await liaison.ConnecterAsync(_annulation.Token);
            _liaison = liaison;
        }
        catch (Exception ex)
        {
            await liaison.DisposeAsync();
            AfficherEtat("Échec : " + ex.Message, Brushes.Red);
            Journaliser("Échec de connexion : " + ex.Message);
            BoutonConnexion.IsEnabled = true;
            return;
        }

        BoutonConnexion.Content = "Déconnecter";
        BoutonConnexion.IsEnabled = true;
        Recepteurs.IsEnabled = Reglages.IsEnabled = Boitier.IsEnabled = true;
        ChoixSerie.IsEnabled = ChoixBluetooth.IsEnabled = ListePorts.IsEnabled = false;
        AfficherEtat($"Connecté par {liaison.Description}", Brushes.ForestGreen);

        await EnvoyerAsync("m " + RecepteursServis());
        await EnvoyerAsync("p " + Puissance.SelectedIndex);
        await EnvoyerAsync("k 1");
        _maintien.Start();
    }

    private async Task DeconnecterAsync()
    {
        _maintien.Stop();
        _repetitionBoitier.Stop();
        _ordreBoitier = null;
        ILiaison? liaison = _liaison;
        _liaison = null;
        if (liaison is not null)
        {
            try { await liaison.EnvoyerAsync("k 0"); } catch { /* déjà coupée */ }
            await liaison.DisposeAsync();
        }
        _annulation?.Cancel();

        BoutonConnexion.Content = "Connecter";
        BoutonConnexion.IsEnabled = true;
        Recepteurs.IsEnabled = Reglages.IsEnabled = Boitier.IsEnabled = false;
        ChoixSerie.IsEnabled = ChoixBluetooth.IsEnabled = ListePorts.IsEnabled = true;
        AfficherEtat("Non connecté", (Brush)FindResource("GrisFonce"));
    }

    private async void SurFermeture(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_liaison is null) return;
        e.Cancel = true;            // on rend d'abord la main aux vraies télécommandes (k 0)
        await DeconnecterAsync();
        Close();
    }

    // ---------- Commandes ----------

    private int CransParAppui() => int.Parse((string)((ComboBoxItem)Pas.SelectedItem).Content);

    private string RecepteursServis() => (string)((ComboBoxItem)Servis.SelectedItem).Tag;

    private async Task CransAsync(Sortie s, int sens)
    {
        if (_liaison is null) return;
        int n = CransParAppui();
        int sensReel = s.Inverser.IsChecked == true ? -sens : sens;
        await EnvoyerAsync($"{s.Cle}{(sensReel > 0 ? "+" : "-")} {n}");
        s.Vitesse += sens * n;   // le récepteur ne renvoie rien : on estime en comptant les crans
        Afficher(s);
    }

    private async Task StopAsync(Sortie s)
    {
        if (_liaison is null) return;
        await EnvoyerAsync($"{s.Recepteur}s{s.Lettre} 3");
        s.Vitesse = 0;
        Afficher(s);
    }

    private async Task ToutArreterAsync()
    {
        if (_liaison is null) return;
        _repetitionBoitier.Stop();
        _ordreBoitier = null;
        await EnvoyerAsync("x");
        foreach (Sortie s in _sorties.Values)
        {
            s.Vitesse = 0;
            Afficher(s);
        }
    }

    private async void SurToutArreter(object sender, RoutedEventArgs e) => await ToutArreterAsync();

    private async void SurServis(object sender, SelectionChangedEventArgs e)
    {
        if (_liaison is not null) await EnvoyerAsync("m " + RecepteursServis());
    }

    private async void SurPuissance(object sender, SelectionChangedEventArgs e)
    {
        if (_liaison is not null) await EnvoyerAsync("p " + Puissance.SelectedIndex);
    }

    private async void SurEtat(object sender, RoutedEventArgs e) => await EnvoyerAsync("f");

    private async void SurOrdreLibre(object sender, RoutedEventArgs e)
    {
        string ordre = OrdreLibre.Text.Trim();
        if (ordre.Length == 0 || _liaison is null) return;
        await EnvoyerAsync(ordre);
        OrdreLibre.Clear();
    }

    // Clavier, comme la page web (récepteur 1) : Z/S sortie A, ↑/↓ sortie B, A et P stops, Espace tout arrêter
    private async void SurTouche(object sender, KeyEventArgs e)
    {
        if (_liaison is null || OrdreLibre.IsKeyboardFocusWithin) return;
        switch (e.Key)
        {
            case Key.Z: await CransAsync(_sorties["1a"], 1); break;
            case Key.S: await CransAsync(_sorties["1a"], -1); break;
            case Key.Up: await CransAsync(_sorties["1b"], 1); e.Handled = true; break;
            case Key.Down: await CransAsync(_sorties["1b"], -1); e.Handled = true; break;
            case Key.A: await StopAsync(_sorties["1a"]); break;
            case Key.P: await StopAsync(_sorties["1b"]); break;
            case Key.Space: await ToutArreterAsync(); e.Handled = true; break;
        }
    }

    // ---------- Outils ----------

    private async Task EnvoyerAsync(string ordre, bool journaliser = true)
    {
        if (_liaison is null) return;
        if (journaliser) Journaliser("→ " + ordre);
        try
        {
            await _liaison.EnvoyerAsync(ordre);
        }
        catch (Exception ex)
        {
            Journaliser("Erreur d'envoi : " + ex.Message);
        }
    }

    private void Journaliser(string message)
    {
        // Les réponses au maintien encombrent le journal : on les masque
        if (message.StartsWith("← OK") && (message.EndsWith("k 1") || message.EndsWith(" 3") && message.Contains("OK h"))) return;
        Journal.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (Journal.Items.Count > 300) Journal.Items.RemoveAt(0);
        Journal.ScrollIntoView(Journal.Items[^1]);
    }

    private void AfficherEtat(string texte, Brush couleur)
    {
        Etat.Text = texte;
        Voyant.Fill = couleur;
    }

    private static void Afficher(Sortie s) => s.Affichage.Text = s.Vitesse > 0 ? "+" + s.Vitesse : s.Vitesse.ToString();
}
