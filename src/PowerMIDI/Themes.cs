using System.Windows;
using System.Windows.Media;
using PowerMIDI.Core;

namespace PowerMIDI;

// Color themes: one resource dictionary per theme (the same brush keys in each), merged first in the application
// resources. The window uses the brushes as dynamic resources, so that it follows a change at once.
// - Built-in themes: Themes\<name>.xaml of the project, compiled into the program (always available).
// - "themes" folder next to the program: a file named like a built-in theme replaces its colors, any other file adds
//   a theme. Only colors are read from these files (see ThemeFile); what a file does not give comes from the
//   built-in theme of the same name, otherwise from the default one.
public static class Themes
{
    static readonly string[] BuiltIn = { "Dark", "Light", "Blue", "Red", "Green", "SC-8850" };
    public const string Default = "Blue";

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "themes");

    // Shown in the About tab and written in PowerMIDI.ini: the built-in themes, then those of the themes folder.
    public static IReadOnlyList<string> Names =>
        BuiltIn.Concat(ThemeFile.GetNames(Folder).Where(n => FindBuiltIn(n) == null)).ToList();

    public static string Current { get; private set; } = Default;

    static string FindBuiltIn(string name) => BuiltIn.FirstOrDefault(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase));

    // Applies a theme; an unknown name (e.g. from an edited PowerMIDI.ini, or a file removed since) gives the default one.
    public static void Apply(string name)
    {
        name = Names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? Default;
        var dictionary = new ResourceDictionary { Source = new Uri($"/PowerMIDI;component/Themes/{FindBuiltIn(name) ?? Default}.xaml", UriKind.Relative) };
        foreach (var (key, text) in ThemeFile.ReadColors(Path.Combine(Folder, name + ".xaml")))
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
                brush.Freeze();
                dictionary[key] = brush;
            }
            catch { }   // not a color: the built-in one is kept
        }
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dictionary; else merged.Add(dictionary);
        Current = name;
    }
}
