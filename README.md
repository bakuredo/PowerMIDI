# PowerMIDI

MIDI file player and editor for Windows, made for hardware or software sound modules :
- **Roland Sound Canvas Series**: 88emuPlayer, Roland SCVA, Roland SC-8850 and other GS modules
- **Yamaha MU Series**: S-MU2000, Yamaha MU2000EX, Yamaha MU50, S-YXG50 and other XG modules
- **Roland MT-32**: MuntVSTi or Roland MT-32 / CM-64
- **General MIDI Level 1 and 2**: Too much devices and software !

![PowerMIDI playing a file: playlist, playback controls and Mixer tab](docs/screenshot.png)

## Features

- **Player**: Playback speed, transposition and playing a channel with computer keyboard ! (click on a channel number in Mixer page)
- **Playlist**: Files and folders, drag and drop, shuffle, repeat modes...
- **Modes**: GM, GM2, GS, XG and MT-32, detected from file or forced, with matching reset and an option to
  drop messages of other modes. Custom Sysex patches detected from XG and MT-32 modes.
- **Mixer**: Mute/Solo, activity and one controller slider per channel (modulation, volume, pan, reverb,
  chorus, delay / variation, filter and envelope settings), with instrument names for each mode.
- **Effects**: Reverb, chorus, delay, EQ and EFX in GS mode - Reverb, chorus, variation, insertion, EQ in XG - Reverb
  in MT-32 - Reverb and chorus in GM2.
- **Saving**: Changed settings (in blue) are written into a copy of MIDI file, for whole track or at a position
  of track (Insert mode).
- **File loops markers**: `loopStart` / `loopEnd`, `[` / `]` markers and CC111 are followed.
- **Color themes**: Built-in XAML themes; can be edited !


## Next-Steps
- A better icon application.
- Built-in SoundFont (SF2) files support.

## Requirements

- Windows 10 or 11, 64-bit.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) or download standalone version.
- A MIDI output device (a hardware module, software, VST or included with Windows if you want...).

## Building

With [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```
cd src
dotnet publish -c Release
```

## Source layout

| Folder | Contents |
| --- | --- |
| `src/PowerMIDI` | Windows application |
| `src/PowerMIDI.Core` | MIDI logic: modes, mixer state, effects, file editing, module definitions |
| `src/PowerMIDI.Console` | Console player |
| `src/PowerMIDI.Tests` | Tests that need no MIDI port |
| `instruments` | Module definition files |
| `samples` | MIDI files for trying program |

## Credits

- MIDI playback and file handling: [DryWetMIDI](https://melanchall.github.io/drywetmidi/) by Maxim Dobroselsky.
- Module definition files: based on the XML files of [Domino](http://takabosoft.com/) by Takabo Soft.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.