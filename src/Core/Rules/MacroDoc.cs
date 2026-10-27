using System.Text.Json;
using System.Text.Json.Nodes;

namespace EpochAssist.Core;

/// <summary>
/// One step of a macro's sequence, as stored: its step name ("cast", "drink", "portal", "wait", "waitUntil",
/// "message"), switch and id, its fields by name and, for a wait until or a channel, its condition tree. Fields this
/// version doesn't read are kept; a step name it doesn't know is kept and stops a run ("needs a newer version").
/// </summary>
internal sealed class MacroStep
{
    public static readonly string[] Kinds = { "cast", "drink", "portal", "wait", "waitUntil", "message" };
    public static readonly string[] Modes = { "tap", "hold", "channel" };
    /// <summary>The stored aims: the three from before targets, and "target" for any other (its "target" object).</summary>
    public static readonly string[] Aims = { "cursor", "nearest", "hovered", "target" };
    public static readonly string[] OnTimeouts = { "stop", "go" };
    public const int MaxMessage = 80;

    public string Step { get; set; } = "cast";
    public bool On { get; set; } = true;
    public string? Id { get; set; }
    /// <summary>Every other field by name, as stored (until excepted).</summary>
    public JsonObject Fields { get; set; } = new();
    /// <summary>The condition of a wait until, or what keeps a channel going; null when the step has none.</summary>
    public ConditionList? Until { get; set; }
    /// <summary>A step that isn't an object at all: kept as it was, invalid.</summary>
    public JsonNode? Raw { get; init; }
    /// <summary>Set on steps past the macro's step limit (a hand-edited document): kept, skipped.</summary>
    internal string? LimitReason { get; set; }

    public bool Known => Raw == null && Kinds.Contains(Step);

    // Defaults when a field is absent (contracts/macro-format.md).
    public string Skill => Text("skill");
    public string Mode => Text("mode", "tap");
    public float Ms => Number("ms", Step == "wait" ? 500f : 1000f);
    public string Aim => Text("aim", "cursor");
    public float AimRange => Number("aimRange", 15f);
    public float TimeoutMs => Number("timeoutMs", Step == "waitUntil" ? 2000f : 1000f);
    public string OnTimeout => Text("onTimeout", "stop");
    public string Message => Text("text");

    /// <summary>Whether the step sends input (cast, drink, portal): sending steps of a run keep 0.1 s apart.</summary>
    public bool Sends => Step is "cast" or "drink" or "portal";

    public string Text(string name, string fallback = "") =>
        Fields[name] is JsonValue v && v.TryGetValue(out string? text) ? text : fallback;

    public float Number(string name, float fallback) =>
        Fields[name] is JsonValue v && MacroDoc.TryNumber(v, out double d) ? (float)d : fallback;

    public bool Has(string name) => Fields.ContainsKey(name);

    // The target read from the fields it was last made from (the run asks every frame; nothing allocates while they
    // stay the same).
    private TargetSpec? _target;
    private (JsonNode? Aim, JsonNode? Range, JsonNode? Target) _targetFrom;

    /// <summary>
    /// What a cast step lands on: an old aim as the target it always meant (at cursor; nearest within aimRange, else at
    /// cursor; hovered, else at cursor), or the "target" object. An aim this build doesn't know reads "needs a newer
    /// version".
    /// </summary>
    public TargetSpec Target
    {
        get
        {
            var from = (Fields["aim"], Fields["aimRange"], Fields["target"]);
            if (_target != null && ReferenceEquals(from.Item1, _targetFrom.Aim) && ReferenceEquals(from.Item2, _targetFrom.Range)
                && ReferenceEquals(from.Item3, _targetFrom.Target)) return _target;
            _targetFrom = from;
            return _target = Aim switch
            {
                "cursor" => TargetSpec.Cursor,
                "nearest" => TargetSpec.OldNearest(AimRange),
                "hovered" => TargetSpec.OldHovered(),
                "target" => TargetSpec.FromObject(Fields["target"]),
                _ => TargetSpec.Unreadable(null),
            };
        }
    }

