using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PowerMIDI.Core;

// Parameter of an effect type (EFX, XG Reverb / Chorus / Variation), from the Domino definitions.
// Min, Max and Default are MIDI values; Labels maps a MIDI value to its text.
public sealed class EffectParamDef
{
    public int Address;
    public string Name;
    public int Min, Max, Default, Offset;
    public Dictionary<int, string> Labels = new();
    public string Unit = "";
}

public sealed class EffectTypeDef
{
    public string Name;
    public Dictionary<int, EffectParamDef> Params = new();   // by address
}

// Instrument shown in the mixer: name, details for the tooltip, drum kit or not. Missing: the sound asked for is
// not in the list of the mode; the name is then the one the module most likely falls back to.
public sealed record InstrumentInfo(string Name, string Detail, bool Drum, bool Missing = false);

// Entry of an instrument list. IsReset: "back to the file's instrument"; IsDisplay: closed-list placeholder;
// AssignPart: "assign the channel to a GS part" (0 = normal part, 1 / 2 = drum part 1 / 2);
// AssignXgBank: "assign the channel to a bank type" (XG: 0 = normal voices, 127 = drum kits, 126 = SFX kits;
// GM2: 121 = melodic sounds, 120 = drum kits).
// Both null: a sound.
public sealed class InstrumentChoice
{
    public string Text { get; init; }
    public int Msb { get; init; }
    public int Lsb { get; init; }
    public int Program { get; init; }
    public bool IsReset { get; init; }
    public bool IsDisplay { get; init; }
    public int? AssignPart { get; init; }
    public int? AssignXgBank { get; init; }
}

// GM, GM2, MT-32, GS and XG banks and effect types, read from definition files in the format of the Domino
// sequencer (instruments\GM.xml, GM2.xml, MT-32.xml, GS.xml = SC-8850, XG.xml = MU2000), in UTF-8.
// Programs are 0-based. GS maps: 1 = SC-55, 2 = SC-88, 3 = SC-88Pro, 4 = SC-8850.
// XG drums: MSB 127 = drum kits, 126 = SFX kits; XG maps: see XgMapNames.
// GM2: MSB 121 + LSB = melodic sounds, MSB 120 = drum kits.
public sealed class InstrumentDefinitions
{
    public static readonly Dictionary<int, string> GsMapNames = new() { [1] = "SC-55", [2] = "SC-88", [3] = "SC-88Pro", [4] = "SC-8850" };
    public const int DefaultGsMap = 4;   // map of bank LSB 0 after a GS Reset (SC-8850; see ChannelMixer.GsDefaultMap)

    // XG maps of the MU2000 (map selector of a channel in XG mode). Unlike the GS maps, most of them are families
    // of sounds picked by the bank MSB. 2000: the voices of bank MSB 0, whose 128 capital voices have two versions
    // (bank LSB 0 = MU100 Native voices, the default of the module, also LSB 126; LSB 127 = MU Basic voices);
    // the list has them as bank LSB 0 (MU100 Native names), then each one as LSB 126 and 127 after its variations.
    // MU50: the voices an XG Level 1 module also has (capital voices and part of the variations, marked MU50="1" in
    // the file), offered when the module plays the MU Basic voice map; its capital voices are plain bank LSB 0.
    // With that voice map, only the MU50 map, the SFX voices and the drum kits of the MU50 are offered.
    public static readonly Dictionary<int, string> XgMapNames = new()
        { [2] = "2000", [3] = "MEx", [4] = "SFX", [5] = "Spl", [6] = "GM2", [7] = "50" };
    public const int XgNativeMap = 2, XgSamplingMap = 5, XgMu50Map = 7;
    public const int XgBasicLsb = 127, XgNativeLsb = 126;

    // Map of an XG voice (not of a drum kit); a bank the MU2000 does not have counts as the default map.
    public static int XgMap(int msb, int lsb) => msb switch { 48 => 3, 64 => 4, 16 => 5, 121 => 6, _ => XgNativeMap };

