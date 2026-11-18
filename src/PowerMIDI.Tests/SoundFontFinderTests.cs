using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// SoundFont of a MIDI file in SF2 mode (SoundFontFinder): SF_BANK tag, same name, the folder's, then the SoundFonts
// tab's; in a temporary folder.
public sealed class SoundFontFinderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"powermidi-sf2-{Guid.NewGuid():N}");
    readonly string soundFonts;
    const string Default = @"C:\default.sf2";

    public SoundFontFinderTests()
    {
        soundFonts = Path.Combine(root, "soundfonts");
        Directory.CreateDirectory(soundFonts);
    }

    public void Dispose() => Directory.Delete(root, true);

    string File(string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        System.IO.File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    static MidiFile Tagged(string text) => new(new TrackChunk(new TextEvent(text), new NoteOnEvent()));

    SoundFontFinder.Result Find(string midi, MidiFile file = null) => SoundFontFinder.Find(Path.Combine(root, midi), file, soundFonts, Default);

    [Fact]
    public void Tag_next_to_file_then_in_soundfonts_folder()
    {
        var inFolder = File(@"Album\Bank.sf2");
        var inSoundFonts = File(@"soundfonts\Other.sf2");
        File(@"Album\song.sf2");   // same name: the tag comes first
        var found = Find(@"Album\song.mid", Tagged("SF_BANK=Bank.sf2"));
        Assert.Equal((inFolder, SoundFontFinder.Source.Tag), (found.Path, found.From));
        Assert.Equal(inSoundFonts, Find(@"Album\song.mid", Tagged("sf_bank: Other.sf2")).Path);
        Assert.Equal("Other.sf2", SoundFontFinder.TagValue(Tagged("SF_BANK = \"Other.sf2\"")));
        Assert.Null(SoundFontFinder.TagValue(Tagged("Bank of the song")));
    }

    [Fact]
    public void Missing_tag_file_goes_on_and_is_said()
    {
        var same = File(@"Album\song.sf2");
        var found = Find(@"Album\song.mid", Tagged("SF_BANK=Gone.sf2"));
        Assert.Equal((same, SoundFontFinder.Source.SameName, "Gone.sf2"), (found.Path, found.From, found.Missing));
    }

    [Fact]
    public void Only_soundfont_of_folder_or_the_one_named_after_it()
    {
        var only = File(@"Solo\bank.sf2");
        Assert.Equal((only, SoundFontFinder.Source.Folder), (Find(@"Solo\a.mid").Path, Find(@"Solo\a.mid").From));

        var named = File(@"Game\Game.sf2");
        File(@"Game\extra.sf2");
        Assert.Equal(named, Find(@"Game\x.mid").Path);

        File(@"Mixed\one.sf2");
        File(@"Mixed\two.sf2");
        var none = Find(@"Mixed\x.mid");
        Assert.Equal((Default, SoundFontFinder.Source.Default), (none.Path, none.From));
    }
}
