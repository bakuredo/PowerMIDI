using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// One parameter of a settings page (SettingsPage): how it is shown and, for a value kept elsewhere than the page's
// module (the GS part EQ switch), how it is read and set.
public sealed class PageParam
{
    public string Label { get; init; }
    public string Section { get; init; }                         // heading above it (a new section starts when it changes)
    public int Min { get; init; }
    public int Max { get; init; } = 127;
    public IReadOnlyList<EffectChoice> Choices { get; init; }    // a list instead of a slider
    public Func<IReadOnlyList<EffectChoice>> DynamicChoices { get; init; }   // a list whose choices change (with ChoicesKey)
    public Func<string> ChoicesKey { get; init; }
    public Func<int, string> Format { get; init; }              // text of a known value (>= 0)
    public int AutoPosition { get; init; }                      // slider position while the value is unknown ("auto")
    public Func<int, bool> Visible { get; init; }               // by group (null: always)
    public Func<string> ToolTipSuffix { get; init; }

    // Own values, by group (null: the page's).
    public Func<int, int> OwnValue { get; init; }
    public Func<int, int> OwnFileValue { get; init; }
    public Func<int, bool> OwnIsFixed { get; init; }
    public Action<int, int> OwnSet { get; init; }
    public Action<int> OwnClear { get; init; }

    internal string Text(int v) => v < 0 ? "auto" : Format?.Invoke(v) ?? v.ToString();
}

// A Part or Drums page of the Effects tab: buttons choosing a group (channel, part, drum setup or map), for the Drums
// pages a note of its kit (names, fixed notes marked •, Follow: the notes played), then the parameters of that group /
// note, in sections. Values fixed in blue, unknown ones "auto"; right click: back to the file for a parameter, a note
// (on the list) or a group (on its button). The values live in a settings module of the Core (XgPartParams,
// GsDrumMap…), reached through the delegates below.
public sealed class SettingsPage
{
    readonly EffectsViewModel effects;
    readonly int baseIndex;                 // group buttons: baseIndex; note list: baseIndex + 1; parameters: + 10 + p
    public const int Span = 100;

    public EffectBlockViewModel Block { get; private set; }
    public IReadOnlyList<PageParam> Params { get; init; }
    public IReadOnlyList<string> GroupLabels { get; init; }           // texts of the group buttons
    public int Group { get; set; }                                    // group shown
    public int Note { get; set; } = 38;                               // note shown (Drums pages)

    // Store of the module: value, file value and fixed state of a parameter; fixed state of a note and of a group.
    public Func<int, int, int, int> Value { get; init; }
    public Func<int, int, int, int> FileValue { get; init; }
    public Func<int, int, int, bool> IsFixed { get; init; }
    public Func<int, int, bool> IsNoteFixed { get; init; }
    public Func<int, bool> IsGroupFixed { get; init; }
    public Action<IOutputDevice, int, int, int, int> Set { get; init; }
    public Action<IOutputDevice, int, int, int> Clear { get; init; }  // group, note, parameter; -1 = all
    public Func<bool> IsActive { get; init; }                         // the module plays in the current mode

    // Group buttons: visible (null: always), tooltip.
    public Func<int, bool> GroupVisible { get; init; }
    public Func<int, string> GroupToolTip { get; init; }
    public Func<int, string> GroupKey { get; init; }                  // what the tooltip depends on (channels…)

    // Drums pages: notes offered for a group, names of its kit, tooltip of the list, channels of a group (Follow).
    public bool HasNotes { get; init; }
    public Func<int, IEnumerable<int>> Notes { get; init; }
    public Func<int, IReadOnlyDictionary<int, string>> Tones { get; init; }
    public Func<int, string> NotesToolTip { get; init; }
    public Func<int, string> KitKey { get; init; }
    public Func<int, int, bool> GroupHasChannel { get; init; }
    public int FirstNote { get; init; }
    public int LastNote { get; init; } = 127;

    // Texts: of an unknown value (tooltip), and the start of a description (Insert mode): group, note.
    public string AutoText { get; init; } = "Set by module";
    public Func<int, int, string> DescribePrefix { get; init; }