    // Map of an XG voice when the MU50 map is offered (MU Basic voice map): a voice the MU50 has belongs to it,
    // unless its bank asks for the MU100 Native version of the capital voice (LSB 126).
    public int XgMap(int msb, int lsb, int program, bool withMu50) =>
        withMu50 && msb == 0 && lsb != XgNativeLsb && XgMu50Voices.Contains((0, lsb == XgBasicLsb ? 0 : lsb, program)) ? XgMu50Map : XgMap(msb, lsb);

    static bool IsInXgMap(int map, int msb, int lsb) => map switch
    {
        3 => msb == 48, 4 => msb == 64, XgSamplingMap => msb == 16, 6 => msb == 121,
        _ => msb == 0,
    };

    public readonly Dictionary<int, string> GmVoices = new();
    public readonly Dictionary<int, string> GmDrums = new();
    public readonly Dictionary<(int Lsb, int Program), string> Gm2Voices = new();   // bank MSB 121
    public readonly Dictionary<int, string> Gm2Drums = new();                         // bank MSB 120
    public const int Gm2MelodicMsb = 121, Gm2DrumMsb = 120;
    public readonly Dictionary<int, string> Mt32Voices = new();   // 128 preset patches (no banks on an MT-32)
    public readonly Dictionary<int, string> Mt32Drums = new();    // the rhythm part
    public readonly Dictionary<(int Map, int Msb, int Program), string> GsVoices = new();
    public readonly Dictionary<(int Map, int Program), string> GsDrums = new();
    public readonly Dictionary<(int Msb, int Lsb, int Program), string> XgVoices = new();
    public readonly Dictionary<(int Msb, int Program), string> XgDrums = new();
    public readonly HashSet<(int Msb, int Lsb, int Program)> XgMu50Voices = new();   // voices the MU50 also has
    public readonly HashSet<(int Msb, int Program)> XgMu50Drums = new();             // and its drum kits
    public Dictionary<int, EffectTypeDef> EfxTypes = new();
    public Dictionary<int, EffectTypeDef> XgReverbTypes = new();
    public Dictionary<int, EffectTypeDef> XgChorusTypes = new();
    public Dictionary<int, EffectTypeDef> XgVariationTypes = new();

    readonly Dictionary<string, List<InstrumentChoice>> choiceCache = new();

    static XmlDocument LoadXml(string path)
    {
        // The encoding is the one declared by the file (UTF-8). Files straight from Domino declare Shift_JIS,
        // which needs the code pages provider on .NET (Core).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var doc = new XmlDocument();
        doc.Load(path);
        return doc;
    }

    static string Attr(XmlNode node, string name) => node?.Attributes?[name]?.Value;
    static IEnumerable<XmlElement> Children(XmlNode node, string name) =>
        node == null ? Enumerable.Empty<XmlElement>() : node.ChildNodes.OfType<XmlElement>().Where(e => e.Name == name);