    /// <summary>
    /// Stores a target: one of the old aims in the old shape (aim, and aimRange for nearest), so steps, exports and
    /// share codes read the same as before; any other as aim "target" with its object, which builds from before
    /// targets refuse rather than cast elsewhere.
    /// </summary>
    public void SetTarget(TargetSpec target)
    {
        if (target.Legacy)
        {
            Fields["aim"] = TargetSpec.PickNames[(int)target.Pick];
            if (target.Pick == TargetPick.Nearest) Fields["aimRange"] = target.Range;
            else Fields.Remove("aimRange");
            Fields.Remove("target");
            return;
        }
        Fields["aim"] = "target";
        Fields["target"] = target.ToObject();
        Fields.Remove("aimRange");
    }

    /// <summary>A step this build can't run because a newer one made it: a step name, aim or target it doesn't know.</summary>
    public bool NeedsNewer => Raw == null && (!Known || (Step == "cast" && Invalid == TargetSpec.NewerVersion));

    /// <summary>Why the step can't run here, or null: a step name this build doesn't know, or a field outside its range.</summary>
    public string? Invalid
    {
        get
        {
            if (Raw != null) return "not a step";
            if (LimitReason != null) return LimitReason;
            if (!Known) return "needs a newer version";
            switch (Step)
            {
                case "cast":
                    if (Skill.Length == 0) return "select skill";
                    if (!Modes.Contains(Mode)) return $"mode isn't one of {string.Join(", ", Modes)}";
                    if (Mode == "hold" && OutOfRange("ms", Ms, 100, 5000) is { } hold) return hold;
                    if (Mode == "channel" && OutOfRange("ms", Ms, 100, 10000) is { } channel) return channel;
                    if (!Aims.Contains(Aim)) return TargetSpec.NewerVersion;
                    if (Aim == "target")
                    {
                        if (Target.Invalid(TargetOwner.Step) is { } target) return target;
                    }
                    else if (OutOfRange("aimRange", AimRange, 2, 60) is { } range) return range;
                    if (Mode == "channel" && Until?.Invalid is { } channelUntil) return channelUntil;
                    return OutOfRange("timeoutMs", TimeoutMs, 0, 5000);
                case "drink":
                    return OutOfRange("timeoutMs", TimeoutMs, 0, 5000);
                case "wait":
                    return OutOfRange("ms", Ms, 50, 10000);
                case "waitUntil":
                    if (Until == null || Until.Entries.Count == 0) return "add a condition to wait for";
                    if (Until.Invalid is { } until) return until;
                    if (!OnTimeouts.Contains(OnTimeout)) return "onTimeout isn't stop or go";
                    return OutOfRange("timeoutMs", TimeoutMs, 100, 10000);
                case "message":
                    if (Message.Length == 0) return "write a message";
                    return Message.Length > MaxMessage ? $"message is longer than {MaxMessage} characters" : null;
            }
            return null;
        }
    }

    private static string? OutOfRange(string name, float value, float min, float max) =>
        value < min || value > max || float.IsNaN(value) ? $"{name} {ArgSpec.Format(value)} is outside {ArgSpec.Format(min)}–{ArgSpec.Format(max)}" : null;

    /// <summary>A new step of a kind with the fields a new one starts with.</summary>
    public static MacroStep New(string step, string? skill = null)
    {
        var made = new MacroStep { Step = step };
        switch (step)
        {
            case "cast":
                if (skill != null) made.Fields["skill"] = skill;
                made.Fields["mode"] = "tap";
                made.Fields["aim"] = "cursor";
                made.Fields["timeoutMs"] = 1000;
                break;
            case "drink":
                made.Fields["timeoutMs"] = 1000;
                break;
            case "wait":
                made.Fields["ms"] = 500;
                break;
            case "waitUntil":
                made.Until = new ConditionList();
                made.Fields["timeoutMs"] = 2000;
                made.Fields["onTimeout"] = "stop";
                break;
            case "message":
                made.Fields["text"] = "Macro fired";
                break;
        }
        return made;
    }
}

/// <summary>A macro's trigger: automatic, on a key press, while a key is held, or toggled by a key.</summary>
internal sealed class MacroTrigger
{
    public static readonly string[] Kinds = { "auto", "press", "hold", "toggle" };