    readonly List<EffectChannelViewModel> groupButtons = new();
    EffectListViewModel noteList;
    readonly List<EffectParamViewModel> rows = new();
    readonly List<(EffectHeadingViewModel Heading, List<int> Params)> sections = new();
    string notesKey = "";
    readonly string[] choicesKeys;

    public SettingsPage(EffectsViewModel effects, int baseIndex, int paramCount)
    {
        this.effects = effects;
        this.baseIndex = baseIndex;
        choicesKeys = new string[paramCount];
    }

    // Creates the block of the page: title, tab title, label of the group buttons (and of a second row of buttons, added
    // by the caller).
    public SettingsPage Build(string title, string tabTitle, string groupsLabel, string partsLabel = null)
    {
        Block = new EffectBlockViewModel
        {
            Title = title, TabTitle = tabTitle, ShowTitle = false, IsFirst = true, ChannelsLabel = groupsLabel, HasFollow = HasNotes,
            PartsLabel = partsLabel,
        };
        for (int g = 0; g < GroupLabels.Count; g++)
        {
            var button = new EffectChannelViewModel(effects, baseIndex, g, -1, GroupLabels[g]);
            groupButtons.Add(button);
            Block.Channels.Add(button);
        }
        if (HasNotes)
        {
            noteList = new EffectListViewModel(effects, baseIndex + 1, Array.Empty<EffectChoice>()) { Label = "Note" };
            Block.Params.Add(noteList);
        }
        string section = null;
        for (int p = 0; p < Params.Count; p++)
        {
            var param = Params[p];
            if (param.Section != null && param.Section != section)
            {
                section = param.Section;
                var heading = new EffectHeadingViewModel(effects, section) { HasLine = sections.Count > 0 };
                sections.Add((heading, new List<int>()));
                Block.Params.Add(heading);
            }
            if (param.Section != null) sections[^1].Params.Add(p);
            EffectParamViewModel row = param.Choices != null || param.DynamicChoices != null
                ? new EffectListViewModel(effects, baseIndex + 10 + p, param.Choices ?? Array.Empty<EffectChoice>()) { Label = param.Label }
                : new EffectSliderViewModel(effects, baseIndex + 10 + p) { Label = param.Label, Minimum = param.Min, Maximum = param.Max };
            rows.Add(row);
            Block.Params.Add(row);
        }
        return this;
    }

    public bool Handles(int fx) => fx >= baseIndex && fx < baseIndex + Span;

    IOutputDevice Device() => IsActive() ? effects.Output() : null;

    public void SetEffect(int fx, int value)
    {
        if (fx == baseIndex)
        {
            if (value >= 0) Group = value;
            foreach (var button in groupButtons) button.Key = "";   // pressed again: stays pressed
        }
        else if (fx == baseIndex + 1) { Note = value; Audition(); }
        else
        {
            var param = Params[fx - baseIndex - 10];
            if (param.OwnSet != null) param.OwnSet(Group, value);
            else Set(Device(), Group, HasNotes ? Note : 0, fx - baseIndex - 10, value);
            Audition();
        }
    }

    // Audition: the note shown, played on the first channel of the group (none: nothing), after the value changed.
    void Audition()
    {
        if (!HasNotes || !Block.Audition) return;
        int channel = Enumerable.Range(0, 16).FirstOrDefault(c => GroupHasChannel(Group, c), -1);
        if (channel >= 0) MidiTools.PlayNote(effects.Output(), channel, Note, 100, TimeSpan.FromMilliseconds(400));
    }

    // Right click: back to the file for a parameter, for the note (on the list) or for the whole group (on its button).
    public void ClearEffect(int fx)
    {
        if (fx == baseIndex)
        {
            Clear(Device(), Group, -1, -1);
            foreach (var param in Params) param.OwnClear?.Invoke(Group);
        }
        else if (fx == baseIndex + 1) Clear(Device(), Group, Note, -1);
        else
        {
            var param = Params[fx - baseIndex - 10];
            if (param.OwnClear != null) param.OwnClear(Group);
            else Clear(Device(), Group, HasNotes ? Note : 0, fx - baseIndex - 10);
        }
    }

    public string Describe(int g, int n, int p, int v) => $"{DescribePrefix(g, n)} {Params[p].Label}: {Params[p].Text(v)}";