    public static InstrumentDefinitions Load(string dir)
    {
        var defs = new InstrumentDefinitions();

        var gmFile = Path.Combine(dir, "GM.xml");
        if (File.Exists(gmFile))
        {
            var x = LoadXml(gmFile);
            foreach (XmlElement pc in x.SelectNodes("/ModuleData/InstrumentList/Map/PC"))
                defs.GmVoices[int.Parse(Attr(pc, "PC")) - 1] = Attr(pc, "Name");
            foreach (XmlElement pc in x.SelectNodes("/ModuleData/DrumSetList/Map/PC"))
                defs.GmDrums[int.Parse(Attr(pc, "PC")) - 1] = Attr(pc, "Name");
        }

        var gm2File = Path.Combine(dir, "GM2.xml");
        if (File.Exists(gm2File))
        {
            var x = LoadXml(gm2File);
            foreach (XmlElement pc in x.SelectNodes("/ModuleData/InstrumentList/Map/PC"))
                foreach (var bank in Children(pc, "Bank"))
                    defs.Gm2Voices[(int.Parse(Attr(bank, "LSB")), int.Parse(Attr(pc, "PC")) - 1)] = Attr(bank, "Name");
            foreach (XmlElement pc in x.SelectNodes("/ModuleData/DrumSetList/Map/PC"))
                defs.Gm2Drums[int.Parse(Attr(pc, "PC")) - 1] = Attr(pc, "Name");
        }

        var mt32File = Path.Combine(dir, "MT-32.xml");
        if (File.Exists(mt32File))
        {
            var x = LoadXml(mt32File);
            foreach (XmlElement pc in x.SelectNodes("/ModuleData/InstrumentList/Map/PC"))
                defs.Mt32Voices[int.Parse(Attr(pc, "PC")) - 1] = Attr(pc, "Name");
            foreach (XmlElement pc in x.SelectNodes("/ModuleData/DrumSetList/Map/PC"))
                defs.Mt32Drums[int.Parse(Attr(pc, "PC")) - 1] = Attr(pc, "Name");
        }

        var gsFile = Path.Combine(dir, "GS.xml");
        if (File.Exists(gsFile))
        {
            var x = LoadXml(gsFile);
            var mapIndex = new Dictionary<string, int> { ["55Map"] = 1, ["88Map"] = 2, ["88ProMap"] = 3, ["8850Map"] = 4 };
            foreach (XmlElement map in x.SelectNodes("/ModuleData/InstrumentList/Map"))
            {
                if (!mapIndex.TryGetValue(Attr(map, "Name") ?? "", out int m)) continue;
                foreach (var pc in Children(map, "PC"))
                    foreach (var bank in Children(pc, "Bank"))
                        defs.GsVoices[(m, int.Parse(Attr(bank, "MSB")), int.Parse(Attr(pc, "PC")) - 1)] = Attr(bank, "Name");
            }
            foreach (XmlElement map in x.SelectNodes("/ModuleData/DrumSetList/Map"))
            {
                if (!mapIndex.TryGetValue(Attr(map, "Name") ?? "", out int m)) continue;
                foreach (var pc in Children(map, "PC")) defs.GsDrums[(m, int.Parse(Attr(pc, "PC")) - 1)] = Attr(pc, "Name");
            }
            defs.EfxTypes = ReadEfxDefinitions(x);
        }

        var xgFile = Path.Combine(dir, "XG.xml");
        if (File.Exists(xgFile))
        {
            var x = LoadXml(xgFile);
            foreach (XmlElement map in x.SelectNodes("/ModuleData/InstrumentList/Map"))
            {
                foreach (var pc in Children(map, "PC"))
                    foreach (var bank in Children(pc, "Bank"))
                    {
                        if (!int.TryParse(Attr(bank, "MSB"), out int msb) || msb > 127) continue;
                        var key = (msb, int.Parse(Attr(bank, "LSB")), int.Parse(Attr(pc, "PC")) - 1);
                        defs.XgVoices[key] = Attr(bank, "Name");
                        if (Attr(bank, "MU50") == "1") defs.XgMu50Voices.Add(key);
                    }
            }
            foreach (XmlElement map in x.SelectNodes("/ModuleData/DrumSetList/Map"))
                foreach (var pc in Children(map, "PC"))
                    foreach (var bank in Children(pc, "Bank"))
                        if (Attr(bank, "MSB") is "126" or "127")
                        {
                            var key = (int.Parse(Attr(bank, "MSB")), int.Parse(Attr(pc, "PC")) - 1);
                            defs.XgDrums[key] = Attr(pc, "Name");
                            if (Attr(bank, "MU50") == "1") defs.XgMu50Drums.Add(key);
                        }
            defs.XgReverbTypes = ReadXgEffectDefinitions(x, "00", "Reverb Parameter");
            defs.XgChorusTypes = ReadXgEffectDefinitions(x, "20", "Chorus Parameter");
            defs.XgVariationTypes = ReadXgEffectDefinitions(x, "40", "Variation Parameter");
        }
        return defs;
    }

