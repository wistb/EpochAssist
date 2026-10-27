using System.Text.Json.Nodes;

namespace EpochAssist.Core;

// Never stored: picks are stored by name (TargetSpec.PickNames), owners not at all.
internal enum TargetPick { Cursor, Nearest, Hovered, Rarest, MostHealth, MaxHealth, LowHealth, AreaHealth, AreaWeight, AreaCount }

/// <summary>What a target belongs to: a cast step, a slot macro (the shared target or a slot's own), or a condition line.
/// It decides the fallbacks offered and the words.</summary>
internal enum TargetOwner { Step, Slot, Condition }

/// <summary>
/// Which pick a target needs: the choice and its limits. Equal keys share one pick (the fallback isn't part of it, so
/// equal targets with different fallbacks still agree on the enemy). Ranges and radii compare within 0.01.
/// </summary>
internal readonly struct TargetKey : IEquatable<TargetKey>
{
    public TargetKey(TargetPick pick, float range, float radius, Rarity minRarity, int? withoutMod, int? withAilment)
    {
        Pick = pick;
        Range = range;
        Radius = radius;
        MinRarity = minRarity;
        WithoutMod = withoutMod;
        WithAilment = withAilment;
    }

    public TargetPick Pick { get; }
    public float Range { get; }
    /// <summary>Area picks only; 0 for the others.</summary>
    public float Radius { get; }
    public Rarity MinRarity { get; }
    public int? WithoutMod { get; }
    public int? WithAilment { get; }

    public bool IsArea => Pick >= TargetPick.AreaHealth;

    public bool Equals(TargetKey other) =>
        Pick == other.Pick && MinRarity == other.MinRarity && WithoutMod == other.WithoutMod && WithAilment == other.WithAilment
        && MathF.Abs(Range - other.Range) <= 0.01f && MathF.Abs(Radius - other.Radius) <= 0.01f;

    public override bool Equals(object? obj) => obj is TargetKey other && Equals(other);

    // Ranges and radii are set in 0.5 m steps, so rounding to them keeps equal keys' hashes equal.
    public override int GetHashCode() =>
        HashCode.Combine(Pick, MinRarity, WithoutMod, WithAilment, (int)MathF.Round(Range * 2f), (int)MathF.Round(Radius * 2f));

    public static bool operator ==(TargetKey a, TargetKey b) => a.Equals(b);
    public static bool operator !=(TargetKey a, TargetKey b) => !a.Equals(b);
}

/// <summary>
/// What a cast lands on, or what a condition line is about (contracts/target-format.md): a pick, a range from the hero,
/// an area radius, the limits (at least a rarity, not with a monster mod, only with an ailment) and what to do when
/// nothing qualifies. Immutable; parsed from and written to a "target" object. A target with a key or value this build
/// doesn't know keeps its stored object and reads "needs a newer version".
/// </summary>
internal sealed class TargetSpec
{
    public static readonly string[] PickNames = { "cursor", "nearest", "hovered", "rarest", "mostHealth", "maxHealth", "lowHealth", "areaHealth", "areaWeight", "areaCount" };
    public static readonly string[] RarityNames = { "normal", "magic", "rare", "boss" };
    public static readonly string[] StepElse = { "cursor", "skip", "wait" };
    public static readonly string[] SlotElse = { "cursor", "hold" };
    private static readonly string[] s_keys = { "pick", "range", "radius", "minRarity", "withoutMod", "withAilment", "else" };

    public const float MinRange = 2f, MaxRange = 60f, DefaultRange = 15f;
    public const float MinRadius = 1f, MaxRadius = 15f, DefaultRadius = 4f, RadiusStep = 0.5f;
    /// <summary>The world model's reach: an old "hovered" aim had no range of its own.</summary>
    public const float HoveredRange = 60f;
    public const string NewerVersion = "needs a newer version";

    public TargetPick Pick { get; private init; }
    public float Range { get; private init; } = DefaultRange;
    public float Radius { get; private init; } = DefaultRadius;
    public Rarity MinRarity { get; private init; }
    public int? WithoutMod { get; private init; }
    public int? WithAilment { get; private init; }
    /// <summary>"cursor", "skip", "wait" (steps) or "hold" (slots); data from before targets reads "cursor".</summary>
    public string Else { get; private init; } = "cursor";
    /// <summary>The stored object of a target this build can't read, written back as it was.</summary>
    public JsonObject? Newer { get; private init; }
    /// <summary>Why a stored object isn't a target at all (not "needs a newer version"), or null.</summary>
    private string? Broken { get; init; }

    public static readonly TargetSpec Cursor = new();

