using System.Text.Json;
using System.Text.Json.Nodes;

namespace EpochAssist.Core;

/// <summary>
/// The slot macros' targets as stored (contracts/target-format.md): {"v":1,"shared":target?,"slots":{"form/slot":
/// target}}. Fields this version doesn't read are kept; a document of a newer version is kept whole and read-only.
/// </summary>
internal sealed class TargetDoc
{
    public const int FormatVersion = 1;
    private static readonly JsonDocumentOptions s_readOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public int Version { get; private init; } = FormatVersion;
    /// <summary>The shared slot target, or null while none is saved (the old aiming switch then decides).</summary>
    public TargetSpec? Shared { get; set; }
    /// <summary>Slots' own targets by "form/slot" key, in stored order.</summary>
    public List<KeyValuePair<string, TargetSpec>> Slots { get; } = new();
    public List<KeyValuePair<string, JsonNode?>> Extra { get; } = new();
    /// <summary>A newer document's whole stored text, written back as it was.</summary>
    public JsonObject? Newer { get; private init; }

    public bool ReadOnly => Newer != null;

    public TargetSpec? Slot(string key)
    {
        foreach (var (k, target) in Slots)
        {
            if (k == key) return target;
        }
        return null;
    }

    public void SetSlot(string key, TargetSpec? target)
    {
        int index = Slots.FindIndex(s => s.Key == key);
        if (target == null)
        {
            if (index >= 0) Slots.RemoveAt(index);
        }
        else if (index >= 0) Slots[index] = new(key, target);
        else Slots.Add(new(key, target));
    }

    public static TargetDoc? Parse(string text, out string reason)
    {
        try
        {
            return Parse(JsonNode.Parse(text, documentOptions: s_readOptions), out reason);
        }
        catch (Exception e)
        {
            reason = e.Message;
            return null;
        }
    }

    public static TargetDoc? Parse(JsonNode? node, out string reason)
    {
        reason = string.Empty;
        if (node is not JsonObject root)
        {
            reason = "not a JSON object";
            return null;
        }
        int version = root["v"] is JsonValue v && v.TryGetValue(out int n) ? n : 0;
        if (version < 1)
        {
            reason = "no format version";
            return null;
        }
        if (version > FormatVersion) return new TargetDoc { Version = version, Newer = (JsonObject)ConditionDoc.Clone(root)! };
        var doc = new TargetDoc { Version = version };
        foreach (var (key, value) in root)
        {
            switch (key)
            {
                case "v":
                    break;
                case "shared":
                    doc.Shared = TargetSpec.FromObject(value);
                    break;
                case "slots" when value is JsonObject slots:
                    foreach (var (slot, target) in slots) doc.Slots.Add(new(slot, TargetSpec.FromObject(target)));
                    break;
                case "slots":
                    reason = "slots isn't an object";
                    return null;
                default:
                    doc.Extra.Add(new(key, ConditionDoc.Clone(value)));
                    break;
            }
        }
        return doc;
    }

    public JsonObject ToJson()
    {
        if (Newer != null) return (JsonObject)ConditionDoc.Clone(Newer)!;
        var root = new JsonObject { ["v"] = Version };
        if (Shared != null) root["shared"] = Shared.ToObject();
        if (Slots.Count > 0)
        {
            var slots = new JsonObject();
            foreach (var (key, target) in Slots) slots[key] = target.ToObject();
            root["slots"] = slots;
        }
        foreach (var (key, value) in Extra) root[key] = ConditionDoc.Clone(value);
        return root;
    }

    public string ToText() => ToJson().ToJsonString();
}

/// <summary>
/// A setting holding slot macro targets: one TargetDoc, stored as compact JSON text in MelonPreferences and as a JSON
/// object in profiles and exports, as ConditionSetting stores its lists. Read with Doc (parsed once per stored text);
/// change with SetShared and SetSlot, which write through Value like any other setting.
/// </summary>
internal sealed class TargetSetting : Setting<string>
{
    private string? _parsedText;
    private TargetDoc _doc;
    private readonly TargetDoc _defaultDoc;