    static string DataOf(XmlElement ccm) => ccm["Data"]?.InnerText ?? "";

    // Parameter from a Domino macro (CCM) whose <Data> matches dataPattern (group 1 = address).
    // linkDefault: default value given by a link (displayed value).
    // Domino values are "displayed" values: MIDI value = displayed value + Offset.
    static EffectParamDef ToParam(XmlElement ccm, string linkDefault, Regex dataPattern, Dictionary<string, Dictionary<int, string>> tables,
                                  Func<string, string> cleanName, bool withUnit)
    {
        var m = dataPattern.Match(DataOf(ccm));
        if (!m.Success) return null;
        var v = ccm["Value"];
        int offset = int.TryParse(Attr(v, "Offset"), out var o) ? o : 0;
        var labels = new Dictionary<int, string>();
        var tableId = Attr(v, "TableID");
        if (tableId != null && tables.TryGetValue(tableId, out var table)) labels = table;
        else foreach (var entry in Children(v, "Entry")) labels[int.Parse(Attr(entry, "Value")) + offset] = Attr(entry, "Label");
        int min = Attr(v, "Min") is { } mn ? int.Parse(mn) + offset : labels.Count > 0 ? labels.Keys.Min() : 0;
        int max = Attr(v, "Max") is { } mx ? int.Parse(mx) + offset : labels.Count > 0 ? labels.Keys.Max() : 127;
        int def = !string.IsNullOrEmpty(linkDefault) ? int.Parse(linkDefault) + offset
                : Attr(v, "Default") is { } d ? int.Parse(d) + offset : offset;   // no default: 0 displayed (Domino convention)
        var unit = withUnit && Attr(v, "Name") is { } u && u != "Value" ? u : "";
        return new EffectParamDef
        {
            Address = Convert.ToInt32(m.Groups[1].Value, 16), Name = cleanName(Attr(ccm, "Name") ?? ""),
            Min = min, Max = max, Default = def, Offset = offset, Labels = labels, Unit = unit,
        };
    }

    // Parameters of a type folder: its own macros (CCM) and links (CCMLink) to shared macros.
    static Dictionary<int, EffectParamDef> ReadParams(XmlElement typeFolder, Dictionary<string, XmlElement> byId,
                                                      Func<XmlElement, string, EffectParamDef> toParam)
    {
        var result = new Dictionary<int, EffectParamDef>();
        foreach (var child in typeFolder.ChildNodes.OfType<XmlElement>())
        {
            EffectParamDef p = null;
            if (child.Name == "CCM") p = toParam(child, null);
            else if (child.Name == "CCMLink" && Attr(child, "ID") is { } id && byId.TryGetValue(id, out var target)) p = toParam(target, Attr(child, "Value"));
            if (p != null) result[p.Address] = p;
        }
        return result;
    }

