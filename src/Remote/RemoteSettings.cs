using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remote;

/// <summary>The radio protocols the transmitter can imitate.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemoteKind
{
    /// <summary>Grey remote with 2 wheels (JM039): receiver with a 1–4 selector, outputs A and B, speed by steps.</summary>
    GreyReceiver,

    /// <summary>White remote: battery box with 4 outputs 1–4, on / off at full speed.</summary>
    BatteryBox,
}

/// <summary>One remote the user added to the page.</summary>
public sealed class RemoteSettings
{
    public RemoteKind Kind { get; set; }

    /// <summary>Selector position of the receiver (1 to 4), for <see cref="RemoteKind.GreyReceiver"/> only.</summary>
    public int Position { get; set; } = 1;

    /// <summary>Name shown on the card ("Red train", "Crane"…).</summary>
    public string Name { get; set; } = "";

    public static string DefaultName(RemoteKind kind, int position) => kind switch
    {
        RemoteKind.GreyReceiver => $"Receiver {position}",
        _ => "Battery box",
    };
}

/// <summary>Saves the list of remotes in %AppData%\nanoBrick\remotes.json.</summary>
public static class RemoteSettingsStore
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nanoBrick", "remotes.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<RemoteSettings> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<List<RemoteSettings>>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception)
        {
            return new();   // unreadable file: start from a blank page rather than crash
        }
    }

    public static void Save(IEnumerable<RemoteSettings> remotes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(remotes, Options));
    }
}