    public TargetSetting(Section section, string id, string label, string defaultText)
        : base(section, id, label, Canonical(defaultText))
    {
        _defaultDoc = TargetDoc.Parse(Default, out _) ?? throw new ArgumentException($"{id}: the default isn't a target document");
        _doc = _defaultDoc;
    }

    private static string Canonical(string text) => TargetDoc.Parse(text, out _)?.ToText() ?? text;

    /// <summary>The document in effect (the profile's or Default's), parsed once per stored text.</summary>
    public TargetDoc Doc
    {
        get
        {
            string text = Value;
            if (!ReferenceEquals(text, _parsedText))
            {
                _doc = TargetDoc.Parse(text, out _) ?? _defaultDoc;
                _parsedText = text;
            }
            return _doc;
        }
    }

    /// <summary>The stored shared target, or null while none is saved.</summary>
    public TargetSpec? Shared => Doc.Shared;

    /// <summary>A slot's own target by "form/slot" key, or null ("same as shared target").</summary>
    public TargetSpec? Slot(string slotKey) => Doc.Slot(slotKey);

    public bool SetShared(TargetSpec? target, out string reason) => Edit(doc => doc.Shared = target, out reason);

    /// <summary>Sets a slot's own target; null removes it, so the slot follows the shared one again.</summary>
    public bool SetSlot(string slotKey, TargetSpec? target, out string reason) => Edit(doc => doc.SetSlot(slotKey, target), out reason);

    private bool Edit(Action<TargetDoc> edit, out string reason)
    {
        var doc = TargetDoc.Parse(Value, out reason);
        if (doc == null) return false;
        if (doc.ReadOnly)
        {
            reason = "the slot targets were made by a newer version";
            return false;
        }
        edit(doc);
        Value = doc.ToText();
        return true;
    }

    /// <summary>Replaces the whole document (undo, dev); false with the reason when the text isn't one.</summary>
    public bool SetText(string text, out string reason)
    {
        var doc = TargetDoc.Parse(text, out reason);
        if (doc == null) return false;
        Value = doc.ToText();
        return true;
    }

    // Text that parses is kept (unreadable targets included); text that doesn't goes back to the default.
    internal override void Clamp()
    {
        if (TargetDoc.Parse(Value, out string reason) != null) return;
        Log.Warn($"{Section.Group.Name} setting {Id} can't be read ({reason}), so it is back to its default");
        Reset();
    }

    protected override string Show(string value)
    {
        var doc = TargetDoc.Parse(value, out _);
        if (doc == null) return "?";
        if (doc.ReadOnly) return "made by a newer version";
        string shared = doc.Shared != null ? doc.Shared.Words(TargetOwner.Slot) : "as the old aiming switch";
        return doc.Slots.Count == 0 ? shared : $"{shared}; {doc.Slots.Count} slot{(doc.Slots.Count == 1 ? string.Empty : "s")} with their own";
    }

    protected override JsonNode? Write(string value) => (JsonNode?)TargetDoc.Parse(value, out _)?.ToJson() ?? JsonValue.Create(value);

    // A string holding a document reads too (an export edited by hand).
    protected override bool TryRead(JsonValue json, out string value)
    {
        value = string.Empty;
        if (!json.TryGetValue(out string? text) || TargetDoc.Parse(text, out _) is not { } doc) return false;
        value = doc.ToText();
        return true;
    }

    protected override bool TryReadNode(JsonNode? node, out string value)
    {
        if (node is JsonObject obj && TargetDoc.Parse(obj, out _) is { } doc)
        {
            value = doc.ToText();
            return true;
        }
        value = string.Empty;
        return node is JsonValue json && TryRead(json, out value);
    }
}