    // EFX types and parameters (SC-88Pro / SC-8850), read from the "EFX" macro folder of the Domino
    // file. Each type: an "EFX: nn Name" folder with its type macro (SysEx 40 03 00 MSB LSB), its
    // parameters (40 03 aa #VL) and links (CCMLink) to shared parameters, with their default value
    // for this type. Key: type code (MSB × 128 + LSB); parameters by address (0x03..0x19).
    static Dictionary<int, EffectTypeDef> ReadEfxDefinitions(XmlDocument x)
    {
        var types = new Dictionary<int, EffectTypeDef>();
        var folder = x.SelectNodes("/ModuleData/ControlChangeMacroList/Folder").OfType<XmlElement>()
                      .FirstOrDefault(f => Attr(f, "Name") == "EFX");
        if (folder == null) return types;

        var tables = new Dictionary<string, Dictionary<int, string>>();
        foreach (var table in Children(folder, "Table"))
            tables[Attr(table, "ID")] = Children(table, "Entry").GroupBy(e => int.Parse(Attr(e, "Value")))
                                         .ToDictionary(g => g.Key, g => Attr(g.Last(), "Label"));
        var byId = new Dictionary<string, XmlElement>();
        foreach (XmlElement ccm in folder.SelectNodes(".//CCM")) byId[Attr(ccm, "ID")] = ccm;

        var paramPattern = new Regex(@"40H 03H ([0-9A-F]{2})H #VL", RegexOptions.IgnoreCase);
        var typePattern = new Regex(@"40H 03H 00H ([0-9A-F]{2})H ([0-9A-F]{2})H", RegexOptions.IgnoreCase);
        Func<XmlElement, string, EffectParamDef> toParam = (ccm, link) =>
            ToParam(ccm, link, paramPattern, tables, n => Regex.Replace(n, @"^EFX:?\s*", "", RegexOptions.IgnoreCase), false);

        foreach (var typeFolder in Children(folder, "Folder"))
        {
            var nm = Regex.Match(Attr(typeFolder, "Name") ?? "", @"^EFX: \d+ (.+)$", RegexOptions.IgnoreCase);
            if (!nm.Success) continue;
            int? code = null;
            foreach (var child in Children(typeFolder, "CCM"))
            {
                var tm = typePattern.Match(DataOf(child));
                if (tm.Success) { code = Convert.ToInt32(tm.Groups[1].Value, 16) * 128 + Convert.ToInt32(tm.Groups[2].Value, 16); break; }
            }
            if (code == null) continue;
            types[code.Value] = new EffectTypeDef { Name = nm.Groups[1].Value.Trim(), Params = ReadParams(typeFolder, byId, toParam) };
        }
        return types;
    }

    // Parameters of an XG effect (Reverb, Chorus, Variation) by type, read from the Domino XG file:
    // "<Effect> Parameter" folder, one "NNN NAME" subfolder per type; the number NNN is linked to the
    // type code (MSB × 128 + LSB) through the type macro's list (labels "001  HALL 1" = 128).
    // typeAddress: type address (00 Reverb, 20 Chorus, 40 Variation). Parameters: Reverb 02 01 02..0B and
    // 10..15; Chorus 22..2B and 30..35; Variation 42..54 (2 bytes) and 70..75. Unit: e.g. "1/10 ms".
    static Dictionary<int, EffectTypeDef> ReadXgEffectDefinitions(XmlDocument x, string typeAddress, string folderName)
    {
        var types = new Dictionary<int, EffectTypeDef>();
        var typeMacro = x.SelectNodes("//CCM").OfType<XmlElement>()
                         .FirstOrDefault(c => Regex.IsMatch(DataOf(c), $"4CH 02H 01H {typeAddress}H #VH #VL", RegexOptions.IgnoreCase));
        var paramsFolder = x.SelectNodes("//Folder").OfType<XmlElement>()
                            .FirstOrDefault(f => Regex.IsMatch(Attr(f, "Name") ?? "", folderName, RegexOptions.IgnoreCase));
        if (typeMacro == null || paramsFolder == null) return types;

        var codeByNumber = new Dictionary<string, int>();
        foreach (var entry in Children(typeMacro["Value"], "Entry"))
        {
            var m = Regex.Match(Attr(entry, "Label") ?? "", @"^\s*(\d{3})\s");
            if (m.Success) codeByNumber[m.Groups[1].Value] = int.Parse(Attr(entry, "Value"));
        }
        // XG table labels: often prefixed with the MIDI value ("52  -12", "1  Wet -63"): strip it.
        var tables = new Dictionary<string, Dictionary<int, string>>();
        foreach (XmlElement table in x.SelectNodes("//Table"))
        {
            var labels = new Dictionary<int, string>();
            foreach (var entry in Children(table, "Entry"))
            {
                int value = int.Parse(Attr(entry, "Value"));
                var label = (Attr(entry, "Label") ?? "").Trim();
                var m = Regex.Match(label, $@"^{value}\s+(.+)$");
                if (m.Success) label = m.Groups[1].Value.Trim();
                labels[value] = Regex.Replace(label, @"\s{2,}", " ");
            }
            tables[Attr(table, "ID")] = labels;
        }
        var byId = new Dictionary<string, XmlElement>();
        foreach (XmlElement ccm in paramsFolder.SelectNodes(".//CCM")) byId[Attr(ccm, "ID")] = ccm;

        var pattern = new Regex(@"4CH 02H 01H ([0-9A-F]{2})H (#VH )?#VL", RegexOptions.IgnoreCase);
        Func<XmlElement, string, EffectParamDef> toParam = (ccm, link) =>
            ToParam(ccm, link, pattern, tables, n => Regex.Replace(n, @"^\w+:P\s*#?\s*", "").Trim(), true);

        foreach (XmlElement typeFolder in paramsFolder.SelectNodes(".//Folder"))
        {
            var m = Regex.Match((Attr(typeFolder, "Name") ?? "").Trim(), @"^(\d{3}) (.+)$");
            if (!m.Success || !codeByNumber.TryGetValue(m.Groups[1].Value, out int code)) continue;
            types[code] = new EffectTypeDef { Name = m.Groups[2].Value.Trim(), Params = ReadParams(typeFolder, byId, toParam) };
        }
        return types;
    }

