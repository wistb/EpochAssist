namespace EpochAssist.Core;

/// <summary>
/// Toggle keys in conditions: an on/off per chord text, flipped by a press of that chord (the hotkey layer, gated like
/// every key it reads). Not stored: every state starts off and is cleared when AutoCast starts; zones keep them.
/// </summary>
internal static class ConditionToggles
{
    private static readonly HashSet<string> s_on = new();

    public static bool IsOn(string chordText) => chordText.Length > 0 && s_on.Contains(chordText);

    public static void Flip(string chordText)
    {
        if (chordText.Length == 0) return;
        if (!s_on.Remove(chordText)) s_on.Add(chordText);
    }

    public static void Clear() => s_on.Clear();

    public static IEnumerable<string> On => s_on;
}

/// <summary>
/// The keys condition entries use in the profile in use, from every registered condition setting: for the hotkey layer
/// (toggle presses) and for conflict warnings. Worked out again only when a setting's stored text changes.
/// </summary>
internal static class ConditionKeys
{
    /// <summary>A key in use: who uses it ("slot 3 condition"), its chord text, and whether it is a toggle.</summary>
    public readonly record struct KeyUse(string Label, string Chord, bool Toggle);

    private sealed record Source(string Owner, ConditionSetting Setting, Func<string, string> LabelFor);

    private static readonly List<Source> s_sources = new();
    private static readonly List<KeyUse> s_all = new();
    private static readonly List<string> s_toggles = new();
    private static readonly List<string?> s_seen = new();

    /// <summary>Registers a setting whose entries may use keys; labelFor names a list key for warnings.</summary>
    public static void Register(string owner, ConditionSetting setting, Func<string, string> labelFor)
    {
        s_sources.RemoveAll(s => s.Owner == owner && s.Setting == setting);
        s_sources.Add(new Source(owner, setting, labelFor));
        s_seen.Clear();
    }

    public static void Remove(string owner)
    {
        s_sources.RemoveAll(s => s.Owner == owner);
        s_seen.Clear();
    }

    public static IReadOnlyList<KeyUse> All
    {
        get
        {
            Refresh();
            return s_all;
        }
    }

    /// <summary>Chord texts of switched-on toggle entries, distinct.</summary>
    public static IReadOnlyList<string> Toggles()
    {
        Refresh();
        return s_toggles;
    }

    // Recomputed when any source's text is a different string than last time (edits, profile switches).
    private static void Refresh()
    {
        bool same = s_seen.Count == s_sources.Count;
        for (int i = 0; same && i < s_sources.Count; i++) same = ReferenceEquals(s_seen[i], s_sources[i].Setting.Value);
        if (same) return;

        s_seen.Clear();
        s_all.Clear();
        s_toggles.Clear();
        foreach (var source in s_sources)
        {
            var doc = source.Setting.Doc;
            s_seen.Add(source.Setting.Value);
            foreach (string key in doc.Order)
            {
                foreach (var entry in doc.List(key).Entries)
                {
                    if (entry.Invalid != null || entry.KindDef is not { } kind || !kind.Args.Any(a => a.Kind == ArgKind.Key)) continue;
                    string chord = entry.Values.Text("key");
                    bool toggle = kind.Id == "input.toggle";
                    s_all.Add(new KeyUse(source.LabelFor(key), chord, toggle));
                    if (toggle && entry.On && !s_toggles.Contains(chord)) s_toggles.Add(chord);
                }
            }
        }
    }
}
