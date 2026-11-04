using System.Xml.Linq;

namespace PowerMIDI.Core;

// Color theme file (themes\<name>.xaml next to the program): a list of <SolidColorBrush x:Key="…" Color="…"/>, and the
// numbers of the background image (<sys:Double x:Key="…">). Read as plain XML, not loaded as XAML: a theme file can only
// give colors and numbers, nothing in it is ever executed.
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

    // Numbers of a theme file by key (<sys:Double x:Key="…">55</sys:Double>, e.g. BackgroundTint). Empty if the file is
    // missing or not valid XML; values that are not numbers (or not finite ones) are ignored.
    public static Dictionary<string, double> ReadNumbers(string path)
    {
        var numbers = new Dictionary<string, double>();
        try
        {
            foreach (var e in XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "Double"))
            {
                var key = (string)e.Attribute(Xaml + "Key");
                if (!string.IsNullOrWhiteSpace(key) &&
                    double.TryParse(e.Value.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) &&
                    double.IsFinite(value))   // not NaN / Infinity, which TryParse accepts
                    numbers[key.Trim()] = value;
            }
        }
        catch { numbers.Clear(); }
        return numbers;
    }
}
