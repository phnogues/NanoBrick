namespace Remote.Liaisons;

/// <summary>Une façon de parler à l'émetteur : des lignes de texte dans les deux sens.</summary>
public interface ILiaison : IAsyncDisposable
{
    /// <summary>Une ligne reçue de l'émetteur (« OK a+ 1 », « ETAT … »). Peut arriver sur un autre fil.</summary>
    event Action<string>? LigneRecue;

    /// <summary>La liaison s'est coupée toute seule (câble débranché, Bluetooth perdu).</summary>
    event Action? Perdue;

    string Description { get; }

    Task ConnecterAsync(CancellationToken annulation);

    Task EnvoyerAsync(string ligne);
}