    public bool IsArea => Pick >= TargetPick.AreaHealth;
    /// <summary>A pick of one enemy (nearest, hovered and the ranked ones).</summary>
    public bool IsEnemy => Pick is not TargetPick.Cursor && !IsArea;
    /// <summary>Picked in the enemy scan and kept steady; cursor, nearest and hovered are resolved at the send.</summary>
    public bool Sticky => Pick is not (TargetPick.Cursor or TargetPick.Nearest or TargetPick.Hovered);
    public bool HasLimits => MinRarity != Rarity.Normal || WithoutMod != null || WithAilment != null;

    public TargetKey Key => new(Pick, Range, IsArea ? Radius : 0f, MinRarity, WithoutMod, WithAilment);

    /// <summary>
    /// The target means exactly one of the three aims from before targets: at cursor; nearest within a range, else at
    /// cursor, no other limit; hovered with the world's reach, else at cursor, no other limit. Those keep the old stored
    /// shape (and share codes their old text).
    /// </summary>
    public bool Legacy => Newer == null && Broken == null && (Pick == TargetPick.Cursor
        || (Pick == TargetPick.Nearest && !HasLimits && Else == "cursor")
        || (Pick == TargetPick.Hovered && !HasLimits && Else == "cursor" && MathF.Abs(Range - HoveredRange) <= 0.01f));

    /// <summary>Why the target can't be used by this owner, or null.</summary>
    public string? Invalid(TargetOwner owner)
    {
        if (Broken != null) return Broken;
        if (Newer != null) return NewerVersion;
        if (Pick == TargetPick.Cursor) return owner == TargetOwner.Condition ? "a condition needs an enemy" : null;
        if (Range < MinRange - 1e-4f || Range > MaxRange + 1e-4f || float.IsNaN(Range)) return $"range {ArgSpec.Format(Range)} is outside {ArgSpec.Format(MinRange)}–{ArgSpec.Format(MaxRange)}";
        if (IsArea && (Radius < MinRadius - 1e-4f || Radius > MaxRadius + 1e-4f || float.IsNaN(Radius)
            || MathF.Abs(Radius / RadiusStep - MathF.Round(Radius / RadiusStep)) > 1e-3f))
        {
            return $"radius {ArgSpec.Format(Radius)} isn't a multiple of 0.5 within {ArgSpec.Format(MinRadius)}–{ArgSpec.Format(MaxRadius)}";
        }
        if (owner != TargetOwner.Condition && !ElseFor(owner).Contains(Else)) return $"else isn't one of {string.Join(", ", ElseFor(owner))}";
        return null;
    }

    /// <summary>The fallbacks an owner offers, in the row's order.</summary>
    public static string[] ElseFor(TargetOwner owner) => owner == TargetOwner.Slot ? SlotElse : StepElse;

    /// <summary>A new target's fallback: a step skips, a slot doesn't cast.</summary>
    public static string NewElse(TargetOwner owner) => owner == TargetOwner.Slot ? "hold" : "skip";

    // ---- making ----

    /// <summary>A target with a new pick, keeping this one's range, radius, limits and fallback (a cursor target taking an
    /// enemy pick gets the owner's new-target fallback).</summary>
    public TargetSpec WithPick(TargetPick pick, TargetOwner owner) => new()
    {
        Pick = pick,
        Range = Range,
        Radius = Radius,
        MinRarity = pick == TargetPick.Cursor ? Rarity.Normal : MinRarity,
        WithoutMod = pick == TargetPick.Cursor ? null : WithoutMod,
        WithAilment = pick == TargetPick.Cursor ? null : WithAilment,
        Else = Pick == TargetPick.Cursor || Newer != null || !ElseFor(owner).Contains(Else) ? NewElse(owner) : Else,
    };

    public TargetSpec WithRange(float range) => Copy(range: range);
    public TargetSpec WithRadius(float radius) => Copy(radius: radius);
    public TargetSpec WithMinRarity(Rarity rarity) => Copy(minRarity: rarity);
    public TargetSpec WithoutModLimit(int? mod) => Copy(withoutMod: mod, setMod: true);
    public TargetSpec WithAilmentLimit(int? ailment) => Copy(withAilment: ailment, setAilment: true);
    public TargetSpec WithElse(string fallback) => Copy(fallback: fallback);

    private TargetSpec Copy(float? range = null, float? radius = null, Rarity? minRarity = null, int? withoutMod = null, bool setMod = false,
        int? withAilment = null, bool setAilment = false, string? fallback = null) => new()
    {
        Pick = Pick,
        Range = range ?? Range,
        Radius = radius ?? Radius,
        MinRarity = minRarity ?? MinRarity,
        WithoutMod = setMod ? withoutMod : WithoutMod,
        WithAilment = setAilment ? withAilment : WithAilment,
        Else = fallback ?? Else,
    };