    // Name of a note in the Yamaha notation (C3 = 60).
    public static string NoteName(int n) => MidiEventList.NoteName(n);

    bool OwnFixed(int g) => Params.Any(x => x.OwnIsFixed?.Invoke(g) == true);

    public void Refresh()
    {
        // Group buttons; a group that cannot be shown any more gives way to the first one that can.
        if (GroupVisible != null && !GroupVisible(Group))
            Group = Enumerable.Range(0, GroupLabels.Count).FirstOrDefault(GroupVisible, Group);
        foreach (var button in groupButtons)
        {
            int g = button.Part;
            bool on = g == Group, isFixed = IsGroupFixed(g) || OwnFixed(g), visible = GroupVisible?.Invoke(g) ?? true;
            button.Show($"{on}/{isFixed}/{visible}/{GroupKey?.Invoke(g)}", on, isFixed, GroupToolTip(g), visible);
        }

        if (HasNotes) RefreshNotes();

        // Sections: shown if one of their parameters is; the first one shown has no line above.
        bool first = true;
        foreach (var (heading, ps) in sections)
        {
            heading.IsVisible = ps.Any(p => Params[p].Visible?.Invoke(Group) ?? true);
            heading.HasLine = !first || !heading.IsVisible;
            if (heading.IsVisible) first = false;
        }

        int n = HasNotes ? Note : 0;
        for (int p = 0; p < Params.Count; p++)
        {
            var param = Params[p];
            var row = rows[p];
            if (param.DynamicChoices != null && param.ChoicesKey() != choicesKeys[p])
            {
                choicesKeys[p] = param.ChoicesKey();
                row.Updating = true;
                try { ((EffectListViewModel)row).Choices = param.DynamicChoices(); }
                finally { row.Updating = false; }
                row.Key = "";
            }
            int v = param.OwnValue?.Invoke(Group) ?? Value(Group, n, p);
            int fileValue = param.OwnFileValue?.Invoke(Group) ?? FileValue(Group, n, p);
            bool isFixed = param.OwnIsFixed?.Invoke(Group) ?? IsFixed(Group, n, p);
            bool visible = param.Visible?.Invoke(Group) ?? true;
            var suffix = param.ToolTipSuffix?.Invoke() ?? "";
            var toolTip = (isFixed ? $"Fixed (file: {(fileValue < 0 ? AutoText.ToLowerInvariant() : param.Text(fileValue))})"
                           : v < 0 ? AutoText : "File value") + suffix;
            row.Show($"{Group}/{n}/{v}/{isFixed}/{fileValue}/{visible}/{suffix}", v, isFixed, toolTip, param.Text(v), param.AutoPosition, visible);
        }
    }

    // Notes of the group (Follow: the last ones played on its channels), with the names of its kit; fixed ones marked •.
    void RefreshNotes()
    {
        if (Block.Follow && effects.FollowNote(c => GroupHasChannel(Group, c), FirstNote, LastNote) is int played) Note = played;
        Note = Math.Clamp(Note, FirstNote, LastNote);
        var tones = Tones(Group);
        var fixedNotes = Enumerable.Range(FirstNote, LastNote - FirstNote + 1).Where(x => IsNoteFixed(Group, x)).ToList();
        var notes = Notes(Group).Concat(fixedNotes).Append(Note).Distinct().OrderBy(x => x).ToList();
        var key = $"{Group}/{KitKey?.Invoke(Group)}/{string.Join(",", fixedNotes)}/{Note}";
        if (key == notesKey) return;
        notesKey = key;
        noteList.Updating = true;
        try
        {
            noteList.Choices = notes.Select(x => new EffectChoice($"{x} {NoteName(x)}" + (tones.TryGetValue(x, out var name) ? $" · {name}" : "") +
                                                                  (fixedNotes.Contains(x) ? " •" : ""), x)).ToList();
            noteList.SelectedChoice = noteList.Choices.FirstOrDefault(c => c.Value == Note);
            noteList.IsFixed = fixedNotes.Contains(Note);
            noteList.ToolTip = NotesToolTip(Group);
        }
        finally { noteList.Updating = false; }
    }
}
