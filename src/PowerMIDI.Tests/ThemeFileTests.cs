using PowerMIDI.Core;

namespace PowerMIDI.Tests;

public class ThemeFileTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("PowerMIDI-themes").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    string Write(string name, string content)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    const string Header = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";

    [Fact]
    public void Colors_are_read_by_key()
    {
        var path = Write("Night.xaml", Header +
            "<!-- comment --><SolidColorBrush x:Key=\"WindowBrush\" Color=\"#101010\"/>" +
            "<SolidColorBrush x:Key=\"TextBrush\" Color=\" White \"/></ResourceDictionary>");
        var colors = ThemeFile.ReadColors(path);
        Assert.Equal(2, colors.Count);
        Assert.Equal("#101010", colors["WindowBrush"]);
        Assert.Equal("White", colors["TextBrush"]);
    }

    // Nothing but colors comes out of a theme file: other elements are not read.
    [Fact]
    public void Other_elements_and_incomplete_brushes_are_ignored()
    {
        var path = Write("Odd.xaml", Header +
            "<ObjectDataProvider x:Key=\"Run\" MethodName=\"Start\"/>" +
            "<SolidColorBrush Color=\"#101010\"/><SolidColorBrush x:Key=\"NoColor\"/>" +
            "<SolidColorBrush x:Key=\"PanelBrush\" Color=\"#202020\"/></ResourceDictionary>");
        Assert.Equal("PanelBrush", Assert.Single(ThemeFile.ReadColors(path)).Key);
    }

    [Fact]
    public void Missing_or_broken_file_gives_no_color()
    {
        Assert.Empty(ThemeFile.ReadColors(Path.Combine(folder, "missing.xaml")));
        Assert.Empty(ThemeFile.ReadColors(Write("Broken.xaml", Header + "<SolidColorBrush x:Key=\"A\" Color=\"#101010\"/>")));
    }

    [Fact]
    public void Names_are_the_xaml_files_of_the_folder()
    {
        Write("Zebra.xaml", Header + "</ResourceDictionary>");
        Write("amber.xaml", Header + "</ResourceDictionary>");
        Write("notes.txt", "not a theme");
        Assert.Equal(new[] { "amber", "Zebra" }, ThemeFile.GetNames(folder));
        Assert.Empty(ThemeFile.GetNames(Path.Combine(folder, "missing")));
    }

    // The themes distributed with the program all give the same brush keys.
    [Fact]
    public void Distributed_themes_have_the_same_keys()
    {
        var themes = Path.Combine(AppContext.BaseDirectory, "themes");
        var names = ThemeFile.GetNames(themes);
        Assert.Equal(7, names.Count);
        var keys = ThemeFile.ReadColors(Path.Combine(themes, "Dark.xaml")).Keys.OrderBy(k => k).ToList();
        Assert.NotEmpty(keys);
        foreach (var name in names)
            Assert.Equal(keys, ThemeFile.ReadColors(Path.Combine(themes, name + ".xaml")).Keys.OrderBy(k => k).ToList());
    }
}