    // ---------- Instrument names ----------

    // General MIDI program name (0-based); "Program n" if GM.xml is missing.
    public string GmProgramName(int program) => GmVoices.TryGetValue(program, out var n) ? n : $"Program {program + 1}";

    // General MIDI only: channel 10 is drums.
    public string GmInstrumentName(int channel, int program)
    {
        if (channel != 9) return GmProgramName(program);
        if (GmDrums.TryGetValue(program, out var kit)) return $"Drums · {kit}";
        return program == 0 ? "Drums" : $"Drums (kit {program + 1})";
    }

    // GM2: is a channel (0-based) playing a drum kit with this bank MSB?
    public static bool IsGm2Drum(int channel, int msb) => msb == Gm2DrumMsb || (msb != Gm2MelodicMsb && channel == 9);

    // Instrument name of a channel (0-based) according to the file's standard, the program (0-based),
    // the bank and the GS drum mode (rhythm > 0).
    public InstrumentInfo Resolve(string standard, int channel, int program, int msb, int lsb, int rhythm)
    {
        var pcText = $"PC {program + 1} · MSB {msb} / LSB {lsb}";

        // MT-32 (MT-32.xml): 128 preset patches, no banks; the rhythm part (channel 10 by default) ignores
        // Program Changes and always plays the rhythm setup.
        if (standard == "MT-32" && Mt32Voices.Count > 0)
        {
            if (rhythm > 0) return new InstrumentInfo($"Drums · {Mt32Drums.GetValueOrDefault(0, "Rhythm")}", "MT-32 · rhythm part", true);
            return new InstrumentInfo(Mt32Voices.GetValueOrDefault(program, $"Patch {program + 1}"), $"MT-32 · PC {program + 1}", false, !Mt32Voices.ContainsKey(program));
        }

        // GM2 (GM2.xml): MSB 120 = drum kit, MSB 121 + LSB = melodic sound and its variation; with another
        // bank (e.g. none, MSB 0), channel 10 keeps its drums and the other channels play the base sounds.
        if (standard == "GM2" && Gm2Voices.Count > 0)
        {
            if (IsGm2Drum(channel, msb))
            {
                var note = "";
                if (!Gm2Drums.TryGetValue(program, out var kit)) { Gm2Drums.TryGetValue(0, out kit); note = " · kit missing, Standard Kit"; }
                return new InstrumentInfo($"Drums · {kit}", $"GM2 · {pcText}{note}", true, note != "");
            }
            int variation = msb == Gm2MelodicMsb ? lsb : 0;
            var vnote = "";
            if (!Gm2Voices.TryGetValue((variation, program), out var name))
            {
                Gm2Voices.TryGetValue((0, program), out name);
                vnote = " · variation missing, base sound";
            }
            return new InstrumentInfo(name ?? GmProgramName(program), $"GM2 · {pcText}{vnote}", false, vnote != "");
        }

        if (standard == "XG" && XgVoices.Count > 0)
        {
            if (msb is 126 or 127)
            {
                var note = "";
                if (!XgDrums.TryGetValue((msb, program), out var kit)) { XgDrums.TryGetValue((msb, 0), out kit); note = " · kit missing, base kit"; }
                return new InstrumentInfo($"Drums · {kit}", $"XG · {pcText}{note}", true, note != "");
            }
            var vnote = "";
            if (!XgVoices.TryGetValue((msb, lsb, program), out var name))
            {
                // Missing variation: XG falls back to the base voice (LSB 0).
                if (!XgVoices.TryGetValue((msb, 0, program), out name)) XgVoices.TryGetValue((0, 0, program), out name);
                vnote = " · variation missing, base voice";
            }
            return new InstrumentInfo(name ?? GmProgramName(program), $"XG · {pcText}{vnote}", false, vnote != "");
        }

        if (standard is "GS" or "None" && GsVoices.Count > 0)
        {
            const string label = "GS";
            bool hasMap = lsb >= 1 && lsb <= 4;
            int map = hasMap ? lsb : ChannelMixer.GsDefaultMap;
            var mapText = hasMap ? $"map {GsMapNames[map]}" : $"map {GsMapNames[map]} (default)";

            if (rhythm > 0)
            {
                var note = "";
                if (!GsDrums.TryGetValue((map, program), out var kit)) { GsDrums.TryGetValue((map, 0), out kit); note = " · kit missing, STANDARD"; }
                return new InstrumentInfo($"Drums · {kit}", $"{label} · {mapText} · {pcText}{note}", true, note != "");
            }
            // Missing variation: like the Sound Canvas, fall back to the base sound of the same
            // family (MSB rounded down to a multiple of 8), then to the capital tone (MSB 0).
            var vnote = "";
            if (!GsVoices.TryGetValue((map, msb, program), out var name))
            {
                if (!GsVoices.TryGetValue((map, msb - msb % 8, program), out name)) GsVoices.TryGetValue((map, 0, program), out name);
                vnote = " · variation missing, capital tone";
            }
            return new InstrumentInfo(name ?? GmProgramName(program), $"{label} · {mapText} · {pcText}{vnote}", false, vnote != "");
        }

        // GM / GM2, or missing definition files: General MIDI names, drums on channel 10.
        // GM has one drum kit: another program on channel 10 is a kit of a larger standard.
        bool gmMissing = channel == 9 ? GmDrums.Count > 0 && !GmDrums.ContainsKey(program) : GmVoices.Count > 0 && !GmVoices.ContainsKey(program);
        return new InstrumentInfo(GmInstrumentName(channel, program), $"GM · {pcText}{(gmMissing ? " · not in the GM list" : "")}", channel == 9, gmMissing);
    }

