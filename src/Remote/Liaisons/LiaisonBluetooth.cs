using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Remote.Liaisons;

/// <summary>
/// Bluetooth Low Energy : pour la version C# nanoFramework de l'émetteur, qui se présente sous le nom
/// « NanoBrick » avec le service série « Nordic UART ».
/// </summary>
public sealed class LiaisonBluetooth : ILiaison
{
    // Service « Nordic UART » : on écrit dans RX, l'émetteur répond par notifications sur TX
    private static readonly Guid ServiceUart = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid CaracRx = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid CaracTx = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    private readonly string _nom;
    private readonly StringBuilder _tampon = new();
    private BluetoothLEDevice? _appareil;
    private GattDeviceService? _service;
    private GattCharacteristic? _rx, _tx;
    private GattWriteOption _modeEcriture = GattWriteOption.WriteWithResponse;

    public event Action<string>? LigneRecue;
    public event Action? Perdue;

    public LiaisonBluetooth(string nom = "NanoBrick")
    {
        _nom = nom;
    }

    public string Description => $"Bluetooth (« {_nom} »)";

    public async Task ConnecterAsync(CancellationToken annulation)
    {
        ulong adresse = await ChercherAsync(annulation);

        _appareil = await BluetoothLEDevice.FromBluetoothAddressAsync(adresse)
                    ?? throw new InvalidOperationException("Impossible d'ouvrir l'appareil Bluetooth.");
        _appareil.ConnectionStatusChanged += (a, _) =>
        {
            if (a.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Perdue?.Invoke();
        };

        // Windows rate souvent la découverte des services juste après une déconnexion : on réessaie
        GattDeviceService? service = null;
        for (int essai = 0; essai < 5 && service is null; essai++)
        {
            annulation.ThrowIfCancellationRequested();
            if (essai > 0) await Task.Delay(1000, annulation);
            var services = await _appareil.GetGattServicesForUuidAsync(
                ServiceUart, essai % 2 == 0 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached);
            if (services.Status == GattCommunicationStatus.Success) service = services.Services.FirstOrDefault();
        }
        if (service is null)
            throw new InvalidOperationException("Service « Nordic UART » introuvable sur l'émetteur (5 essais).");
        _service = service;

        _rx = (await service.GetCharacteristicsForUuidAsync(CaracRx)).Characteristics.FirstOrDefault()
              ?? throw new InvalidOperationException("Caractéristique RX introuvable.");
        _tx = (await service.GetCharacteristicsForUuidAsync(CaracTx)).Characteristics.FirstOrDefault()
              ?? throw new InvalidOperationException("Caractéristique TX introuvable.");

        // Écriture sans réponse dès que possible : avec réponse, l'ESP32 (nanoFramework) renvoie parfois
        // l'erreur 0x8065000E alors que l'ordre est bien arrivé. La couche radio Bluetooth acquitte quand même.
        if (_rx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
            _modeEcriture = GattWriteOption.WriteWithoutResponse;

        _tx.ValueChanged += SurNotification;
        var etat = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (etat != GattCommunicationStatus.Success)
            throw new InvalidOperationException("L'émetteur refuse les notifications : " + etat);
    }

    // Écoute les annonces Bluetooth jusqu'à trouver l'appareil qui porte le bon nom (15 s au plus).
    private async Task<ulong> ChercherAsync(CancellationToken annulation)
    {
        var trouve = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guetteur = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        guetteur.Received += (_, annonce) =>
        {
            if (annonce.Advertisement.LocalName == _nom) trouve.TrySetResult(annonce.BluetoothAddress);
        };
        guetteur.Start();
        try
        {
            using var delai = CancellationTokenSource.CreateLinkedTokenSource(annulation);
            delai.CancelAfter(TimeSpan.FromSeconds(15));
            using (delai.Token.Register(() => trouve.TrySetCanceled()))
            {
                try
                {
                    return await trouve.Task;
                }
                catch (TaskCanceledException) when (!annulation.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Aucun appareil « {_nom} » trouvé en 15 s. L'ESP32 a-t-il le programme C# et le Bluetooth du PC est-il allumé ?");
                }
            }
        }
        finally
        {
            guetteur.Stop();
        }
    }

    public async Task EnvoyerAsync(string ligne)
    {
        if (_rx is null) return;
        var ecrivain = new DataWriter();
        ecrivain.WriteBytes(Encoding.ASCII.GetBytes(ligne + "\n"));
        var etat = await _rx.WriteValueAsync(ecrivain.DetachBuffer(), _modeEcriture);
        if (etat != GattCommunicationStatus.Success) Perdue?.Invoke();
    }

    private void SurNotification(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        var lecteur = DataReader.FromBuffer(e.CharacteristicValue);
        var octets = new byte[lecteur.UnconsumedBufferLength];
        lecteur.ReadBytes(octets);

        _tampon.Append(Encoding.ASCII.GetString(octets));
        string texte = _tampon.ToString();
        int fin;
        while ((fin = texte.IndexOf('\n')) >= 0)
        {
            string l = texte[..fin].Trim();
            texte = texte[(fin + 1)..];
            if (l.Length > 0) LigneRecue?.Invoke(l);
        }
        _tampon.Clear().Append(texte);
    }

    // Libère tout explicitement : sinon Windows garde la liaison ouverte jusqu'au passage du ramasse-miettes,
    // l'ESP32 se croit encore connecté et la connexion suivante échoue ou traîne.
    public async ValueTask DisposeAsync()
    {
        if (_tx is not null)
        {
            _tx.ValueChanged -= SurNotification;
            try
            {
                await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { /* déjà déconnecté */ }
        }
        _service?.Session?.Dispose();
        _service?.Dispose();
        _appareil?.Dispose();
        _service = null;
        _appareil = null;
        _rx = _tx = null;
    }
}
