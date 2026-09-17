using System.IO;
using System.Text;

namespace JBZUniveresalLunix.Services;

/// <summary>Legacy .setup is local configuration, never a UART command stream.</summary>
public sealed record JbzSetupProfile(
    string SourcePath,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Sections)
{
    public string? Get(string section, string key) =>
        Sections.TryGetValue(section, out var values) && values.TryGetValue(key, out string? value)
            ? value : null;
}

public static class JbzSetupParser
{
    public static string? FindForModel(string modelPath)
    {
        string full = Path.GetFullPath(modelPath);
        string name = Path.GetFileNameWithoutExtension(full) + ".setup";
        string adjacent = Path.Combine(Path.GetDirectoryName(full)!, name);
        if (File.Exists(adjacent)) return adjacent;
        string? parent = Directory.GetParent(Path.GetDirectoryName(full)!)?.FullName;
        if (parent is not null)
        {
            foreach (string folder in new[] { "Setups", "setup" })
            {
                string sibling = Path.Combine(parent, folder, name);
                if (File.Exists(sibling)) return sibling;
            }
        }
        return null;
    }

    public static JbzSetupProfile Load(string setupPath, string modelPath)
    {
        string full = Path.GetFullPath(setupPath);
        if (!Path.GetExtension(full).Equals(".setup", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected a .setup configuration file.");
        if (!Path.GetFileNameWithoutExtension(full).Equals(
                Path.GetFileNameWithoutExtension(modelPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(".model and .setup part numbers do not match.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text = File.ReadAllText(full, Encoding.GetEncoding(949));
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string name = line[1..^1].Trim();
                if (name.Length == 0 || sections.ContainsKey(name))
                    throw new InvalidDataException("Duplicate or empty .setup section.");
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add(name, current);
                continue;
            }
            int equals = line.IndexOf('=');
            if (current is null || equals <= 0)
                throw new InvalidDataException("Invalid .setup INI entry.");
            current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        var profile = new JbzSetupProfile(full, sections);
        string? declaredModel = profile.Get("Common", "Model");
        if (string.IsNullOrWhiteSpace(declaredModel) ||
            !Path.GetFileNameWithoutExtension(declaredModel.Replace('\\', '/'))
                .Equals(Path.GetFileNameWithoutExtension(modelPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(".setup [Common] Model does not match .model.");
        return profile;
    }
}
