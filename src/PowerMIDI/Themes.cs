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

    // Light controls (About tab, PowerMIDI.ini; off by default): the drop-down lists of the tabs (right panel) and of the
    // Event Editor drawn light, as Windows draws them, whatever the theme (otherwise in the colors of the theme); the rest
    // of the windows keeps the colors of the theme. ApplyControls then puts LightControlBrushes in the resources of the
    // tabs and of the Event Editor (they are found there before those of the theme).
    public static bool LightControls { get; set; }

    // Icons before the instrument of each channel in the Mixer (PowerMIDI.ini only, Window/InstrumentIcons; on by
    // default). Never with the light controls.
    public static bool InstrumentIcons { get; set; } = true;

    // Colors of the controls (Styles.xaml): those of the theme (set with it), or the light ones (those of the Windows
    // lists). Background, border, text, under the mouse, selected or pressed, arrow and disabled text, then the state
    // colors (value set by hand, sound missing, set by a SysEx), the text of a disabled list, then the open list:
    // background, border, item under the mouse and its frame, item selected and its frame (frames: light ones only, none
    // with the theme), and the background of the search boxes. A disabled list is dimmed with the colors of the theme
    // (ControlDisabledOpacity), kept light otherwise.
    static readonly string[] ControlKeys =
        { "ControlBrush", "ControlLineBrush", "ControlTextBrush", "ControlHoverBrush", "ControlSelectedBrush", "ControlMutedBrush",
          "ControlFixedBrush", "ControlMissingBrush", "ControlSysExBrush", "ControlDisabledTextBrush",
          "ControlPopupBrush", "ControlPopupLineBrush", "ControlItemHoverBrush", "ControlItemHoverLineBrush", "ControlItemSelectedBrush",
          "ControlItemSelectedLineBrush", "ControlFieldBrush" };
    // Of the theme (null: none).
    static readonly string[] ThemeControlKeys =
        { "ButtonBrush", "LineBrush", "TextBrush", "TabHoverBrush", "TabSelectedBrush", "TextMutedBrush", "FixedBrush", "MuteBrush", "GreenBrush", "TextBrush",
          "ButtonBrush", "LineBrush", "TabHoverBrush", null, "TabSelectedBrush", null, "ButtonBrush" };
    static readonly string[] LightControlColors =
        { "#FFEDEDED", "#FFACACAC", "#FF1E1E1E", "#FFE5F1FB", "#FFCCE4F7", "#FF606060", "#FF1F5FBF", "#FFC62828", "#FF2E7D32", "#FF8A8A8A",
          "#FFFFFFFF", "#FF646464", "#FFB8E0F3", "#FF26A0DA", "#FFF6F6F6", "#FFDADADA", "#FFFFFFFF" };

    static void SetControlColors(ResourceDictionary dictionary)
    {
        for (int i = 0; i < ControlKeys.Length; i++)
            dictionary[ControlKeys[i]] = ThemeControlKeys[i] is { } key ? dictionary[key] : Brushes.Transparent;
        dictionary["ControlDisabledOpacity"] = 0.5;
        dictionary["ThemedSliders"] = true;   // sliders in the colors of the theme (Styles.xaml, ThemedSlider)
        // Fill of these sliders: white on the dark themes (light text), black on the light ones.
        var text = (dictionary["TextBrush"] as SolidColorBrush)?.Color ?? Colors.White;
        dictionary["SliderFillBrush"] = 0.299 * text.R + 0.587 * text.G + 0.114 * text.B >= 128 ? Brushes.White : Brushes.Black;
        // Instrument list of the Mixer: its sound / drum kit icon, before the name (not with the light controls nor with
        // InstrumentIcons off: NoInstrumentIcons).
        dictionary["InstrumentIconsVisibility"] = Visibility.Visible;
        dictionary["InstrumentTextMargin"] = new Thickness(22, 0, 20, 0);
    }

    static ResourceDictionary lightControlBrushes;

    static ResourceDictionary LightControlBrushes()
    {
        if (lightControlBrushes != null) return lightControlBrushes;
        var dictionary = new ResourceDictionary();
        for (int i = 0; i < ControlKeys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(LightControlColors[i]));
            brush.Freeze();
            dictionary[ControlKeys[i]] = brush;
        }
        dictionary["ControlDisabledOpacity"] = 1.0;
        dictionary["ThemedSliders"] = false;   // the sliders of Windows
        foreach (System.Collections.DictionaryEntry entry in NoInstrumentIcons()) dictionary[entry.Key] = entry.Value;
        return lightControlBrushes = dictionary;
    }

    static ResourceDictionary noInstrumentIcons;

    // The instrument names of the Mixer without their icons.
    static ResourceDictionary NoInstrumentIcons() => noInstrumentIcons ??= new ResourceDictionary
    {
        ["InstrumentIconsVisibility"] = Visibility.Collapsed,
        ["InstrumentTextMargin"] = new Thickness(6, 0, 20, 0),
    };

    // Puts the light colors of the controls in these resources (tabs of the main window, Event Editor), or takes them
    // away without LightControls; the same for the instrument icons hidden (InstrumentIcons). The other merged
    // dictionaries (e.g. Styles.xaml) are kept.
    public static void ApplyControls(ResourceDictionary resources)
    {
        var merged = resources.MergedDictionaries;
        merged.Remove(LightControlBrushes());
        merged.Remove(NoInstrumentIcons());
        if (LightControls) merged.Add(LightControlBrushes());
        if (!InstrumentIcons) merged.Add(NoInstrumentIcons());
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