    /// <summary>The target a key picks for (fallback at cursor), for words where only the key is known (debug, dev).</summary>
    public static TargetSpec FromKey(in TargetKey key) => new()
    {
        Pick = key.Pick,
        Range = key.Range,
        Radius = key.IsArea ? key.Radius : DefaultRadius,
        MinRarity = key.MinRarity,
        WithoutMod = key.WithoutMod,
        WithAilment = key.WithAilment,
    };

    /// <summary>The nearest enemy within a range, else at cursor: the old "nearest" aim and the old slot aiming switch.</summary>
    public static TargetSpec OldNearest(float range) => new() { Pick = TargetPick.Nearest, Range = range };

    /// <summary>The old "hovered" aim: the hovered enemy anywhere in reach, else at cursor.</summary>
    public static TargetSpec OldHovered() => new() { Pick = TargetPick.Hovered, Range = HoveredRange };

    /// <summary>A target that can't be read here, keeping what was stored.</summary>
    public static TargetSpec Unreadable(JsonNode? stored, string? broken = null) =>
        new() { Newer = stored as JsonObject ?? new JsonObject(), Broken = broken };

    /// <summary>
    /// A stored target object. A key this build doesn't know, or an unknown pick or else value, reads "needs a newer
    /// version" and keeps the object; a pick that is missing or a number that isn't one makes it broken.
    /// </summary>
    public static TargetSpec FromObject(JsonNode? node)
    {
        if (node is not JsonObject obj) return Unreadable(null, "target isn't an object");
        foreach (var (key, _) in obj)
        {
            if (Array.IndexOf(s_keys, key) < 0) return Unreadable(obj);
        }
        if (Str(obj["pick"]) is not { } pickName) return new TargetSpec { Newer = (JsonObject)ConditionDoc.Clone(obj)!, Broken = "target has no pick" };
        int pick = Array.IndexOf(PickNames, pickName);
        if (pick < 0) return Unreadable(ConditionDoc.Clone(obj));
        string fallback = obj.ContainsKey("else") ? Str(obj["else"]) ?? "?" : "cursor";
        if (Array.IndexOf(StepElse, fallback) < 0 && Array.IndexOf(SlotElse, fallback) < 0) return Unreadable(ConditionDoc.Clone(obj));
        string rarity = obj.ContainsKey("minRarity") ? Str(obj["minRarity"]) ?? "?" : "normal";
        int rarityIndex = Array.IndexOf(RarityNames, rarity);
        if (rarityIndex < 0) return Unreadable(ConditionDoc.Clone(obj));
        if (!Number(obj, "range", DefaultRange, out float range) || !Number(obj, "radius", DefaultRadius, out float radius)
            || !Id(obj, "withoutMod", out int? mod, signed: true) || !Id(obj, "withAilment", out int? ailment))
        {
            return new TargetSpec { Newer = (JsonObject)ConditionDoc.Clone(obj)!, Broken = "a target number isn't one" };
        }
        return new TargetSpec
        {
            Pick = (TargetPick)pick,
            Range = range,
            Radius = radius,
            MinRarity = (Rarity)rarityIndex,
            WithoutMod = mod,
            WithAilment = ailment,
            Else = fallback,
        };
    }

    /// <summary>The target as stored: the pick, then only what it uses, with else always written (so a reading default
    /// only ever applies to data from before targets).</summary>
    public JsonObject ToObject()
    {
        if (Newer != null) return (JsonObject)ConditionDoc.Clone(Newer)!;
        var obj = new JsonObject { ["pick"] = PickNames[(int)Pick] };
        if (Pick == TargetPick.Cursor) return obj;
        obj["range"] = Range;
        if (IsArea) obj["radius"] = Radius;
        if (MinRarity != Rarity.Normal) obj["minRarity"] = RarityNames[(int)MinRarity];
        if (WithoutMod is int mod) obj["withoutMod"] = mod;
        if (WithAilment is int ailment) obj["withAilment"] = ailment;
        obj["else"] = Else;
        return obj;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? text) ? text : null;

    private static bool Number(JsonObject obj, string name, float fallback, out float value)
    {
        value = fallback;
        if (!obj.TryGetPropertyValue(name, out var node)) return true;
        if (node is not JsonValue v || !MacroDoc.TryNumber(v, out double d)) return false;
        value = (float)d;
        return true;
    }

