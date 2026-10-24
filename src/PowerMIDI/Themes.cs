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
    static readonly string[] BuiltIn = { "Dark", "Light", "Blue", "Teal", "Red", "Green", "SC-8850" };
    public const string Default = "Teal";

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "themes");

    // Shown in the About tab and written in PowerMIDI.ini: the built-in themes, then those of the themes folder.
    public static IReadOnlyList<string> Names =>
        BuiltIn.Concat(ThemeFile.GetNames(Folder).Where(n => FindBuiltIn(n) == null)).ToList();

    public static string Current { get; private set; } = Default;

    // Dark controls (About tab, PowerMIDI.ini): the drop-down lists of the tabs (right panel) in the colors of the theme.
    // Off by default: they are drawn light, as Windows draws them, whatever the theme; the rest of the window keeps the
    // colors of the theme. The window then puts LightControlBrushes in the resources of its tabs (they are found there
    // before those of the theme).
    public static bool DarkControls { get; set; }

    // Colors of the controls (Styles.xaml): those of the theme (set with it), or the light ones. Background, border,
    // text, under the mouse, selected or pressed, arrow and disabled text, then the state colors (value set by hand,
    // sound missing, set by a SysEx), and the text of a disabled list. A disabled list is dimmed with the colors of the
    // theme (ControlDisabledOpacity), kept light otherwise.
    static readonly string[] ControlKeys =
        { "ControlBrush", "ControlLineBrush", "ControlTextBrush", "ControlHoverBrush", "ControlSelectedBrush", "ControlMutedBrush",
          "ControlFixedBrush", "ControlMissingBrush", "ControlSysExBrush", "ControlDisabledTextBrush" };
    static readonly string[] ThemeControlKeys =
        { "ButtonBrush", "LineBrush", "TextBrush", "TabHoverBrush", "TabSelectedBrush", "TextMutedBrush", "FixedBrush", "MuteBrush", "GreenBrush", "TextBrush" };
    static readonly string[] LightControlColors =
        { "#FFEDEDED", "#FFACACAC", "#FF1E1E1E", "#FFE5F1FB", "#FFCCE4F7", "#FF606060", "#FF1F5FBF", "#FFC62828", "#FF2E7D32", "#FF8A8A8A" };

    static void SetControlColors(ResourceDictionary dictionary)
    {
        for (int i = 0; i < ControlKeys.Length; i++) dictionary[ControlKeys[i]] = dictionary[ThemeControlKeys[i]];
        dictionary["ControlDisabledOpacity"] = 0.5;
    }

    public static ResourceDictionary LightControlBrushes()
    {
        var dictionary = new ResourceDictionary();
        for (int i = 0; i < ControlKeys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(LightControlColors[i]));
            brush.Freeze();
            dictionary[ControlKeys[i]] = brush;
        }
        dictionary["ControlDisabledOpacity"] = 1.0;
        return dictionary;
    }

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
        SetControlColors(dictionary);
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dictionary; else merged.Add(dictionary);
        Current = name;
    }
}
