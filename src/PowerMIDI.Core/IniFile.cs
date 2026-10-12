using System.Text;

namespace PowerMIDI.Core;

// INI settings file. Format: [Section] then key=value; lines starting with ; or # are comments.
// Sections and keys are case-insensitive; sections are written in the order they were added.
public sealed class IniFile
{
    readonly List<(string Name, Dictionary<string, string> Values)> sections = new();

    public static IniFile Read(string path)
    {
        var ini = new IniFile();
        if (!File.Exists(path)) return ini;
        string section = "";
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq < 1) continue;
            ini.Set(section, line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        return ini;
    }

    Dictionary<string, string> Section(string name, bool create)
    {
        foreach (var s in sections)
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s.Values;
        if (!create) return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        sections.Add((name, values));
        return values;
    }

    public void Set(string section, string key, object value) => Section(section, true)[key] = Convert.ToString(value) ?? "";

    public string Get(string section, string key, string defaultValue = null) =>
        Section(section, false) is { } s && s.TryGetValue(key, out var v) ? v : defaultValue;

    public int? GetInt(string section, string key) => int.TryParse(Get(section, key), out var n) ? n : null;

    // Written in UTF-8 with BOM.
    public void Write(string path, string header = null)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(header))
        {
            foreach (var h in header.Split('\n')) lines.Add("; " + h);
            lines.Add("");
        }
        foreach (var (name, values) in sections)
        {
            lines.Add($"[{name}]");
            foreach (var kv in values) lines.Add($"{kv.Key}={kv.Value}");
            lines.Add("");
        }
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
    }
}
