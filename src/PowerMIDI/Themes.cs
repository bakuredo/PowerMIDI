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
    // colors (sound missing, set by a SysEx), the text of a disabled list, then the open list:
    // background, border, item under the mouse and its frame, item selected and its frame (frames: light ones only, none
    // with the theme), the background of the search boxes, the instrument or value chosen by hand (the accent
    // color with the theme, blue with the light controls), then the thumb of a slider under the mouse and its frame (the
    // accent color with the theme, the light blue of Windows with the light controls). A disabled
    // list is dimmed with the colors of the theme (ControlDisabledOpacity), kept light otherwise.
    static readonly string[] ControlKeys =
        { "ControlBrush", "ControlLineBrush", "ControlTextBrush", "ControlHoverBrush", "ControlSelectedBrush", "ControlMutedBrush",
          "ControlMissingBrush", "ControlSysExBrush", "ControlDisabledTextBrush",
          "ControlPopupBrush", "ControlPopupLineBrush", "ControlItemHoverBrush", "ControlItemHoverLineBrush", "ControlItemSelectedBrush",
          "ControlItemSelectedLineBrush", "ControlFieldBrush", "ControlManualBrush", "ControlSliderHoverBrush", "ControlSliderHoverLineBrush" };
    // Of the theme (null: none).
    static readonly string[] ThemeControlKeys =
        { "ButtonBrush", "LineBrush", "TextBrush", "TabHoverBrush", "TabSelectedBrush", "TextMutedBrush", "MuteBrush", "GreenBrush", "TextBrush",
          "ButtonBrush", "LineBrush", "TabHoverBrush", null, "TabSelectedBrush", null, "ButtonBrush", "AccentBrush", "AccentBrush", "AccentBrush" };
    static readonly string[] LightControlColors =
        { "#FFEDEDED", "#FFACACAC", "#FF1E1E1E", "#FFE5F1FB", "#FFCCE4F7", "#FF606060", "#FFC62828", "#FF2E7D32", "#FF8A8A8A",
          "#FFFFFFFF", "#FF646464", "#FFB8E0F3", "#FF26A0DA", "#FFF6F6F6", "#FFDADADA", "#FFFFFFFF", "#FF1F5FBF", "#FFDCECFC", "#FF7EB4EA" };

    static void SetControlColors(ResourceDictionary dictionary)
    {
        for (int i = 0; i < ControlKeys.Length; i++)
            dictionary[ControlKeys[i]] = ThemeControlKeys[i] is { } key ? dictionary[key] : Brushes.Transparent;
        dictionary["ControlDisabledOpacity"] = 0.5;
        dictionary["HoverBrush"] = dictionary["AccentBrush"];   // buttons under the mouse (see WindowsHover)
        dictionary["HoverLineBrush"] = dictionary["AccentBrush"];
        dictionary["HoverTextBrush"] = dictionary["OnAccentTextBrush"];
        dictionary["PressedBrush"] = dictionary["AccentBrush"];   // buttons pressed or toggled on
        dictionary["PressedLineBrush"] = dictionary["AccentBrush"];
        dictionary["PressedTextBrush"] = dictionary["OnAccentTextBrush"];
        dictionary["ActiveIconBrush"] = dictionary["AccentBrush"];   // icon of an enabled option (transport bar)
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

    // Buttons under the mouse or pressed, and thumbs of the sliders under the mouse, everywhere (Styles.xaml): HoverBrush,
    // PressedBrush, their frames and their texts, set with the theme (the accent color, SetControlColors); with the light
    // controls, the light blue of Windows for the whole window (this dictionary, merged in the application after the
    // theme).
    static ResourceDictionary windowsHover;

    static ResourceDictionary WindowsHover()
    {
        if (windowsHover != null) return windowsHover;
        var dictionary = new ResourceDictionary();
        foreach (var (key, color) in new[] { ("HoverBrush", "#FFBEE6FD"), ("HoverLineBrush", "#FF3C7FB1"), ("HoverTextBrush", "#FF1E1E1E"),
                                             ("PressedBrush", "#FFC4E5F6"), ("PressedLineBrush", "#FF2C628B"), ("PressedTextBrush", "#FF1E1E1E"),
                                             ("ControlSliderHoverBrush", "#FFDCECFC"), ("ControlSliderHoverLineBrush", "#FF7EB4EA") })
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            dictionary[key] = brush;
        }
        return windowsHover = dictionary;
    }

    // The colors under the mouse and pressed of the whole window: those of Windows with the light controls, else those of
    // the theme; the icon of an enabled option (shuffle, repeat, module display) then in the blue of the theme. Also
    // called by Apply, for that blue.
    public static void ApplyHover()
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Remove(WindowsHover());
        if (!LightControls) return;
        if (merged.Count > 0 && merged[0]["FixedBrush"] is Brush blue) WindowsHover()["ActiveIconBrush"] = blue;
        merged.Add(WindowsHover());
    }

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
        ApplyHover();
    }
}
