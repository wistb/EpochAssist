using System.Globalization;
using System.Text.Json.Nodes;

namespace EpochAssist.Core;

// Never stored: entries store args by name and choices by option name.
internal enum ArgKind { Number, Choice, Buff, Slot, Rarity, Key, Skill, Mod, Ailment, Bool }
internal enum ConditionState { Pass, Fail, Unknown, Invalid }

/// <summary>
/// One argument of a condition kind: its stored name, its kind and default, and for numbers the range, step and unit,
/// for choices the option names. A stored value outside these makes the entry invalid (it is kept).
/// </summary>
internal sealed class ArgSpec
{
    private ArgSpec(string name, ArgKind kind, JsonNode? defaultValue)
    {
        Name = name;
        Kind = kind;
        Default = defaultValue;
    }

    public string Name { get; }
    public ArgKind Kind { get; }
    /// <summary>The value a new entry gets; null for a buff or key the player still has to pick.</summary>
    public JsonNode? Default { get; }
    public float Min { get; private init; }
    public float Max { get; private init; }
    public float Step { get; private init; } = 1f;
    public string Unit { get; private init; } = string.Empty;
    /// <summary>Choice and rarity options, by stored name.</summary>
    public string[] Options { get; private init; } = Array.Empty<string>();
    /// <summary>A missing value reads as Default (or as no value when there is none) instead of making the entry invalid,
    /// so an argument added later leaves stored entries meaning what they meant.</summary>
    public bool Optional { get; init; }
    /// <summary>A choice whose stored value this build doesn't know was written by a newer one: it reads "needs a newer
    /// version" rather than "isn't one of".</summary>
    public bool UnknownIsNewer { get; private init; }

    /// <summary>The comparisons a "compare" arg offers, in menu order.</summary>
    public static readonly string[] Comparisons = { "below", "above", "atLeast", "atMost", "between" };

    public static readonly string[] Rarities = { "magic", "rare", "boss" };

    public static ArgSpec Number(string name, float defaultValue, float min, float max, float step, string unit = "") =>
        new(name, ArgKind.Number, JsonValue.Create(defaultValue)) { Min = min, Max = max, Step = step, Unit = unit };

    public static ArgSpec Choice(string name, string defaultValue, params string[] options) =>
        new(name, ArgKind.Choice, JsonValue.Create(defaultValue)) { Options = options };

    public static ArgSpec Buff(string name) => new(name, ArgKind.Buff, null);

    /// <summary>0 is the slot the list belongs to, 1–5 a bar slot.</summary>
    public static ArgSpec Slot(string name) => new(name, ArgKind.Slot, JsonValue.Create(0)) { Min = 0, Max = 5 };

    public static ArgSpec Rarity(string name, string defaultValue) =>
        new(name, ArgKind.Rarity, JsonValue.Create(defaultValue)) { Options = Rarities };

    public static ArgSpec Key(string name) => new(name, ArgKind.Key, JsonValue.Create(string.Empty));

    /// <summary>A yes/no, stored as true or false.</summary>
    public static ArgSpec Bool(string name, bool defaultValue) => new(name, ArgKind.Bool, JsonValue.Create(defaultValue));

    /// <summary>A skill by ability id ("smbmb"); the player picks it.</summary>
    public static ArgSpec Skill(string name) => new(name, ArgKind.Skill, null);

    /// <summary>A monster mod by its key (the game's hash, any int); the player picks it.</summary>
    public static ArgSpec Mod(string name) => new(name, ArgKind.Mod, null);

    /// <summary>An ailment by id (0–255), as buffs are; the player picks it.</summary>
    public static ArgSpec Ailment(string name) => new(name, ArgKind.Ailment, null);

    /// <summary>A comparison: the kind's original ones first (so stored values keep meaning), then the rest.</summary>
    public static ArgSpec Compare(string defaultValue, params string[] first) =>
        Choice("compare", defaultValue, first.Concat(Comparisons.Where(c => !first.Contains(c))).ToArray());

    /// <summary>A copy that may be missing (reading as its default).</summary>
    public ArgSpec AsOptional() => new(Name, Kind, Default) { Min = Min, Max = Max, Step = Step, Unit = Unit, Options = Options, Optional = true, UnknownIsNewer = UnknownIsNewer };

