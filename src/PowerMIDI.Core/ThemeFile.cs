using System.Xml.Linq;

namespace PowerMIDI.Core;

// Color theme file (themes\<name>.xaml next to the program): a list of <SolidColorBrush x:Key="…" Color="…"/>.
// Read as plain XML, not loaded as XAML: a theme file can only give colors, nothing in it is ever executed.
public static class ThemeFile
{
    static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Names of the themes found in a folder (file names without extension), sorted; empty if the folder is missing.
    public static IReadOnlyList<string> GetNames(string folder)
    {
        try
        {
            return Directory.GetFiles(folder, "*.xaml").Select(Path.GetFileNameWithoutExtension)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    // Colors of a theme file by brush key (text as written, e.g. "#1E1F24"). Empty if the file is missing or not
    // valid XML; other elements, and brushes without a key or a color, are ignored.
    public static Dictionary<string, string> ReadColors(string path)
    {
        var colors = new Dictionary<string, string>();
        try
        {
            foreach (var e in XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "SolidColorBrush"))
            {
                string key = (string)e.Attribute(Xaml + "Key"), color = (string)e.Attribute("Color");
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(color)) colors[key.Trim()] = color.Trim();
            }
        }
        catch { colors.Clear(); }
        return colors;
    }
}
