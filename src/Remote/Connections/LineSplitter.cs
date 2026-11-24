using System.Text;

namespace Remote.Connections;

/// <summary>Rebuilds text lines from data that arrives in arbitrary chunks.</summary>
internal sealed class LineSplitter
{
    private readonly StringBuilder _pending = new();

    /// <summary>Appends a chunk and returns the complete, non-empty lines it finished.</summary>
    public IEnumerable<string> Append(string chunk)
    {
        _pending.Append(chunk);
        string text = _pending.ToString();
        var lines = new List<string>();

        int end;
        while ((end = text.IndexOf('\n')) >= 0)
        {
            string line = text[..end].Trim();
            text = text[(end + 1)..];
            if (line.Length > 0) lines.Add(line);
        }

        _pending.Clear().Append(text);
        return lines;
    }
}
