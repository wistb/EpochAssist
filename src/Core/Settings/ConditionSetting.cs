using System.Text.Json.Nodes;

namespace EpochAssist.Core;

/// <summary>
/// A setting holding condition lists: one document of keyed lists (contracts/conditions-format.md), stored as compact
/// JSON text in MelonPreferences and as a JSON object in profiles and exports. Read lists with List (parsed once per
/// stored text); change them with Edit, which writes through Value like any other setting (profile in use, save delay).
/// Named entries can be HUD quick switches, and kept entries can be switched off but not removed.
/// </summary>
internal sealed class ConditionSetting : Setting<string>
{
    private string? _parsedText;
    private ConditionDoc _doc;
    private readonly ConditionDoc _defaultDoc;

    public ConditionSetting(Section section, string id, string label, string defaultText)
        : base(section, id, label, Canonical(defaultText))
    {
        _defaultDoc = ConditionDoc.Parse(Default, out _) ?? throw new ArgumentException($"{id}: the default isn't a condition document");
        _doc = _defaultDoc;
    }

    /// <summary>Named entries offered as HUD quick toggles: (list key, entry id, label, action id).</summary>
    public List<(string List, string Entry, string Label, string Action)> Quick { get; init; } = new();

    /// <summary>Entries that can't be removed: (list key, entry id). A document without one gets it back from the
    /// default, switched off, at the top of its list.</summary>
    public List<(string List, string Entry)> KeptEntries { get; init; } = new();

    private static string Canonical(string text) => ConditionDoc.Parse(text, out _)?.ToText() ?? text;

    /// <summary>The document in effect (the profile's or Default's), parsed once per stored text.</summary>
    public ConditionDoc Doc
    {
        get
        {
            string text = Value;
            if (!ReferenceEquals(text, _parsedText))
            {
                _doc = ConditionDoc.Parse(text, out _) ?? _defaultDoc;
                _parsedText = text;
            }
            return _doc;
        }
    }

    /// <summary>A list by key; empty when the document has none.</summary>
    public ConditionList List(string key) => Doc.List(key);

    public bool IsKept(string list, ConditionEntry entry) => entry.Id != null && KeptEntries.Contains((list, entry.Id));

    /// <summary>
    /// Changes one list: edit gets a fresh copy of it (an empty list when there is none) and returns the list to store.
    /// False with the reason when the list or the document can't be changed here (made by a newer version).
    /// </summary>
    public bool Edit(string key, Func<ConditionList, ConditionList> edit, out string reason)
    {
        reason = string.Empty;
        var doc = ConditionDoc.Parse(Value, out reason);
        if (doc == null) return false;
        if (doc.ReadOnly)
        {
            reason = "the list was made by a newer version";
            return false;
        }
        var list = doc.Lists.TryGetValue(key, out var stored) ? stored : new ConditionList();
        if (list.Invalid != null)
        {
            reason = $"the list can't be changed here ({list.Invalid})";
            return false;
        }
        doc.Set(key, edit(list));
        Value = Repair(doc).ToText();
        return true;
    }

    /// <summary>Replaces the whole document (dev cond doc); false with the reason when the text isn't one.</summary>
    public bool SetText(string text, out string reason)
    {
        var doc = ConditionDoc.Parse(text, out reason);
        if (doc == null) return false;
        Value = Repair(doc).ToText();
        return true;
    }

    // Puts back any kept entry the document lacks, from the default, switched off, at the top of its list.
    private ConditionDoc Repair(ConditionDoc doc)
    {
        if (doc.ReadOnly) return doc;
        foreach (var (key, id) in KeptEntries)
        {
            var list = doc.Lists.TryGetValue(key, out var stored) ? stored : null;
            if (list?.Invalid != null || (list != null && list.IndexOf(id) >= 0)) continue;
            int index = _defaultDoc.List(key).IndexOf(id);
            if (index < 0) continue;
            var copy = ConditionDoc.Parse(Default, out _)!.List(key).Entries[index];
            copy.On = false;
            list ??= new ConditionList();
            list.Entries.Insert(0, copy);
            doc.Set(key, list);
        }
        return doc;
    }

    // Text that parses is kept (bad entries included); text that doesn't goes back to the default.
    internal override void Clamp()
    {
        string value = Value;
        var doc = ConditionDoc.Parse(value, out string reason);
        if (doc == null)
        {
            Log.Warn($"{Section.Group.Name} setting {Id} can't be read ({reason}), so it is back to its default");
            Reset();
            return;
        }
        string repaired = Repair(doc).ToText();
        if (repaired != value) Value = repaired;
    }

    protected override string Show(string value)
    {
        var doc = ConditionDoc.Parse(value, out _);
        if (doc == null) return "?";
        int entries = doc.Lists.Values.Sum(l => l.Entries.Count);
        return entries == 0 ? "none" : $"{entries} condition{(entries == 1 ? string.Empty : "s")}";
    }

    protected override JsonNode? Write(string value) => (JsonNode?)ConditionDoc.Parse(value, out _)?.ToJson() ?? JsonValue.Create(value);

    // A string holding a document reads too (an export edited by hand, or an older writer).
    protected override bool TryRead(JsonValue json, out string value)
    {
        value = string.Empty;
        if (!json.TryGetValue(out string? text) || ConditionDoc.Parse(text, out _) is not { } doc) return false;
        value = Repair(doc).ToText();
        return true;
    }

    protected override bool TryReadNode(JsonNode? node, out string value)
    {
        if (node is JsonObject obj && ConditionDoc.Parse(obj, out _) is { } doc)
        {
            value = Repair(doc).ToText();
            return true;
        }
        value = string.Empty;
        return node is JsonValue json && TryRead(json, out value);
    }

    internal override IEnumerable<IQuickSwitch> QuickSwitches => _switches ??= Quick.Select(q => (IQuickSwitch)new EntrySwitch(this, q.List, q.Entry, q.Label, q.Action)).ToArray();

    private IQuickSwitch[]? _switches;

    /// <summary>A named entry's on/off as a quick switch.</summary>
    private sealed class EntrySwitch : IQuickSwitch
    {
        private readonly ConditionSetting _setting;
        private readonly string _list, _entry;

        public EntrySwitch(ConditionSetting setting, string list, string entry, string label, string action)
        {
            _setting = setting;
            _list = list;
            _entry = entry;
            Label = label;
            ActionId = action;
        }

        public string Label { get; }
        public string? Tip => _setting.List(_list) is var list && list.IndexOf(_entry) is int i and >= 0 ? list.Entries[i].Words : Label;
        public string ActionId { get; }

        public bool Value => _setting.List(_list) is var list && list.IndexOf(_entry) is int i and >= 0 && list.Entries[i].On;

        public void Set(bool on)
        {
            _setting.Edit(_list, list =>
            {
                int i = list.IndexOf(_entry);
                if (i >= 0) list.Entries[i].On = on;
                return list;
            }, out _);
        }
    }
}