    // An ailment id (0 and up), or with signed a monster mod key (the game's hash, any int).
    private static bool Id(JsonObject obj, string name, out int? value, bool signed = false)
    {
        value = null;
        if (!obj.TryGetPropertyValue(name, out var node) || node == null) return true;
        if (node is not JsonValue v || !MacroDoc.TryNumber(v, out double d) || d < (signed ? int.MinValue : 0) || d > int.MaxValue
            || Math.Abs(d - Math.Round(d)) > 1e-6) return false;
        value = (int)Math.Round(d);
        return true;
    }

    public static Rarity RarityOf(string name) => Array.IndexOf(RarityNames, name) is int i and >= 0 ? (Rarity)i : Rarity.Normal;

    // ---- words (contracts/target-words.md) ----

    /// <summary>A pick's label in the Target row: "rarest enemy", "centre of the most enemies".</summary>
    public static string Label(TargetPick pick) => pick switch
    {
        TargetPick.Nearest => "nearest enemy",
        TargetPick.Hovered => "hovered enemy",
        TargetPick.Rarest => "rarest enemy",
        TargetPick.MostHealth => "enemy with the most health",
        TargetPick.MaxHealth => "enemy with the highest max health",
        TargetPick.LowHealth => "enemy with the lowest health %",
        TargetPick.AreaHealth => "centre of the most enemy health",
        TargetPick.AreaWeight => "centre of the most enemy weight",
        TargetPick.AreaCount => "centre of the most enemies",
        _ => "at cursor",
    };

    /// <summary>The Target row's options in order ("at cursor", then the enemy picks, then the area picks) and its headings.</summary>
    public static readonly TargetPick[] Choices = (TargetPick[])Enum.GetValues(typeof(TargetPick));
    public static readonly (string Label, int First)[] ChoiceGroups = { ("Enemy", 1), ("Area", 7) };

    /// <summary>A fallback's words as the row offers it: "at cursor", "skip the step", "wait for a target", "don't cast".</summary>
    public static string ElseLabel(string fallback) => fallback switch
    {
        "skip" => "skip the step",
        "wait" => "wait for a target",
        "hold" => "don't cast",
        _ => "at cursor",
    };

    // The fallback in a sentence: "else at cursor", "else skip", "else wait", "else don't cast".
    private static string ElseWords(string fallback) => fallback switch
    {
        "skip" => "else skip",
        "wait" => "else wait",
        "hold" => "else don't cast",
        _ => "else at cursor",
    };

    /// <summary>"rare or better", "magic or better", "bosses only"; empty for any.</summary>
    public static string RarityWords(Rarity rarity) => rarity switch
    {
        Rarity.Magic => "magic or better",
        Rarity.Rare => "rare or better",
        Rarity.Boss => "bosses only",
        _ => string.Empty,
    };

    // "rarest enemy within 20 m" / "centre of the most enemies (4 m) within 20 m" / "hovered enemy" (in reach).
    private string Choice()
    {
        string label = Label(Pick);
        if (IsArea) label += $" ({ArgSpec.Format(Radius)} m)";
        bool showRange = !(Pick == TargetPick.Hovered && MathF.Abs(Range - HoveredRange) <= 0.01f);
        return showRange ? $"{label} within {ArgSpec.Format(Range)} m" : label;
    }

    // ", rare or better, not with Reflects Damage, only with Shocked"; empty with no limit.
    private string LimitWords()
    {
        string text = string.Empty;
        if (RarityWords(MinRarity) is { Length: > 0 } rarity) text += ", " + rarity;
        if (WithoutMod is int mod) text += $", not with {GameData.MonsterMod(mod).Name ?? $"mod {mod}"}";
        if (WithAilment is int ailment) text += $", only with {ConditionKinds.BuffName(ailment)}";
        return text;
    }

    /// <summary>
    /// The target in its owner's words. A step: "at the rarest enemy within 20 m, rare or better, else at cursor" (the
    /// old aims read as before: "at cursor", "nearest enemy within 15 m", "hovered enemy"). A slot: "rarest enemy within
    /// 15 m, else don't cast". A condition: "Rarest enemy within 20 m, rare or better".
    /// </summary>
    public string Words(TargetOwner owner)
    {
        // A step's line adds the reason itself.
        if (Newer != null) return owner == TargetOwner.Step ? "at a target this version can't read" : Broken ?? NewerVersion;
        if (Pick == TargetPick.Cursor) return "at cursor";
        switch (owner)
        {
            case TargetOwner.Step:
                if (Legacy) return Choice();
                return $"at the {Choice()}{LimitWords()}, {ElseWords(Else)}";
            case TargetOwner.Slot:
                return $"{Choice()}{LimitWords()}, {ElseWords(Else)}";
            default:
                string words = Choice() + LimitWords();
                return char.ToUpperInvariant(words[0]) + words[1..];
        }
    }
}
