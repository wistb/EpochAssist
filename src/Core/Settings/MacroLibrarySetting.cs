using System.Text.Json.Nodes;

namespace EpochAssist.Core;

/// <summary>
/// A setting holding the macro library (contracts/macro-format.md): compact JSON text in MelonPreferences, a JSON object
/// in profiles and exports, as ConditionSetting stores its lists. Read the library with Doc (parsed once per stored
/// text); change it with Edit, which writes through Value like any other setting. Generation goes up with every new
/// stored text, so a run started from an older one knows it was edited.
/// </summary>
internal sealed class MacroLibrarySetting : Setting<string>
{
    private string? _parsedText;
    private MacroDoc _doc;
    private readonly MacroDoc _defaultDoc;

    public MacroLibrarySetting(Section section, string id, string label, string defaultText)
        : base(section, id, label, Canonical(defaultText))
    {
        _defaultDoc = MacroDoc.Parse(Default, out _) ?? throw new ArgumentException($"{id}: the default isn't a macro library");
        _doc = _defaultDoc;
    }

    private static string Canonical(string text) => MacroDoc.Parse(text, out _)?.ToText() ?? text;

    /// <summary>Goes up each time the library in effect changes (an edit, a profile switch, an import).</summary>
    public int Generation { get; private set; }

    /// <summary>The library in effect (the profile's or Default's), parsed once per stored text.</summary>
    public MacroDoc Doc
    {
        get
        {
            string text = Value;
            if (!ReferenceEquals(text, _parsedText))
            {
                _doc = MacroDoc.Parse(text, out _) ?? _defaultDoc;
                _parsedText = text;
                Generation++;
            }
            return _doc;
        }
    }

    /// <summary>
    /// Changes the library: edit gets a fresh copy and changes it in place. False with the reason when the library was
    /// made by a newer version (read-only) or the stored text can't be read.
    /// </summary>
    public bool Edit(Action<MacroDoc> edit, out string reason)
    {
        var doc = MacroDoc.Parse(Value, out reason);
        if (doc == null) return false;
        if (doc.ReadOnly)
        {
            reason = "the library was made by a newer version";
            return false;
        }
        edit(doc);
        Value = doc.ToText();
        return true;
    }

    /// <summary>Replaces the whole library (undo, dev); false with the reason when the text isn't one.</summary>
    public bool SetText(string text, out string reason)
    {
        var doc = MacroDoc.Parse(text, out reason);
        if (doc == null) return false;
        Value = doc.ToText();
        return true;
    }

    // Text that parses is kept (bad parts included); text that doesn't goes back to the default.
    internal override void Clamp()
    {
        if (MacroDoc.Parse(Value, out string reason) != null) return;
        Log.Warn($"{Section.Group.Name} setting {Id} can't be read ({reason}), so it is back to its default");
        Reset();
    }

    protected override string Show(string value)
    {
        var doc = MacroDoc.Parse(value, out _);
        if (doc == null) return "?";
        if (doc.ReadOnly) return "made by a newer version";
        int macros = doc.Sets.Sum(s => s.Macros.Count);
        return doc.Sets.Count == 0 ? "empty" : $"{doc.Sets.Count} set{(doc.Sets.Count == 1 ? string.Empty : "s")}, {macros} macro{(macros == 1 ? string.Empty : "s")}";
    }

    protected override JsonNode? Write(string value) => (JsonNode?)MacroDoc.Parse(value, out _)?.ToJson() ?? JsonValue.Create(value);

    // A string holding a library reads too (an export edited by hand).
    protected override bool TryRead(JsonValue json, out string value)
    {
        value = string.Empty;
        if (!json.TryGetValue(out string? text) || MacroDoc.Parse(text, out _) is not { } doc) return false;
        value = doc.ToText();
        return true;
    }

    protected override bool TryReadNode(JsonNode? node, out string value)
    {
        if (node is JsonObject obj && MacroDoc.Parse(obj, out _) is { } doc)
        {
            value = doc.ToText();
            return true;
        }
        value = string.Empty;
        return node is JsonValue json && TryRead(json, out value);
    }

    /// <summary>Parts this build can't read (unknown kinds, steps, fields), for an import's result.</summary>
    public int UnreadableParts => Doc.Sets.Sum(s => MacroDoc.UnknownParts(s).Count);
}