    /// <summary>A copy whose unknown stored choices read "needs a newer version".</summary>
    public ArgSpec NewerWhenUnknown() => new(Name, Kind, Default) { Min = Min, Max = Max, Step = Step, Unit = Unit, Options = Options, Optional = Optional, UnknownIsNewer = true };

    /// <summary>An optional number with no default: absent reads as no value (NaN).</summary>
    public static ArgSpec OptionalNumber(string name, float min, float max, float step, string unit = "") =>
        new(name, ArgKind.Number, null) { Min = min, Max = max, Step = step, Unit = unit, Optional = true };

    /// <summary>A stored value read as this arg: a number in range and on a step, an option, a buff id, or chord text.
    /// Null with the reason when it doesn't fit.</summary>
    public bool TryRead(JsonNode? node, out float number, out string? text, out string reason)
    {
        number = 0f;
        text = null;
        reason = string.Empty;
        if (node == null && Optional)
        {
            if (Default == null)
            {
                number = float.NaN;
                return true;
            }
            node = Default;
        }
        switch (Kind)
        {
            case ArgKind.Number:
            case ArgKind.Slot:
                if (!TryNumber(node, out double d))
                {
                    reason = node == null ? $"{Name} missing" : $"{Name} isn't a number";
                    return false;
                }
                if (d < Min - 1e-4 || d > Max + 1e-4)
                {
                    reason = $"{Name} {Format(d)} is outside {Format(Min)}–{Format(Max)}";
                    return false;
                }
                // On a step from the minimum, within float noise.
                double steps = Step > 0f ? (d - Min) / Step : 0;
                if (Math.Abs(steps - Math.Round(steps)) > 1e-3)
                {
                    reason = $"{Name} {Format(d)} isn't a multiple of {Format(Step)}";
                    return false;
                }
                number = (float)d;
                return true;
            case ArgKind.Choice:
            case ArgKind.Rarity:
                if (node is JsonValue cv && cv.TryGetValue(out string? option) && Options.Contains(option))
                {
                    text = option;
                    return true;
                }
                reason = node == null ? $"{Name} missing"
                    : UnknownIsNewer && node is JsonValue unknown && unknown.TryGetValue(out string? _) ? TargetSpec.NewerVersion
                    : $"{Name} isn't one of {string.Join(", ", Options)}";
                return false;
            case ArgKind.Buff:
            case ArgKind.Ailment:
                if (TryNumber(node, out double id) && id >= 0 && id <= 255 && Math.Abs(id - Math.Round(id)) < 1e-6)
                {
                    number = (float)Math.Round(id);
                    return true;
                }
                reason = Kind == ArgKind.Buff ? "select buff" : "select ailment";
                return false;
            case ArgKind.Mod:
                // Mod keys are the game's hashes: any int, negative ones included.
                if (TryNumber(node, out double mod) && mod >= int.MinValue && mod <= int.MaxValue && Math.Abs(mod - Math.Round(mod)) < 1e-6)
                {
                    number = (float)Math.Round(mod);
                    return true;
                }
                reason = "select monster mod";
                return false;
            case ArgKind.Bool:
                if (node is JsonValue bv && bv.TryGetValue(out bool flag))
                {
                    number = flag ? 1f : 0f;
                    return true;
                }
                reason = $"{Name} isn't true or false";
                return false;
            case ArgKind.Skill:
                if (node is JsonValue sv && sv.TryGetValue(out string? skill) && skill.Length > 0)
                {
                    text = skill;
                    return true;
                }
                reason = "select skill";
                return false;
            case ArgKind.Key:
                if (node is JsonValue kv && kv.TryGetValue(out string? chordText) && Chord.TryParse(chordText, HotkeyKind.Hold, out var chord) && !chord.IsEmpty)
                {
                    text = chord.Text;
                    return true;
                }
                reason = "set a key";
                return false;
        }
        reason = $"{Name}: unknown arg kind";
        return false;
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = double.NaN;
        if (node is not JsonValue json) return false;
        if (json.TryGetValue(out double d)) value = d;
        else if (json.TryGetValue(out float f)) value = f;
        else if (json.TryGetValue(out int i)) value = i;
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"below 50%", "at least 3", "between 30% and 60%": a comparison's words with the unit after each number.</summary>
    public static string CompareWords(string compare, float limit, float to, string unit) => compare switch
    {
        "above" => $"above {Format(limit)}{unit}",
        "atLeast" => $"at least {Format(limit)}{unit}",
        "atMost" => $"at most {Format(limit)}{unit}",
        "between" => $"between {Format(limit)}{unit} and {(float.IsNaN(to) ? "?" : Format(to))}{unit}",
        _ => $"below {Format(limit)}{unit}",
    };

    /// <summary>The words for an option a menu shows ("at least" for atLeast).</summary>
    public static string OptionWords(string option) => option switch
    {
        "atLeast" => "at least",
        "atMost" => "at most",
        "potion" => "a potion's heal",
        "target" => "a target",
        "cast" => "the cast target",
        _ => option,
    };
}

/// <summary>An entry's arguments, read once when its document is parsed, by name. Reading them allocates nothing.</summary>
internal readonly struct ArgValues
{
    private readonly ArgSpec[] _specs;
    private readonly float[] _numbers;
    private readonly string?[] _texts;

    public ArgValues(ArgSpec[] specs, float[] numbers, string?[] texts)
    {
        _specs = specs;
        _numbers = numbers;
        _texts = texts;
    }

    private int IndexOf(string name)
    {
        if (_specs == null) return -1;
        for (int i = 0; i < _specs.Length; i++)
        {
            if (_specs[i].Name == name) return i;
        }
        return -1;
    }

    /// <summary>A number, slot or buff arg; 0 when the kind has no such arg.</summary>
    public float Number(string name) => IndexOf(name) is int i and >= 0 ? _numbers[i] : 0f;

    public int Int(string name) => (int)Math.Round(Number(name));

    /// <summary>Whether a number arg has a value (an optional one without a default may not).</summary>
    public bool Has(string name) => IndexOf(name) is int i and >= 0 && (_texts[i] != null || !float.IsNaN(_numbers[i]));

    /// <summary>A yes/no arg; false when the kind has no such arg.</summary>
    public bool Flag(string name) => Number(name) > 0.5f;

    /// <summary>
    /// The value against the entry's compare (below when absent), its limit arg and, for between, "to" (both ends
    /// included). Allocates nothing.
    /// </summary>
    public bool Compare(float value, string limit) => Compare(value, Text("compare"), Number(limit), Number("to"));

    public static bool Compare(float value, string compare, float limit, float to) => compare switch
    {
        "above" => value > limit,
        "atLeast" => value >= limit - 1e-4f,
        "atMost" => value <= limit + 1e-4f,
        "between" => value >= limit - 1e-4f && value <= to + 1e-4f,
        _ => value < limit,
    };

    /// <summary>"set an upper bound" when compare is between and "to" is missing or below the limit; else null.</summary>
    public string? BetweenInvalid(string limit) =>
        Text("compare") == "between" && (!Has("to") || Number("to") < Number(limit) - 1e-4f) ? "set an upper bound" : null;

    /// <summary>A choice, rarity or key arg's stored text; empty when the kind has no such arg.</summary>
    public string Text(string name) => IndexOf(name) is int i and >= 0 ? _texts[i] ?? string.Empty : string.Empty;
}

/// <summary>
/// What one check of one entry found: pass, fail, unknown (a value couldn't be read; the entry's switch decides) or
/// invalid (the entry can't be checked here; it fails). Number and Note are the value it saw, formatted on demand by
/// the kind; Reason says why it is unknown or invalid. Building one allocates nothing.
/// </summary>
internal readonly record struct ConditionResult(ConditionState State, float Number = float.NaN, string? Note = null, string? Reason = null)
{
    public static ConditionResult Pass(float number = float.NaN, string? note = null) => new(ConditionState.Pass, number, note);
    public static ConditionResult Fail(float number = float.NaN, string? note = null) => new(ConditionState.Fail, number, note);
    public static ConditionResult Unknown(string reason) => new(ConditionState.Unknown, Reason: reason);
    public static ConditionResult Invalid(string reason) => new(ConditionState.Invalid, Reason: reason);
    public static ConditionResult Of(bool pass, float number = float.NaN, string? note = null) => pass ? Pass(number, note) : Fail(number, note);

    public bool HasValue => !float.IsNaN(Number) || Note != null;
}

/// <summary>What a check is about: the slot the list belongs to (0-based, -1 for none), the owner's enemy tally, and now;
/// for a library macro's trees, its id.</summary>
internal readonly record struct ConditionContext(int Slot, WorldTally? Tally, float Now)
{
    public string? Macro { get; init; }
    /// <summary>What "the cast target" means for the list being checked: a slot's effective target, the shared slot
    /// target, or a library macro's cast step's; null where the list has no cast to take it from.</summary>
    public TargetSpec? CastTarget { get; init; }

    /// <summary>A slot arg as a bar slot number (1–5): 0 means the list's own slot; 0 when there is none.</summary>
    public int SlotNumber(int arg) => arg > 0 ? arg : Slot >= 0 ? Slot + 1 : 0;
}

/// <summary>
/// A declared kind of condition: its permanent id, subject, args, the model parts it reads, its words and its check.
/// Kinds read the player model, the world tally, game data and the hotkey layer only; they never send.
/// </summary>
internal sealed class ConditionKind
{
    public ConditionKind(string id, string subject, string label)
    {
        Id = id;
        Subject = subject;
        Label = label;
    }