    public string Kind { get; set; } = "auto";
    /// <summary>Chord text as hotkeys store it (empty for automatic).</summary>
    public string Key { get; set; } = string.Empty;
    public List<KeyValuePair<string, JsonNode?>> Extra { get; } = new();

    public bool NeedsKey => Kind is "press" or "hold" or "toggle";
}

/// <summary>
/// A library macro: name and switch; pre-conditions (trigger, movement, wait between uses); the skills that must be
/// ready; the trigger tree (an "all" root); and the sequence of steps. Unknown fields are kept.
/// </summary>
internal sealed class Macro
{
    public static readonly string[] Moves = { "any", "moving", "still" };
    public const int MaxName = 40;
    public const int MaxSteps = 10;
    public const float MaxWaitMs = 600000f;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool On { get; set; } = true;
    public MacroTrigger Trigger { get; set; } = new();
    public string Move { get; set; } = "any";
    public float WaitMs { get; set; } = 1000f;
    /// <summary>Skill id → ticked, in stored order: the skills that must be on the bar and ready before it starts.</summary>
    public List<KeyValuePair<string, bool>> Ready { get; } = new();
    public ConditionList All { get; set; } = new();
    public List<MacroStep> Actions { get; } = new();
    public List<KeyValuePair<string, JsonNode?>> Extra { get; } = new();
    /// <summary>A macro that isn't an object, or one past its set's limit: kept, never starts.</summary>
    public JsonNode? Raw { get; init; }
    internal string? LimitReason { get; set; }

    /// <summary>Why the macro never starts, or null.</summary>
    public string? Invalid
    {
        get
        {
            if (Raw != null) return "not a macro";
            if (LimitReason != null) return LimitReason;
            if (Name.Length is 0 or > MaxName) return $"name must be 1–{MaxName} characters";
            if (!MacroTrigger.Kinds.Contains(Trigger.Kind)) return "needs a newer version (trigger)";
            if (Trigger.NeedsKey && ConditionKinds.ChordOf(Trigger.Key).IsEmpty) return "set a trigger key";
            if (!Moves.Contains(Move)) return "movement isn't any, moving or still";
            if (WaitMs < 0f || WaitMs > MaxWaitMs || float.IsNaN(WaitMs)) return $"wait between uses is outside 0–{ArgSpec.Format(MaxWaitMs)} ms";
            if (All.Invalid != null) return All.Invalid;
            int on = 0;
            foreach (var step in Actions)
            {
                if (step.On) on++;
            }
            return on == 0 ? "no steps" : null;
        }
    }

    /// <summary>The skills of the cast steps, in step order, without repeats.</summary>
    public IEnumerable<string> CastSkills() =>
        Actions.Where(s => s.Raw == null && s.Step == "cast" && s.Skill.Length > 0).Select(s => s.Skill).Distinct();

    /// <summary>Whether a skill is ticked in must be ready (a cast skill not listed is ticked, as a new step's is).</summary>
    public bool ReadyTicked(string skill)
    {
        foreach (var (key, on) in Ready)
        {
            if (key == skill) return on;
        }
        return true;
    }

    /// <summary>Makes must be ready follow the cast steps: new skills ticked, skills no step casts any more dropped.</summary>
    public void SyncReady()
    {
        var skills = CastSkills().ToList();
        Ready.RemoveAll(r => !skills.Contains(r.Key));
        foreach (string skill in skills)
        {
            if (!Ready.Any(r => r.Key == skill)) Ready.Add(new(skill, true));
        }
    }
}

/// <summary>A named set of macros for one class, or for all of them. Sets and macros in library order are priority.</summary>
internal sealed class MacroSet
{
    public static readonly string[] Classes = { "Acolyte", "Mage", "Primalist", "Rogue", "Sentinel" };
    /// <summary>The class of a set that shows and runs for every class.</summary>
    public const string AllClasses = "All";
    public const int MaxMacros = 30;
    public const int MaxPerClass = 20;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Class { get; set; } = "Rogue";
    public bool On { get; set; } = true;
    public List<Macro> Macros { get; } = new();
    public List<KeyValuePair<string, JsonNode?>> Extra { get; } = new();
    public JsonNode? Raw { get; init; }
    internal string? LimitReason { get; set; }

