using System.IO.Ports;
using System.Text;

namespace Remote.Liaisons;

/// <summary>
/// Câble USB : pour la version Arduino de l'émetteur (JM039TX.ino), 115200 bauds, une ligne par ordre.
/// DTR et RTS restent bas pour ne pas redémarrer l'ESP32 à l'ouverture.
/// </summary>
public sealed class LiaisonSerie : ILiaison
{
    private readonly SerialPort _port;
    private readonly StringBuilder _tampon = new();

    public event Action<string>? LigneRecue;
    public event Action? Perdue;

    public LiaisonSerie(string nomPort)
    {
        _port = new SerialPort(nomPort, 115200)
        {
            DtrEnable = false,
            RtsEnable = false,
            NewLine = "\n",
            Encoding = Encoding.ASCII,
        };
        _port.DataReceived += SurDonnees;
        _port.ErrorReceived += (_, _) => Perdue?.Invoke();
    }

    public string Description => $"câble USB ({_port.PortName})";

    public static string[] PortsDisponibles() => SerialPort.GetPortNames().OrderBy(p => p).ToArray();

    public Task ConnecterAsync(CancellationToken annulation)
    {
        _port.Open();  // lève UnauthorizedAccessException si le port est déjà pris (IDE Arduino, page web…)
        return Task.CompletedTask;
    }

    public Task EnvoyerAsync(string ligne)
    {
        try
        {
            _port.Write(ligne + "\n");
        }
        catch (Exception) when (!_port.IsOpen)
        {
            Perdue?.Invoke();
        }
        return Task.CompletedTask;
    }

    private void SurDonnees(object sender, SerialDataReceivedEventArgs e)
    {
        string recu;
        try
        {
            recu = _port.ReadExisting();
        }
        catch (Exception)
        {
            Perdue?.Invoke();
            return;
        }

        _tampon.Append(recu);
        string texte = _tampon.ToString();
        int fin;
        while ((fin = texte.IndexOf('\n')) >= 0)
        {
            string ligne = texte[..fin].Trim();
            texte = texte[(fin + 1)..];
            if (ligne.Length > 0) LigneRecue?.Invoke(ligne);
        }
        _tampon.Clear().Append(texte);
    }

    public ValueTask DisposeAsync()
    {
        _port.DataReceived -= SurDonnees;
        if (_port.IsOpen) _port.Close();
        _port.Dispose();
        return ValueTask.CompletedTask;
    }
}