    /// <summary>Dotted and permanent ("player.health"); never reused.</summary>
    public string Id { get; }
    /// <summary>player, buff, slot, enemies or input: a string, since it may appear in saved text later.</summary>
    public string Subject { get; }
    /// <summary>The "+ Add condition" entry.</summary>
    public string Label { get; }
    public List<ArgSpec> Args { get; } = new();
    public List<PartNeed> Parts { get; } = new();
    /// <summary>The "True when unresolvable" switch of a new entry.</summary>
    public bool TrueWhenUnknown { get; init; }
    /// <summary>Reads the owner's enemy tally (counted a few times a second) rather than this frame's models.</summary>
    public bool Scanned { get; init; }
    /// <summary>For scanned kinds: the range in metres the entry needs counted.</summary>
    public Func<ArgValues, float>? Range { get; init; }
    /// <summary>For enemy kinds: which enemy the entry is about, as the owner's scan picks it (null: a target's pick,
    /// read from the targets instead).</summary>
    public Func<ArgValues, SubjectKey?>? Picks { get; init; }
    /// <summary>For scanned kinds: what the entry needs counted (range, centre, weights); null reads Range around the
    /// hero with the owner's weights.</summary>
    public Func<ArgValues, TallyKey>? TallyKey { get; init; }

    /// <summary>The count a scanned entry needs, or null for a kind that isn't scanned.</summary>
    public TallyKey? KeyFor(ArgValues values) =>
        !Scanned ? null : TallyKey != null ? TallyKey(values) : Range != null ? new TallyKey(Range(values), false) : null;
    public Func<ConditionContext, ArgValues, ConditionResult> Check { get; init; } = (_, _) => ConditionResult.Unknown("no check");
    /// <summary>Why read args can't be checked together (between without an upper bound), or null.</summary>
    public Func<ArgValues, string?>? Validate { get; init; }
    /// <summary>The entry's sentence: "Health below 50%".</summary>
    public Func<ArgValues, string> Words { get; init; } = _ => string.Empty;
    /// <summary>The words after an entry's label, when they read better shorter ("Enemy gate: weight 1 within 15 m").</summary>
    public Func<ArgValues, string>? LabelledWords { get; init; }
    /// <summary>Why it holds a slot back when it fails, as the status shows it: "health not below 50%".</summary>
    public Func<ArgValues, string>? FailText { get; init; }
    /// <summary>A result's value as the readout shows it ("43%", "4.1 s", "weight 6").</summary>
    public Func<ConditionResult, string>? ValueText { get; init; }

    private ArgSpec[]? _specs;
    internal ArgSpec[] Specs => _specs ??= Args.ToArray();

    public string ShowValue(ConditionResult result)
    {
        if (result.Note != null && float.IsNaN(result.Number)) return result.Note;
        if (ValueText != null) return ValueText(result);
        return float.IsNaN(result.Number) ? string.Empty : ArgSpec.Format(result.Number);
    }
}