    /// <summary>A class this build knows: only those sets run (another is kept and shown "unknown class").</summary>
    public bool KnownClass => Class == AllClasses || Classes.Contains(Class);

    /// <summary>Whether the set runs for a character of this class: its own class, or all classes.</summary>
    public bool AppliesTo(string? cls) => cls != null && (Class == cls || Class == AllClasses);

    /// <summary>The class as shown: "all classes" for an all-classes set.</summary>
    public static string ClassLabel(string cls) => cls == AllClasses ? "all classes" : cls;

    public string? Invalid
    {
        get
        {
            if (Raw != null) return "not a set";
            if (LimitReason != null) return LimitReason;
            return Name.Length is 0 or > Macro.MaxName ? $"name must be 1–{Macro.MaxName} characters" : null;
        }
    }

    public bool Full => Macros.Count >= MaxMacros;
}

/// <summary>
/// The macro library (contracts/macro-format.md): format version v and named sets in priority order, with unknown
/// fields at every level kept. A document of a newer version is kept whole and read-only. Read once per stored text and
/// not changed after; edits work on a copy (Copy, then the setting's Edit).
/// </summary>
internal sealed class MacroDoc
{
    public const int FormatVersion = 1;

    // ---- shipped sets ----

    /// <summary>
    /// The sets the mod ships, switched off, for all classes. "Auto potion" drinks whenever a whole potion's heal (50 + 10
    /// × level) fits in the missing health, or below 35% health at once; never with no potion or in town. No wait
    /// between uses: the dispatcher holds a second drink back only while the last one is still landing.
    /// </summary>
    private const string ShippedSetsText = """
        [{"id":"potions","name":"Potions","class":"All","on":false,"macros":[
          {"id":"autopotion","name":"Auto potion","on":true,"trigger":{"kind":"auto"},"move":"any","waitMs":0,
           "all":[
             {"kind":"potions.available","args":{"compare":"atLeast","count":1},"on":true,"trueWhenUnknown":false},
             {"kind":"zone.kind","args":{"is":"town","not":true},"on":true,"trueWhenUnknown":false},
             {"any":[
               {"kind":"player.healthMissing","args":{"atLeast":"potion"},"on":true,"trueWhenUnknown":false},
               {"kind":"player.health","args":{"compare":"below","percent":35},"on":true,"trueWhenUnknown":false}
             ]}
           ],
           "actions":[{"step":"drink","timeoutMs":1000}]}
        ]}]
        """;

    /// <summary>Raised when a new shipped set is added; a library below it gets the sets it hasn't had yet.</summary>
    public const int ShippedVersion = 1;
    private const string ShippedKey = "shipped";

    /// <summary>The library a new profile starts with: the shipped sets, switched off.</summary>
    public static readonly string DefaultText = $"{{\"v\":1,\"{ShippedKey}\":{ShippedVersion},\"sets\":{ShippedSetsText.Trim()}}}";

    /// <summary>Whether the library hasn't had every shipped set yet (and can be changed).</summary>
    public bool NeedsShipped
    {
        get
        {
            if (ReadOnly) return false;
            int index = Extra.FindIndex(e => e.Key == ShippedKey);
            int had = index >= 0 && Extra[index].Value is JsonValue v && v.TryGetValue(out int n) ? n : 0;
            return had < ShippedVersion;
        }
    }

    /// <summary>
    /// Adds the shipped sets a stored library hasn't had yet (once: a deleted shipped set doesn't come back), switched
    /// off and first in the library; false when there was nothing to add or the library is read-only.
    /// </summary>
    public bool AddShipped()
    {
        if (!NeedsShipped) return false;
        int index = Extra.FindIndex(e => e.Key == ShippedKey);
        var shipped = JsonNode.Parse(ShippedSetsText) as JsonArray;
        int at = 0;
        foreach (var node in shipped ?? new JsonArray())
        {
            var set = ParseSet(node);
            if (SetsOf(set.Class).Count() >= MacroSet.MaxPerClass) continue;
            if (Sets.Any(s => s.Id == set.Id) || set.Macros.Any(m => Sets.SelectMany(s => s.Macros).Any(o => o.Id == m.Id))) Renumber(set);
            set.Name = FreeSetName(set.Name, set.Class);
            Sets.Insert(at++, set);
        }
        if (index >= 0) Extra[index] = new(ShippedKey, JsonValue.Create(ShippedVersion));
        else Extra.Add(new(ShippedKey, JsonValue.Create(ShippedVersion)));
        return true;
    }