    // Instruments offered for a channel, sorted by program then bank. Cached per (standard, type, map).
    // map: in GS, the map of the channel (bank LSB 1 to 4, anything else: the default one); in XG, see XgMapNames.
    // mu50Only (XG, MU Basic voice map): only the drum kits and the SFX voices the MU50 also has.
    public IReadOnlyList<InstrumentChoice> GetChoices(string standard, bool drum, int lsb, bool mu50Only = false)
    {
        // MT-32: the 128 preset patches, or the rhythm part.
        bool mt32 = standard == "MT-32" && Mt32Voices.Count > 0;
        // GM2: the 256 sounds (MSB 121 + LSB) or the 9 kits (MSB 120).
        bool gm2 = !mt32 && standard == "GM2" && Gm2Voices.Count > 0;
        bool useXg = !mt32 && !gm2 && standard == "XG" && XgVoices.Count > 0;
        bool useGs = !mt32 && !gm2 && !useXg && standard != "GM" && standard != "GM2" && GsVoices.Count > 0;
        int map = useXg ? (XgMapNames.ContainsKey(lsb) ? lsb : XgNativeMap) : lsb >= 1 && lsb <= 4 ? lsb : ChannelMixer.GsDefaultMap;
        mu50Only &= useXg;
        var cacheKey = $"{mt32}/{gm2}/{useXg}/{useGs}/{drum}/{map}/{mu50Only}";
        if (choiceCache.TryGetValue(cacheKey, out var cached)) return cached;

        var raw = new List<(int Msb, int Lsb, int Program, string Name)>();
        if (mt32) raw.AddRange((drum ? Mt32Drums : Mt32Voices).Select(kv => (0, 0, kv.Key, kv.Value)));
        else if (gm2 && drum) raw.AddRange(Gm2Drums.Select(kv => (Gm2DrumMsb, 0, kv.Key, kv.Value)));
        else if (gm2) raw.AddRange(Gm2Voices.Select(kv => (Gm2MelodicMsb, kv.Key.Lsb, kv.Key.Program, kv.Value)));
        else if (useXg && drum) raw.AddRange(XgDrums.Where(kv => !mu50Only || XgMu50Drums.Contains(kv.Key)).Select(kv => (kv.Key.Msb, 0, kv.Key.Program, kv.Value)));
        // MU50 map: its capital voices are the MU Basic ones (named by bank LSB 127), sent as plain bank LSB 0.
        else if (useXg && map == XgMu50Map)
            raw.AddRange(XgMu50Voices.Where(k => k.Msb == 0).Select(k => (0, k.Lsb, k.Program,
                k.Lsb == 0 && XgVoices.TryGetValue((0, XgBasicLsb, k.Program), out var basic) ? basic : XgVoices[k])));
        else if (useXg) raw.AddRange(XgVoices.Where(kv => IsInXgMap(map, kv.Key.Msb, kv.Key.Lsb) && (!mu50Only || XgMu50Voices.Contains(kv.Key))).Select(kv => (kv.Key.Msb, kv.Key.Lsb, kv.Key.Program, kv.Value)));
        else if (useGs && drum) raw.AddRange(GsDrums.Where(kv => kv.Key.Map == map).Select(kv => (0, map, kv.Key.Program, kv.Value)));
        else if (useGs) raw.AddRange(GsVoices.Where(kv => kv.Key.Map == map).Select(kv => (kv.Key.Msb, map, kv.Key.Program, kv.Value)));
        else if (drum && GmDrums.Count > 0) raw.AddRange(GmDrums.Select(kv => (0, 0, kv.Key, kv.Value)));
        else for (int pc = 0; pc < 128; pc++) raw.Add((0, 0, pc, GmProgramName(pc)));

        // In XG, the SFX kits (MSB 126) come after the drum kits; the sampling voices are listed bank after bank.
        bool byBank = useXg && !drum && map == XgSamplingMap;
        var sorted = raw.OrderBy(r => useXg && drum && r.Msb == 126).ThenBy(r => byBank ? r.Lsb : 0).ThenBy(r => r.Program)
                        .ThenBy(r => r.Msb).ThenBy(r => r.Lsb).ToList();
        var choices = sorted.Select((r, i) =>
            {
                // Base sound of a program: number shown. Variation: indented under its program.
                bool isBase = drum || mt32 || (gm2 && r.Lsb == 0) || (useGs && r.Msb == 0) || (!gm2 && !useGs && !useXg)
                              || (useXg && (byBank || i == 0 || sorted[i - 1].Program != r.Program));
                var prefix = isBase ? $"{r.Program + 1:000} " : "      · ";
                return new InstrumentChoice { Text = prefix + r.Name, Msb = r.Msb, Lsb = r.Lsb, Program = r.Program };
            }).ToList();
        choiceCache[cacheKey] = choices;
        return choices;
    }
}