    private static readonly JsonDocumentOptions s_readOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public int Version { get; private init; } = FormatVersion;
    public List<MacroSet> Sets { get; } = new();
    public List<KeyValuePair<string, JsonNode?>> Extra { get; } = new();
    /// <summary>A newer document's whole stored text, written back as it was.</summary>
    public JsonObject? Newer { get; private init; }

    public bool ReadOnly => Newer != null;

    public static MacroDoc? Parse(string text, out string reason)
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

    public static MacroDoc? Parse(JsonNode? node, out string reason)
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
        if (version > FormatVersion) return new MacroDoc { Version = version, Newer = (JsonObject)ConditionDoc.Clone(root)! };

        var doc = new MacroDoc { Version = version };
        foreach (var (key, value) in root)
        {
            switch (key)
            {
                case "v":
                    break;
                case "sets" when value is JsonArray sets:
                    foreach (var set in sets) doc.Sets.Add(ParseSet(set));
                    break;
                case "sets":
                    reason = "sets isn't an array";
                    return null;
                default:
                    doc.Extra.Add(new(key, ConditionDoc.Clone(value)));
                    break;
            }
        }
        doc.MarkLimits();
        return doc;
    }

    // Sets past a class's limit and macros past a set's are kept and invalid (a hand-edited document).
    private void MarkLimits()
    {
        var perClass = new Dictionary<string, int>();
        foreach (var set in Sets)
        {
            perClass.TryGetValue(set.Class, out int count);
            perClass[set.Class] = ++count;
            set.LimitReason = count > MacroSet.MaxPerClass ? $"past the {MacroSet.MaxPerClass}-set limit for {set.Class}" : null;
            for (int i = 0; i < set.Macros.Count; i++)
            {
                set.Macros[i].LimitReason = i >= MacroSet.MaxMacros ? $"past the {MacroSet.MaxMacros}-macro limit" : null;
            }
        }
    }

    /// <summary>A set parsed on its own (a share code's payload).</summary>
    public static MacroSet ParseSet(JsonNode? node)
    {
        if (node is not JsonObject obj) return new MacroSet { Raw = ConditionDoc.Clone(node) ?? JsonValue.Create(string.Empty) };
        var set = new MacroSet();
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
                case "id" when Str(value) is { } id:
                    set.Id = id;
                    break;
                case "name" when Str(value) is { } name:
                    set.Name = name;
                    break;
                case "class" when Str(value) is { } cls:
                    set.Class = cls;
                    break;
                case "on" when value is JsonValue o && o.TryGetValue(out bool on):
                    set.On = on;
                    break;
                case "macros" when value is JsonArray macros:
                    foreach (var macro in macros) set.Macros.Add(ParseMacro(macro));
                    break;
                default:
                    set.Extra.Add(new(key, ConditionDoc.Clone(value)));
                    break;
            }
        }
        return set;
    }

    private static Macro ParseMacro(JsonNode? node)
    {
        if (node is not JsonObject obj) return new Macro { Raw = ConditionDoc.Clone(node) ?? JsonValue.Create(string.Empty) };
        var macro = new Macro();
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
                case "id" when Str(value) is { } id:
                    macro.Id = id;
                    break;
                case "name" when Str(value) is { } name:
                    macro.Name = name;
                    break;
                case "on" when value is JsonValue o && o.TryGetValue(out bool on):
                    macro.On = on;
                    break;
                case "trigger" when value is JsonObject trigger:
                    macro.Trigger = ParseTrigger(trigger);
                    break;
                case "move" when Str(value) is { } move:
                    macro.Move = move;
                    break;
                case "waitMs" when value is JsonValue w && TryNumber(w, out double waitMs):
                    macro.WaitMs = (float)waitMs;
                    break;
                case "ready" when value is JsonObject ready:
                    foreach (var (skill, tick) in ready)
                    {
                        macro.Ready.Add(new(skill, tick is JsonValue t && t.TryGetValue(out bool ticked) ? ticked : true));
                    }
                    break;
                case "all" when value is JsonArray all:
                    macro.All = ConditionDoc.ParseItems(all);
                    break;
                case "actions" when value is JsonArray actions:
                    foreach (var step in actions) macro.Actions.Add(ParseStep(step));
                    break;
                default:
                    macro.Extra.Add(new(key, ConditionDoc.Clone(value)));
                    break;
            }
        }
        for (int i = Macro.MaxSteps; i < macro.Actions.Count; i++) macro.Actions[i].LimitReason = $"past the {Macro.MaxSteps}-step limit";
        return macro;
    }

    private static MacroTrigger ParseTrigger(JsonObject obj)
    {
        var trigger = new MacroTrigger();
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
                case "kind" when Str(value) is { } kind:
                    trigger.Kind = kind;
                    break;
                case "key" when Str(value) is { } chord:
                    trigger.Key = chord;
                    break;
                default:
                    trigger.Extra.Add(new(key, ConditionDoc.Clone(value)));
                    break;
            }
        }
        return trigger;
    }

    private static MacroStep ParseStep(JsonNode? node)
    {
        if (node is not JsonObject obj) return new MacroStep { Raw = ConditionDoc.Clone(node) ?? JsonValue.Create(string.Empty) };
        var step = new MacroStep();
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
                case "step" when Str(value) is { } name:
                    step.Step = name;
                    break;
                case "on" when value is JsonValue o && o.TryGetValue(out bool on):
                    step.On = on;
                    break;
                case "id" when Str(value) is { } id:
                    step.Id = id;
                    break;
                case "until" when value is JsonObject until && until["all"] is JsonArray items:
                    step.Until = ConditionDoc.ParseItems(items);
                    break;
                default:
                    step.Fields[key] = ConditionDoc.Clone(value);
                    break;
            }
        }
        return step;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? text) ? text : null;

    internal static bool TryNumber(JsonValue json, out double value)
    {
        value = double.NaN;
        if (json.TryGetValue(out double d)) value = d;
        else if (json.TryGetValue(out float f)) value = f;
        else if (json.TryGetValue(out int i)) value = i;
        else if (json.TryGetValue(out long l)) value = l;
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    // ---- writing ----

    public JsonObject ToJson()
    {
        if (Newer != null) return (JsonObject)ConditionDoc.Clone(Newer)!;
        var sets = new JsonArray();
        foreach (var set in Sets) sets.Add(SetJson(set));
        var root = new JsonObject { ["v"] = Version, ["sets"] = sets };
        foreach (var (key, value) in Extra) root[key] = ConditionDoc.Clone(value);
        return root;
    }

    public string ToText() => ToJson().ToJsonString();

    /// <summary>A copy that can be changed and stored (Parse of this document's text).</summary>
    public MacroDoc Copy() => Parse(ToText(), out _)!;

    public static JsonNode? SetJson(MacroSet set)
    {
        if (set.Raw != null) return ConditionDoc.Clone(set.Raw);
        var macros = new JsonArray();
        foreach (var macro in set.Macros) macros.Add(MacroJson(macro));
        var node = new JsonObject { ["id"] = set.Id, ["name"] = set.Name, ["class"] = set.Class, ["on"] = set.On, ["macros"] = macros };
        foreach (var (key, value) in set.Extra) node[key] = ConditionDoc.Clone(value);
        return node;
    }

    private static JsonNode? MacroJson(Macro macro)
    {
        if (macro.Raw != null) return ConditionDoc.Clone(macro.Raw);
        var trigger = new JsonObject { ["kind"] = macro.Trigger.Kind };
        if (macro.Trigger.Key.Length > 0 || macro.Trigger.NeedsKey) trigger["key"] = macro.Trigger.Key;
        foreach (var (key, value) in macro.Trigger.Extra) trigger[key] = ConditionDoc.Clone(value);
        var ready = new JsonObject();
        foreach (var (skill, on) in macro.Ready) ready[skill] = on;
        var actions = new JsonArray();
        foreach (var step in macro.Actions) actions.Add(StepJson(step));
        var node = new JsonObject
        {
            ["id"] = macro.Id,
            ["name"] = macro.Name,
            ["on"] = macro.On,
            ["trigger"] = trigger,
            ["move"] = macro.Move,
            ["waitMs"] = macro.WaitMs,
            ["ready"] = ready,
            ["all"] = ConditionDoc.ItemsJson(macro.All),
            ["actions"] = actions,
        };
        foreach (var (key, value) in macro.Extra) node[key] = ConditionDoc.Clone(value);
        return node;
    }

    private static JsonNode? StepJson(MacroStep step)
    {
        if (step.Raw != null) return ConditionDoc.Clone(step.Raw);
        var node = new JsonObject { ["step"] = step.Step };
        if (step.Id != null) node["id"] = step.Id;
        if (!step.On) node["on"] = false;
        foreach (var (key, value) in step.Fields) node[key] = ConditionDoc.Clone(value);
        if (step.Until != null) node["until"] = new JsonObject { ["all"] = ConditionDoc.ItemsJson(step.Until) };
        return node;
    }

    // ---- ids ----

    /// <summary>An id with the prefix ("s", "m") that no set or macro in the document has.</summary>
    public string NewId(string prefix)
    {
        var used = new HashSet<string>(Sets.Select(s => s.Id).Concat(Sets.SelectMany(s => s.Macros).Select(m => m.Id)));
        for (int n = 1; ; n++)
        {
            string id = prefix + n;
            if (!used.Contains(id)) return id;
        }
    }

    /// <summary>A name that no set of the class has: the name, then " (2)", " (3)" …</summary>
    public string FreeSetName(string name, string cls)
    {
        if (!Sets.Any(s => s.Class == cls && s.Name == name)) return name;
        for (int n = 2; ; n++)
        {
            string suffix = $" ({n})";
            string candidate = (name.Length + suffix.Length > Macro.MaxName ? name[..(Macro.MaxName - suffix.Length)] : name) + suffix;
            if (!Sets.Any(s => s.Class == cls && s.Name == candidate)) return candidate;
        }
    }

    /// <summary>Gives a set and its macros ids this document doesn't use yet.</summary>
    public void Renumber(MacroSet set)
    {
        set.Id = string.Empty;
        foreach (var macro in set.Macros) macro.Id = string.Empty;
        set.Id = NewId("s");
        Sets.Add(set);
        foreach (var macro in set.Macros) macro.Id = NewId("m");
        Sets.Remove(set);
    }

    /// <summary>Sets of a class in library order.</summary>
    public IEnumerable<MacroSet> SetsOf(string cls) => Sets.Where(s => s.Class == cls);

    /// <summary>The set and macro with these ids, or nulls.</summary>
    public (MacroSet? Set, Macro? Macro) Find(string setId, string? macroId = null)
    {
        var set = Sets.FirstOrDefault(s => s.Id == setId);
        return (set, macroId == null ? null : set?.Macros.FirstOrDefault(m => m.Id == macroId));
    }

    /// <summary>Counts of parts this build can't read (unknown kinds, steps, fields), for an import's report.</summary>
    public static List<string> UnknownParts(MacroSet set)
    {
        var parts = new List<string>();
        foreach (var (key, _) in set.Extra) parts.Add($"set field {key}");
        foreach (var macro in set.Macros)
        {
            foreach (var (key, _) in macro.Extra) parts.Add($"{macro.Name}: field {key}");
            foreach (var node in macro.All.Nodes)
            {
                if (!node.IsGroup && node.Raw == null && ConditionKinds.Find(node.Kind) == null) parts.Add($"{macro.Name}: condition {node.Kind}");
            }
            foreach (var step in macro.Actions)
            {
                if (step.Raw == null && !step.Known) parts.Add($"{macro.Name}: step {step.Step}");
                else if (step.NeedsNewer) parts.Add($"{macro.Name}: {step.Step} step's target");
            }
        }
        return parts;
    }
}
