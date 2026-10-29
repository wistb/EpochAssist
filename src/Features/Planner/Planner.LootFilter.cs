using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;

namespace EpochAssist.Features;

/// <summary>The strictness steps a rule is on at, as a 4-bit set: Levelling 0, Regular 1, Strict 2, Very strict 3.</summary>
[Flags]
internal enum Steps : byte
{
    None = 0,
    Levelling = 1,
    Regular = 2,
    Strict = 4,
    VeryStrict = 8,
    All = 15,
}

/// <summary>The UTILITY: rule groups, each switched on or off as a whole.</summary>
internal enum UtilityGroup { Ascend, Havoc, ClassShatter, LowShards, Shatter }

/// <summary>Where a plan rule comes from: the planner's own kinds, EA · Base's table, or a foreign base filter.</summary>
internal enum RuleOrigin { Planner, EaBase, Base }

/// <summary>A rule's look as game ids: the colour (null = no recolour), emphasis, sound, map icon and beam.</summary>
internal sealed class RuleLook
{
    public int? Colour;
    public bool Emphasized;
    public int Sound;
    public int MapIcon;
    /// <summary>The game's beam size (0 none … 7 smallest) and colour id; null = the game's own beam.</summary>
    public (int Size, int Colour)? Beam;

    public RuleLook Copy() => (RuleLook)MemberwiseClone();
}

/// <summary>An affix condition: the ids, the tier bar as the game shows tiers (0 = any), the count on one item, and an
/// optional sum of tiers.</summary>
internal sealed class AffixNeed
{
    public List<int> Ids = new();
    public int TierBar;
    public int Min = 1;
    public int? Sum;
    /// <summary>The game's own comparison fields as an edit in its editor left them, used as they are instead of the tier
    /// bar and sum: comparison and value, combined comparison and value, and the advanced flag.</summary>
    public (int Cmp, int Value, int CombinedCmp, int CombinedValue, bool Advanced)? Raw;

    public AffixNeed Copy() => new() { Ids = Ids.ToList(), TierBar = TierBar, Min = Min, Sum = Sum, Raw = Raw };
}

/// <summary>What a rule matches; every set field is one game condition, all of them together.</summary>
internal sealed class RuleConditions
{
    public List<int>? Types;
    public List<int>? SubTypes;
    public List<AffixNeed> Affixes = new();
    public List<ushort>? Uniques;
    /// <summary>Minimum legendary potential and weaver's will; null fields are not asked.</summary>
    public (int? Lp, int? Ww)? Potential;
    /// <summary>Minimum forging potential (in the game's potential condition, with LP and WW); null = not asked.</summary>
    public int? Forging;
    /// <summary>A kind of non-equipment item, every one of it (the game's own condition for it); null = not asked.</summary>
    public ResourceKind? Resource;
    /// <summary>The game's rarity flags (1 normal … 64 exalted); 0 = not asked.</summary>
    public int Rarity;
    /// <summary>0 any, 1 corrupted only, 2 uncorrupted only.</summary>
    public int Corruption;
    public (int Min, int Max)? Levels;
    /// <summary>The game's class flags (1 primalist, 2 mage, 4 sentinel, 8 acolyte, 16 rogue); 0 = not asked.</summary>
    public int Classes;
    /// <summary>The game's faction ids the item was found for (0 Circle of Fortune, 1 Merchant's Guild); null = not asked.</summary>
    public List<int>? Factions;

    public RuleConditions Copy() => new()
    {
        Types = Types?.ToList(), SubTypes = SubTypes?.ToList(), Affixes = Affixes.Select(a => a.Copy()).ToList(), Uniques = Uniques?.ToList(),
        Potential = Potential, Rarity = Rarity, Corruption = Corruption, Levels = Levels, Classes = Classes, Factions = Factions?.ToList(), Forging = Forging, Resource = Resource,
    };
}

/// <summary>The non-equipment kinds a rule can show or hide, each with the game's condition for all of that kind.</summary>
internal enum ResourceKind { Runes, Glyphs, Keys, WovenEchoes, Shards, Resonances }

/// <summary>A planned slot's rule settings: the counted affixes, the minimum on one item, the tier bar and the bases.</summary>
internal sealed class SlotPlan
{
    public string Slot = string.Empty;
    /// <summary>The planned piece's own base.</summary>
    public int ItemType;
    public int SubType;
    /// <summary>The slot's planned affixes the game knows, and those that count.</summary>
    public List<int> Planned = new();
    public List<int> Counted = new();
    /// <summary>The planned tiers as the game shows them, by affix id.</summary>
    public Dictionary<int, int> Tiers = new();
    public int Min;
    public int TierBar;
    public List<int> Types = new();
    public List<int> SubTypes = new();
}

/// <summary>One rule of a plan, as plain data until Make.</summary>
internal sealed class PlanRule
{
    /// <summary>Stable id for choices: unique.lp3, slot.ring1.main, idol.1x3.two, base.lp4, base.17, hide.rest …</summary>
    public string Key = string.Empty;
    public RuleOrigin Origin;
    public string Name = string.Empty;
    public bool Hide;
    public bool Enabled = true;
    /// <summary>The steps it's on at; None for a foreign base's rules.</summary>
    public Steps Steps;
    public RuleLook Look = new();
    public RuleConditions Conditions = new();
    /// <summary>Its conditions are the ones edited in the game's editor (a choice), not the planner's.</summary>
    public bool GameConditions;
    /// <summary>For slot rules, the slot's settings.</summary>
    public SlotPlan? Slot;
    /// <summary>A foreign base's rule index, copied unchanged from the loaded base.</summary>
    public int BaseIndex = -1;
}

/// <summary>The level band of the hide rule and of a stage's rules in All stages.</summary>
internal readonly record struct LevelBand(int Min, int Max);

/// <summary>Something the plan couldn't make, and why.</summary>
internal readonly record struct LeftOut(string What, string Why);

/// <summary>One rule of a snapshot: its key, name, on or off, outcome, and short hashes of its look and conditions.</summary>
internal sealed record SnapRule(string Key, string Name, bool On, bool Hide, string Look, string Cond);

/// <summary>What a made filter was made from, kept with its record so the page can say what changed since.</summary>
internal sealed class PlanSnapshot
{
    public string Name = string.Empty, Description = string.Empty, Base = string.Empty, BaseHash = string.Empty;
    public int Strictness;
    public readonly List<SnapRule> Rules = new();

    private string? _fingerprint;

    /// <summary>Equal fingerprints make the same filter. Made once: a snapshot isn't changed after it's taken.</summary>
    public string Fingerprint => _fingerprint ??= MakeFingerprint();

    private string MakeFingerprint()
    {
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Name).Append('|').Append(Description).Append('|').Append(Base).Append('|').Append(BaseHash).Append('|').Append(Strictness);
            foreach (var r in Rules) sb.Append('\n').Append(r.Key).Append(';').Append(r.Name).Append(';').Append(r.On).Append(r.Hide).Append(';').Append(r.Look).Append(';').Append(r.Cond);
            return Planner.LootFilterGame.Hash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        }
    }
}

/// <summary>A filter as the mod would make it: planner rules on top, the base's rules, the hide rule last.</summary>
internal sealed class FilterPlan
{
    public string BuildId = string.Empty;
    /// <summary>The stage index as text, or "all".</summary>
    public string StageKey = string.Empty;
    public string StageLabel = string.Empty;
    /// <summary>EaBaseName, or a file name in the game's Filters folder.</summary>
    public string Base = Planner.EaBaseName;
    public int Strictness = 1;
    public string Name = string.Empty;
    public string Description = string.Empty;
    public LevelBand? Band;
    public bool HideRest = true;
    public bool HideOtherClasses;
    public bool HideClassesFirst;
    public bool HideAll;
    /// <summary>The highly-needed rule's shard count: fewer than this shows; 0 leaves the rule out.</summary>
    public int LowShards;
    /// <summary>The shatter rule's tier: affixes at it or higher; 0 turns the rule off.</summary>
    public int ShatterTier;
    /// <summary>The page set the shatter tier: the rule is then on at every strictness.</summary>
    public bool ShatterTierChosen;
    public int ClassId;
    public readonly List<PlanRule> Rules = new();
    public readonly List<LeftOut> LeftOut = new();
    public int Limit = 200;
    /// <summary>A note for the page (a missing base fell back to EA · Base, say).</summary>
    public string? Note;

    public int EnabledCount
    {
        get
        {
            int n = 0;
            foreach (var rule in Rules)
                if (rule.Enabled) n++;
            return n;
        }
    }
}

internal static partial class Planner
{
    /// <summary>The name of the mod's general base filter, as the game's panel lists it.</summary>
    public const string EaBaseName = "EA · Base";

    /// <summary>The base choice for none: the filter holds the planner's own rules and its hide rules only.</summary>
    public const string NoBase = "none";

    /// <summary>A base in words: "base EA · Base", "no base".</summary>
    public static string BaseWords(string baseName) => baseName == NoBase ? "no base" : $"base {baseName}";

    /// <summary>The strictness steps' names, Levelling first.</summary>
    public static readonly string[] StrictnessNames = { "Levelling", "Regular", "Strict", "Very strict" };

    /// <summary>The step bit of a strictness.</summary>
    public static Steps StepOf(int strictness) => (Steps)(1 << Math.Clamp(strictness, 0, 3));
}

/// <summary>The player's changes to one rule: on or off, outcome, look; null fields keep the generated value.</summary>
internal sealed class RuleChoice
{
    public bool? Enabled;
    public bool? Hide;
    public RuleLook? Look;
    /// <summary>The rule's conditions as edited in the game's editor, used in place of the planner's; null keeps the planner's.</summary>
    public RuleConditions? Conditions;
}

/// <summary>The player's changes to one planned slot's rules.</summary>
internal sealed class SlotChoice
{
    public List<int>? Affixes;
    public int? Min;
    public int? TierBar;
    public List<int>? Types;
    public List<int>? SubTypes;
}

/// <summary>The player's choices for one stage key (a stage index or "all"); only changes from the defaults.</summary>
internal sealed class StageChoices
{
    /// <summary>Null = EA · Base.</summary>
    public string? Base;
    public int? Strictness;
    /// <summary>Null = the generated name.</summary>
    public string? Name;
    /// <summary>Set when the band was changed: Band null then means every level.</summary>
    public bool BandSet;
    public LevelBand? Band;
    public bool? HideRest;
    public bool? HideOtherClasses;
    /// <summary>The other-classes rule at the very top; null = the setting.</summary>
    public bool? HideClassesFirst;
    /// <summary>The hide rule hides every item kind, not only equipment; null = the setting.</summary>
    public bool? HideAll;
    /// <summary>UTILITY: groups switched away from the setting.</summary>
    public readonly Dictionary<UtilityGroup, bool> Utility = new();
    /// <summary>The highly-needed shard count; null = the setting.</summary>
    public int? LowShards;
    /// <summary>The shatter rule's tier; null = the setting.</summary>
    public int? ShatterTier;
    public readonly Dictionary<string, RuleChoice> Rules = new();
    public readonly Dictionary<string, SlotChoice> Slots = new();
    public readonly List<ushort> DroppedUniques = new();
    /// <summary>The planned uniques' potential rules on or off; null = by strictness.</summary>
    public bool? Potential;

    public bool Empty => Base == null && Strictness == null && Name == null && !BandSet && HideRest == null && HideOtherClasses == null
        && HideClassesFirst == null && HideAll == null && Utility.Count == 0 && LowShards == null && ShatterTier == null
        && Rules.Count == 0 && Slots.Count == 0 && DroppedUniques.Count == 0 && Potential == null;
}

internal static partial class Planner
{
    // Rule keys of the planned uniques' potential steps, which the unique rows' potential switch turns on and off.
    private static readonly string[] s_potentialKeys = { "unique.lp3", "unique.lp2", "unique.lp1" };

    // Paper doll slots in the made filter's order, with each one's key and words.
    private static readonly (string Slot, string Key, string Label)[] s_filterSlots =
    {
        ("helmet", "helmet", "Helmet"), ("armor", "body", "Body armour"), ("weapon", "weapon", "Weapon"), ("offhand", "offhand", "Off hand"),
        ("amulet", "amulet", "Amulet"), ("leftRing", "ring1", "Ring 1"), ("rightRing", "ring2", "Ring 2"), ("belt", "belt", "Belt"),
        ("gloves", "gloves", "Gloves"), ("boots", "boots", "Boots"), ("relic", "relic", "Relic"),
    };

    // Idol types largest first, with each one's key and the game's name for the size.
    private static readonly (int Type, string Key, string Label)[] s_idolSizes =
    {
        (33, "2x2", "Adorned"), (32, "1x4", "Huge"), (31, "4x1", "Ornate"), (30, "1x3", "Large"), (29, "3x1", "Grand"),
        (28, "1x2", "Stout"), (27, "2x1", "Humble"), (26, "1x1l", "Small Lagonian"), (25, "1x1e", "Small Eterran"),
    };

    /// <summary>Equipment types 0–24 and the idols 25–33: what the hide rule hides (not altars, runes, glyphs, keys,
    /// woven echoes or materials).</summary>
    public static readonly List<int> HiddenTypes = Enumerable.Range(0, 34).ToList();

    /// <summary>The idol types, small and large.</summary>
    public static readonly List<int> IdolTypes = Enumerable.Range(25, 9).ToList();

    // The game's rarity flags.
    public const int RarityNormal = 1, RarityMagic = 2, RarityRare = 4, RarityUnique = 8, RaritySet = 16, RarityLegendary = 32, RarityExalted = 64;

    /// <summary>The steps written as letters, Levelling first ("LRSV").</summary>
    public static string StepLetters(Steps steps) =>
        (steps.HasFlag(Steps.Levelling) ? "L" : "") + (steps.HasFlag(Steps.Regular) ? "R" : "") + (steps.HasFlag(Steps.Strict) ? "S" : "") + (steps.HasFlag(Steps.VeryStrict) ? "V" : "");

    private const Steps LRSV = Steps.All, LRS = Steps.Levelling | Steps.Regular | Steps.Strict, LR = Steps.Levelling | Steps.Regular,
        SV = Steps.Strict | Steps.VeryStrict;

    /// <summary>The stage key's words: "Stage 2 · Endgame", the name alone for a one-stage build, "All stages".</summary>
    public static string FilterStageLabel(Build build, string stageKey)
    {
        if (stageKey == "all") return "All stages";
        int index = int.TryParse(stageKey, out int i) ? Math.Clamp(i, 0, build.Stages.Count - 1) : 0;
        string name = build.Stages[index].Name.Trim();
        if (build.Stages.Count == 1) return name.Length > 0 ? name : "Stage 1";
        return name.Length > 0 ? $"Stage {index + 1} · {name}" : $"Stage {index + 1}";
    }

    /// <summary>
    /// The filter the mod would make for a build's stage (an index as text, or "all") on the base the choices name, shaped
    /// by the choices. Plain data: no game object is made. Rules the limit can't hold and planned things the game lacks
    /// are listed in LeftOut.
    /// </summary>
    public static FilterPlan PlanFor(Build build, string stageKey, StageChoices? choices = null, int limit = 200)
    {
        choices ??= new StageChoices();
        bool all = stageKey == "all" && build.Stages.Count > 1;
        if (!all && !int.TryParse(stageKey, out _)) stageKey = "0";
        int index = all ? build.Stages.Count - 1 : Math.Clamp(int.Parse(stageKey), 0, build.Stages.Count - 1);
        if (!all) stageKey = index.ToString();
        var stage = build.Stages[index];
        var plan = new FilterPlan
        {
            BuildId = build.Id,
            StageKey = stageKey,
            StageLabel = FilterStageLabel(build, stageKey),
            Base = choices.Base ?? EaBaseName,
            Strictness = Math.Clamp(choices.Strictness ?? PlannerSettings.DefaultStrictness, 0, 3),
            HideRest = choices.HideRest ?? PlannerSettings.LootFilterHideRest.Value,
            HideOtherClasses = choices.HideOtherClasses ?? PlannerSettings.LootFilterHideOtherClasses.Value,
            HideClassesFirst = choices.HideClassesFirst ?? PlannerSettings.LootFilterHideClassesFirst.Value,
            HideAll = choices.HideAll ?? PlannerSettings.LootFilterHideAll.Value,
            LowShards = choices.LowShards ?? (int)PlannerSettings.LootFilterLowShards.Value,
            ShatterTier = Math.Clamp(choices.ShatterTier ?? (int)PlannerSettings.LootFilterShatterTier.Value, 0, 7),
            ShatterTierChosen = choices.ShatterTier > 0,
            ClassId = stage.ClassId,
            Limit = limit,
        };
        // From the previous stage's level upward; the first or only stage (or one after a stage with no level) at every level.
        plan.Band = choices.BandSet ? choices.Band
            : !all && index > 0 && build.Stages[index - 1].Level > 0 ? new LevelBand(build.Stages[index - 1].Level, 100) : null;
        // Hiding every item kind starts at level 10 unless a level was chosen: the first zones keep everything.
        if (plan.Band == null && plan.HideAll && !choices.BandSet) plan.Band = new LevelBand(10, 100);
        plan.Name = choices.Name is { Length: > 0 } name ? CleanFilterName(name) : DefaultFilterName(build, stageKey);
        // A one-stage build's stage is often named after the build; it isn't said twice.
        string stagePart = plan.StageLabel.Equals(build.Name, StringComparison.OrdinalIgnoreCase) ? string.Empty : $" · {plan.StageLabel}";
        plan.Description = $"Made by Epoch Assist from {build.Name}{stagePart} · {StrictnessNames[plan.Strictness]} · {BaseWords(plan.Base)}";

        var stages = all ? Enumerable.Range(0, build.Stages.Count).Reverse().ToList() : new List<int> { index };
        UniqueRules(build, stages, plan, choices);
        var legendaries = new Dictionary<ushort, List<PlanRule>>();
        // Every stage's slots: the UTILITY: rules come once per filter, over all of them.
        var utilitySlots = new List<SlotPlan>();
        foreach (int s in stages)
        {
            LevelBand? band = all ? StageBand(build, s) : null;
            // Things to collect ahead (idols, the altar, weaver items, legendary bases) count from the stage before.
            LevelBand? collect = all ? CollectBand(build, s) : null;
            string prefix = all ? $"s{s + 1}." : string.Empty;
            // "S2 Endgame · ", or "S2 · " for a stage with no name.
            string stageName = build.Stages[s].Name.Trim();
            string label = all ? $"S{s + 1}{(stageName.Length > 0 ? " " + stageName : string.Empty)} · " : string.Empty;
            var gear = GearOf(build, s);
            var slots = SlotRules(gear, plan, choices, band, prefix, label);
            var stageSlots = slots.Concat(LegendarySlots(gear)).ToList();
            CorruptedRule(stageSlots, plan, band, prefix, label);
            utilitySlots.AddRange(stageSlots);
            LegendaryRules(gear, plan, collect, prefix, label, legendaries);
            IdolRules(gear, plan, collect, prefix, label);
            AltarRules(gear, plan, collect, prefix, label);
            WeaverRules(gear, plan, collect, prefix, label);
        }

        UtilityRules(utilitySlots, plan);
        AscendRule(build, stages, plan);

        var baseRules = plan.Base == NoBase ? new List<PlanRule>() : plan.Base == EaBaseName ? LootFilterBase.RulesFor() : LootFilterGame.BaseRules(plan.Base);
        if (baseRules == null)
        {
            plan.Note = $"{plan.Base} isn't in the game's filter list · EA · Base is used instead";
            plan.Base = EaBaseName;
            baseRules = LootFilterBase.RulesFor();
        }
        int classesAt = plan.HideClassesFirst ? 0 : plan.Rules.Count;
        if (plan.Base == EaBaseName && !plan.HideClassesFirst)
            classesAt += Math.Max(0, baseRules.FindLastIndex(r => r.Key.StartsWith("base.lp", StringComparison.Ordinal)) + 1);
        plan.Rules.AddRange(baseRules);
        if (plan.HideOtherClasses) plan.Rules.Insert(classesAt, HideClassesRule(stage.ClassId));
        if (plan.HideRest)
        {
            if (plan.HideAll) plan.Rules.AddRange(ResourceRules());
            var hide = new PlanRule { Key = "hide.rest", Name = plan.HideAll ? "Everything else" : "Other equipment", Hide = true, Steps = LRSV };
            if (!plan.HideAll) hide.Conditions.Types = HiddenTypes.ToList();
            if (plan.Band is { } b) hide.Conditions.Levels = (b.Min, b.Max);
            plan.Rules.Add(hide);
        }

        ApplyChoices(plan, choices);
        FitLimit(plan);
        return plan;
    }

    // What the game shows without a filter, kept above a hide rule for every item kind; each can be turned off on its own.
    private static IEnumerable<PlanRule> ResourceRules()
    {
        foreach (var (kind, name) in new[] { (ResourceKind.Runes, "Runes"), (ResourceKind.Glyphs, "Glyphs"), (ResourceKind.Keys, "Keys and currency"),
                     (ResourceKind.WovenEchoes, "Woven echoes"), (ResourceKind.Shards, "Shards"), (ResourceKind.Resonances, "Resonances") })
        {
            var rule = new PlanRule { Key = "res." + kind.ToString().ToLowerInvariant(), Name = name, Steps = LRSV };
            rule.Conditions.Resource = kind;
            yield return rule;
        }
    }

    /// <summary>The generated name for a build's stage key: a one-stage build by the build alone.</summary>
    public static string DefaultFilterName(Build build, string stageKey)
    {
        if (build.Stages.Count == 1) return FilterName(build.Name, null);
        if (stageKey == "all") return FilterName(build.Name, "All stages");
        int index = int.TryParse(stageKey, out int i) ? Math.Clamp(i, 0, build.Stages.Count - 1) : 0;
        return FilterName(build.Name, build.Stages[index].Name.Trim() is { Length: > 0 } stageName ? stageName : $"Stage {index + 1}");
    }

    /// <summary>"EA · build · stage", cleaned and cut to the game's 50-character name field by shortening the build.</summary>
    public static string FilterName(string build, string? stage)
    {
        const int max = 50;
        build = CleanFilterName(build);
        if (stage == null) return CleanFilterName("EA · " + build);
        stage = CleanFilterName(stage);
        string full = $"EA · {build} · {stage}";
        if (full.Length <= max) return full;
        int room = max - $"EA ·  · {stage}".Length;
        if (room >= 4) return $"EA · {build[..(room - 1)].TrimEnd()}… · {stage}";
        return full[..max];
    }

    /// <summary>A name with the characters file names can't hold taken out, cut to 50.</summary>
    public static string CleanFilterName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 50 ? clean[..50].TrimEnd() : clean;
    }

    // The stage's band in All stages: from the previous stage's level to below its own; the last stage upward.
    private static LevelBand? StageBand(Build build, int s)
    {
        int from = s > 0 ? build.Stages[s - 1].Level : 0;
        int to = s + 1 < build.Stages.Count && build.Stages[s].Level > 0 ? build.Stages[s].Level - 1 : 100;
        if (from <= 0 && to >= 100) return null;
        return new LevelBand(Math.Max(0, from), Math.Max(from, to));
    }

    // A stage's band for things to collect ahead: from where the nearest earlier stage with a lower start begins (stages
    // planned at the same level share it), to its own end.
    private static LevelBand? CollectBand(Build build, int s)
    {
        var own = StageBand(build, s);
        int min = own?.Min ?? 0, max = own?.Max ?? 100;
        for (int k = s - 1; k >= 0; k--)
        {
            int earlier = StageBand(build, k)?.Min ?? 0;
            if (earlier >= min) continue;
            min = earlier;
            break;
        }
        return min <= 0 && max >= 100 ? null : new LevelBand(min, max);
    }

    private static void UniqueRules(Build build, List<int> stages, FilterPlan plan, StageChoices choices)
    {
        var ids = new List<ushort>();
        foreach (int s in stages)
        {
            var gear = GearOf(build, s);
            foreach (var item in gear.Slots.Values.Concat(gear.Idols).Concat(gear.WeaverItems.Values))
            {
                if (item.Unique == 0 || ids.Contains(item.Unique)) continue;
                if (item.Unknown.Contains("unique"))
                {
                    plan.LeftOut.Add(new LeftOut($"Unique {item.Unique}", "isn't in this game version"));
                    continue;
                }
                if (choices.DroppedUniques.Contains(item.Unique)) continue;
                ids.Add(item.Unique);
            }
        }
        if (ids.Count == 0) return;
        bool loud = PlannerSettings.LootFilterEmphasizeUniques.Value;
        PlanRule Rule(string key, string name, Steps steps, RuleLook look, Action<RuleConditions> fill)
        {
            var rule = new PlanRule { Key = key, Name = name, Steps = steps, Look = loud ? look : new RuleLook() };
            rule.Conditions.Uniques = ids.ToList();
            fill(rule.Conditions);
            return rule;
        }
        plan.Rules.Add(Rule("unique.lp3", "PLANNER: uniques · 3 LP or 20 WW", LRSV, Looks.UniqueTop(emphasized: true, beam: true), c => c.Potential = (3, 20)));
        plan.Rules.Add(Rule("unique.lp2", "PLANNER: uniques · 2 LP or 17 WW", LRSV, Looks.Unique(emphasized: true, beam: true), c => c.Potential = (2, 17)));
        plan.Rules.Add(Rule("unique.lp1", "PLANNER: uniques · 1 LP or 14 WW", LRS, new RuleLook { Emphasized = true }, c => c.Potential = (1, 14)));
        plan.Rules.Add(Rule("unique.corrupted", "PLANNER: uniques · corrupted", LR, new RuleLook { Emphasized = true }, c => c.Corruption = 1));
        plan.Rules.Add(Rule("unique.legendary", "PLANNER: legendaries", LRSV, Looks.UniqueTop(emphasized: true, beam: true, sound: false), c => c.Rarity = RarityLegendary));
        plan.Rules.Add(Rule("unique.any", "PLANNER: uniques and sets", LR, new RuleLook(), c => c.Rarity = RarityUnique | RaritySet | RarityLegendary));
    }

    // The stage's slot plans, for the rules over every slot's affixes.
    private static List<SlotPlan> SlotRules(PlannedGear gear, FilterPlan plan, StageChoices choices, LevelBand? band, string prefix, string label)
    {
        var known = AffixList.get();
        var slots = new List<SlotPlan>();
        // Bases already given their base rules (two rings of one base get them once).
        var basesDone = new HashSet<string>();
        foreach (var (slotName, key, words) in s_filterSlots)
        {
            if (!gear.Slots.TryGetValue(slotName, out var item) || item.Unique != 0 || item.Rarity > 4) continue;
            string what = label + words;
            if (item.Unmakeable || item.Unknown.Contains("base"))
            {
                plan.LeftOut.Add(new LeftOut(what, "base not in this game version"));
                continue;
            }
            var slot = new SlotPlan { Slot = slotName, ItemType = item.ItemType, SubType = item.SubType };
            // A sealed affix is the planner's crafting step, not something a drop has; it counts only when the player adds it.
            var sealedIds = new List<int>();
            foreach (var affix in item.Affixes)
            {
                if (known?.GetAffix(affix.Id) == null)
                {
                    plan.LeftOut.Add(new LeftOut(what, $"affix {affix.Id} isn't in this game version"));
                    continue;
                }
                if (slot.Planned.Contains(affix.Id)) continue;
                slot.Planned.Add(affix.Id);
                if (affix.Sealed != SealedAffixType.None) sealedIds.Add(affix.Id);
                slot.Tiers[affix.Id] = affix.Tier + 1;
            }
            if (slot.Planned.Count == 0) continue;
            choices.Slots.TryGetValue(prefix + key, out var change);
            slot.Counted = change?.Affixes?.Where(slot.Planned.Contains).ToList() is { Count: > 0 } counted ? counted
                : slot.Planned.Where(id => !sealedIds.Contains(id)).ToList() is { Count: > 0 } unsealed ? unsealed : slot.Planned.ToList();
            int lowest = slot.Counted.Min(id => slot.Tiers[id]);
            slot.TierBar = Math.Clamp(change?.TierBar ?? Math.Min((int)PlannerSettings.LootFilterTierCap.Value, lowest), 1, 7);
            slot.Min = Math.Clamp(change?.Min ?? Math.Min((int)PlannerSettings.LootFilterMinAffixes.Value, slot.Counted.Count), 1, slot.Counted.Count);
            slot.Types = change?.Types is { Count: > 0 } types ? types.ToList() : new List<int> { item.ItemType };
            slot.SubTypes = change?.SubTypes?.ToList() ?? new List<int>();
            string baseName = item.Name.Length > 0 ? item.Name : "planner base";
            string id = $"slot.{prefix}{key}";

            PlanRule Rule(string ruleKey, string name, Steps steps, RuleLook look)
            {
                var rule = new PlanRule { Key = ruleKey, Name = name, Steps = steps, Look = look, Slot = slot };
                if (band is { } b) rule.Conditions.Levels = (b.Min, b.Max);
                return rule;
            }
            var t7 = Rule($"{id}.t7", $"PLANNER: {what} · {baseName} · T7", SV, Looks.T7(emphasized: true, beam: true));
            t7.Conditions.Types = new List<int> { item.ItemType };
            t7.Conditions.SubTypes = new List<int> { item.SubType };
            t7.Conditions.Affixes.Add(new AffixNeed { Ids = slot.Counted.ToList(), TierBar = 7, Min = 1 });
            plan.Rules.Add(t7);

            var main = Rule($"{id}.main", $"PLANNER: {what} · {slot.Min} of {slot.Counted.Count} affixes", LRSV, Looks.Slot(emphasized: true));
            main.Conditions.Types = slot.Types.ToList();
            main.Conditions.SubTypes = slot.SubTypes.Count > 0 ? slot.SubTypes.ToList()
                : plan.Strictness == 3 ? new List<int> { item.SubType } : null;
            main.Conditions.Affixes.Add(new AffixNeed { Ids = slot.Counted.ToList(), TierBar = slot.TierBar, Min = slot.Min });
            plan.Rules.Add(main);

            // Strict and below: the planned base with two planned affixes of some tier.
            var pair = Rule($"{id}.pair", $"PLANNER: {what} · {baseName} · {Math.Min(2, slot.Counted.Count)} affixes, tiers 4+", LRS, Looks.Slot(emphasized: false));
            pair.Conditions.Types = new List<int> { item.ItemType };
            pair.Conditions.SubTypes = slot.SubTypes.Count > 0 ? slot.SubTypes.ToList() : new List<int> { item.SubType };
            pair.Conditions.Affixes.Add(new AffixNeed { Ids = slot.Counted.ToList(), Min = Math.Min(2, slot.Counted.Count), Sum = 4 });
            plan.Rules.Add(pair);

            var t6 = Rule($"{id}.t6", $"PLANNER: {what} · affix T6+", LR, Looks.Exalted());
            t6.Conditions.Types = slot.Types.ToList();
            t6.Conditions.SubTypes = slot.SubTypes.Count > 0 ? slot.SubTypes.ToList() : null;
            t6.Conditions.Affixes.Add(new AffixNeed { Ids = slot.Counted.ToList(), TierBar = 6, Min = 1 });
            t6.Conditions.Corruption = 2;
            // T6 pieces matter until T7s take over.
            t6.Conditions.Levels = band is { } t6Band ? (t6Band.Min, Math.Min(85, t6Band.Max)) : (0, 85);
            t6.Name += " (to 85)";
            if (t6.Conditions.Levels.Value.Min <= t6.Conditions.Levels.Value.Max) plan.Rules.Add(t6);

            var tiers = Rule($"tiers.{prefix}{key}", $"PLANNER: {what} · affixes 3+ tiers", LR, new RuleLook());
            tiers.Conditions.Types = slot.Types.ToList();
            tiers.Conditions.Affixes.Add(new AffixNeed { Ids = slot.Counted.ToList(), Sum = 3 });
            plan.Rules.Add(tiers);

            var level = Rule($"level.{prefix}{key}", $"PLANNER: {what} · any affix (to 40)", LR, new RuleLook());
            level.Conditions.Types = slot.Types.ToList();
            level.Conditions.Affixes.Add(new AffixNeed { Ids = slot.Counted.ToList() });
            level.Conditions.Levels = band is { } lb ? (lb.Min, Math.Min(40, lb.Max)) : (0, 40);
            if (level.Conditions.Levels.Value.Min <= level.Conditions.Levels.Value.Max) plan.Rules.Add(level);

            slots.Add(slot);
            var baseSubTypes = slot.SubTypes.Count > 0 ? slot.SubTypes.ToList() : new List<int> { item.SubType };
            if (!basesDone.Add($"{string.Join(",", slot.Types)}/{string.Join(",", baseSubTypes)}")) continue;

            // The planned base itself while levelling: worth wearing or crafting on before its affixes roll high.
            var bases = Rule($"bases.{prefix}{key}", $"PLANNER: {what} · {baseName} (to 60)", LR, Looks.Base());
            bases.Conditions.Types = slot.Types.ToList();
            bases.Conditions.SubTypes = baseSubTypes.ToList();
            bases.Conditions.Levels = band is { } bb ? (bb.Min, Math.Min(60, bb.Max)) : (0, 60);
            if (bases.Conditions.Levels.Value.Min <= bases.Conditions.Levels.Value.Max) plan.Rules.Add(bases);

        }
        return slots;
    }

    // Items needing Circle of Fortune rank of every item type the planner has a unique or set item for, to turn with a Rune
    // of Ascendance: the rune keeps the type, not the base or the rarity, so the rule names types only. Any item can take
    // the rune; on these, the Circle's upgraded rune is often kept.
    private static void AscendRule(Build build, List<int> stages, FilterPlan plan)
    {
        var types = stages.SelectMany(s => GearOf(build, s).Slots.Values).Where(i => i.Unique != 0 && !i.Unknown.Contains("unique"))
            .Select(i => (int)i.ItemType).Distinct().OrderBy(t => t).ToList();
        if (types.Count == 0) return;
        var rule = new PlanRule { Key = "ascend", Name = "UTILITY: unique and set item types · CoF items, to ascend", Steps = LRS, Look = Looks.Craft() };
        rule.Conditions.Types = types;
        rule.Conditions.Rarity = RarityNormal | RarityMagic | RarityRare | RarityExalted;
        rule.Conditions.Corruption = 2;
        rule.Conditions.Factions = new List<int> { 0 };
        // The rune takes forging potential: a piece with none left can't take it.
        rule.Conditions.Forging = 1;
        plan.Rules.Add(rule);
    }

    // The stage's corrupted pieces with two of its counted affixes, to wear while levelling.
    private static void CorruptedRule(List<SlotPlan> slots, FilterPlan plan, LevelBand? band, string prefix, string label)
    {
        var ids = slots.SelectMany(s => s.Counted).Distinct().OrderBy(i => i).ToList();
        if (ids.Count == 0) return;
        var corrupted = new PlanRule
        {
            Key = $"build.{prefix}corrupted", Name = $"PLANNER: {label}corrupted · {Math.Min(2, ids.Count)} build affixes (to 60)", Steps = LR, Look = new RuleLook(),
        };
        corrupted.Conditions.Levels = band is { } b ? (b.Min, Math.Min(60, b.Max)) : (0, 60);
        corrupted.Conditions.Types = slots.SelectMany(s => s.Types).Distinct().OrderBy(i => i).ToList();
        corrupted.Conditions.Affixes.Add(new AffixNeed { Ids = ids, Min = Math.Min(2, ids.Count) });
        corrupted.Conditions.Corruption = 1;
        if (corrupted.Conditions.Levels.Value.Min <= corrupted.Conditions.Levels.Value.Max) plan.Rules.Add(corrupted);
    }

    // UTILITY: rules over every stage's counted affixes, once per filter at every level: T7s to turn with a Rune of
    // Havoc and pieces to shatter for shards.
    private static void UtilityRules(List<SlotPlan> slots, FilterPlan plan)
    {
        var ids = slots.SelectMany(s => s.Counted).Distinct().OrderBy(i => i).ToList();
        if (ids.Count == 0) return;
        var types = slots.SelectMany(s => s.Types).Distinct().OrderBy(i => i).ToList();
        PlanRule Rule(string key, string name, Steps steps, RuleLook look) =>
            new() { Key = $"build.{key}", Name = $"UTILITY: {name}", Steps = steps, Look = look };

        var havoc = Rule("havoc", "T7 and a build affix · uncorrupted, for a Rune of Havoc", LRS, Looks.Craft());
        havoc.Conditions.Types = types.ToList();
        havoc.Conditions.Affixes.Add(new AffixNeed { Ids = LootFilterBase.Groups.Equipment, TierBar = 7, Min = 1 });
        havoc.Conditions.Affixes.Add(new AffixNeed { Ids = ids.ToList(), Min = 1 });
        havoc.Conditions.Corruption = 2;
        // A piece with no forging potential left can't take the rune.
        havoc.Conditions.Forging = 1;
        if (havoc.Conditions.Affixes[0].Ids.Count > 0) plan.Rules.Add(havoc);

        // Class affixes roll on few items, so their shards are worth a rune at any tier (T4+ at Very strict).
        var known = AffixList.get();
        var classIds = ids.Where(id => known?.GetAffix(id) is { } affix && affix.isSpecificToASingleClass()).ToList();
        foreach (var (key, name, steps, tier) in new[] { ("class", "class affix, any tier · to shatter for shards", LRS, 0),
                     ("class.t4", "class affix T4+ · to shatter for shards", Steps.VeryStrict, 4) })
        {
            if (classIds.Count == 0) break;
            var rule = Rule(key, name, steps, Looks.Shatter(loud: true));
            rule.Conditions.Affixes.Add(new AffixNeed { Ids = classIds.ToList(), TierBar = tier, Min = 1 });
            rule.Conditions.Rarity = RarityMagic | RarityRare | RarityExalted;
            plan.Rules.Add(rule);
        }

        // Build affixes the player holds few shards of, at any tier; class affixes have their own rule above.
        int below = plan.LowShards;
        if (below > 0)
        {
            var counted = ids.Where(id => !classIds.Contains(id)).Select(id => (Id: id, Count: ShardCounts.Of(id))).ToList();
            if (counted.Any(c => c.Count == null)) plan.LeftOut.Add(new LeftOut("Highly needed", "shard counts are read once a character is loaded"));
            var lowIds = counted.Where(c => c.Count < below).Select(c => c.Id).ToList();
            var low = Rule("low", $"highly needed · fewer than {below} shards · to shatter", LRSV, Looks.Shatter(loud: true));
            low.Conditions.Affixes.Add(new AffixNeed { Ids = lowIds, Min = 1 });
            low.Conditions.Rarity = RarityMagic | RarityRare | RarityExalted;
            if (lowIds.Count > 0) plan.Rules.Add(low);
        }

        // At tier 0 the rule stays in the filter, off, at T5.
        int shatterTier = plan.ShatterTier > 0 ? plan.ShatterTier : 5;
        var shatter = Rule("shatter", $"build affix T{shatterTier}+ · to shatter for shards", plan.ShatterTierChosen ? LRSV : LRS, Looks.Shatter());
        shatter.Conditions.Affixes.Add(new AffixNeed { Ids = ids.ToList(), TierBar = shatterTier, Min = 1 });
        shatter.Conditions.Rarity = RarityMagic | RarityRare | RarityExalted;
        plan.Rules.Add(shatter);
    }

    private static void IdolRules(PlannedGear gear, FilterPlan plan, LevelBand? band, string prefix, string label)
    {
        var known = AffixList.get();
        foreach (var (type, key, words) in s_idolSizes)
        {
            var affixes = new List<int>();
            var bases = new List<int>();
            foreach (var idol in gear.Idols)
            {
                if (idol.ItemType != type || idol.Unique != 0) continue;
                if (idol.Unmakeable || idol.Unknown.Contains("base"))
                {
                    plan.LeftOut.Add(new LeftOut($"{label}{words} idol", "base not in this game version"));
                    continue;
                }
                if (!bases.Contains(idol.SubType)) bases.Add(idol.SubType);
                foreach (var affix in idol.Affixes)
                {
                    if (known?.GetAffix(affix.Id) == null) plan.LeftOut.Add(new LeftOut($"{label}{words} idol", $"affix {affix.Id} isn't in this game version"));
                    else if (!affixes.Contains(affix.Id)) affixes.Add(affix.Id);
                }
            }
            bases.Sort();
            if (affixes.Count == 0) continue;
            PlanRule Rule(string suffix, string name, Steps steps, RuleLook look, int min)
            {
                var rule = new PlanRule { Key = $"idol.{prefix}{key}.{suffix}", Name = $"PLANNER: {label}{words} idol · {name}", Steps = steps, Look = look };
                rule.Conditions.Types = new List<int> { type };
                // Strict and Very strict: the planned idol bases only (an Omen idol's affixes roll on Omen bases).
                if (plan.Strictness >= 2) rule.Conditions.SubTypes = bases.ToList();
                rule.Conditions.Affixes.Add(new AffixNeed { Ids = affixes.ToList(), Min = min });
                if (band is { } b) rule.Conditions.Levels = (b.Min, b.Max);
                return rule;
            }
            if (affixes.Count >= 2) plan.Rules.Add(Rule("two", "2 affixes", LRSV, Looks.Idol(emphasized: true), 2));
            plan.Rules.Add(Rule("one", "1 affix", LRS, Looks.Idol(emphasized: false), 1));
        }
    }

    // Only affixes a base piece can roll: corrupted, sealed and the unique's own mods come with the legendary.
    private static List<int> LegendaryAffixes(PlannedItem item, AffixList? known) =>
        item.Affixes.Where(a => a.Sealed == SealedAffixType.None
                && known?.GetAffix(a.Id) is { } affix && affix.specialAffixType == AffixList.SpecialAffixType.Standard)
            .Select(a => (int)a.Id).Distinct().OrderBy(i => i).ToList();

    // The stage's planned legendaries as slots for the build-affix rules: their affixes are crafted on a base first.
    private static IEnumerable<SlotPlan> LegendarySlots(PlannedGear gear)
    {
        var known = AffixList.get();
        foreach (var (slotName, _, _) in s_filterSlots)
        {
            if (!gear.Slots.TryGetValue(slotName, out var item) || item.Unique == 0 || item.Rarity != 9 || item.Unmakeable) continue;
            var ids = LegendaryAffixes(item, known);
            if (ids.Count > 0)
                yield return new SlotPlan { Slot = slotName, ItemType = item.ItemType, SubType = item.SubType, Planned = ids, Counted = ids.ToList(), Types = new List<int> { item.ItemType } };
        }
    }

    // A planned legendary's affixes on any base of its slot: the piece to make it from in the Temporal Sanctum. A
    // legendary planned in several stages gets one pair of rules, the latest stage's, with every stage's affixes.
    private static void LegendaryRules(PlannedGear gear, FilterPlan plan, LevelBand? band, string prefix, string label, Dictionary<ushort, List<PlanRule>> made)
    {
        var known = AffixList.get();
        foreach (var (slotName, key, words) in s_filterSlots)
        {
            if (!gear.Slots.TryGetValue(slotName, out var item) || item.Unique == 0 || item.Rarity != 9 || item.Unmakeable) continue;
            var ids = LegendaryAffixes(item, known);
            if (ids.Count == 0) continue;
            if (made.TryGetValue(item.Unique, out var earlier))
            {
                foreach (var rule in earlier)
                {
                    var need = rule.Conditions.Affixes[0];
                    need.Ids = need.Ids.Union(ids).OrderBy(i => i).ToList();
                    // The widest band of the stages it's planned in.
                    if (rule.Conditions.Levels is { } l) rule.Conditions.Levels = band is { } b ? (Math.Min(l.Min, b.Min), Math.Max(l.Max, b.Max)) : null;
                }
                continue;
            }
            string what = label + words;
            string unique = item.Name.Length > 0 ? item.Name : "planner legendary";
            PlanRule Rule(string suffix, string name, Steps steps, RuleLook look, int tier)
            {
                var rule = new PlanRule { Key = $"legendary.{prefix}{key}.{suffix}", Name = $"PLANNER: {what} · for {unique}: {name}", Steps = steps, Look = look };
                rule.Conditions.Types = new List<int> { item.ItemType };
                rule.Conditions.Affixes.Add(new AffixNeed { Ids = ids.ToList(), TierBar = tier, Min = 1 });
                rule.Conditions.Corruption = 2;
                if (band is { } b) rule.Conditions.Levels = (b.Min, b.Max);
                return rule;
            }
            var pair = new List<PlanRule> { Rule("t7", "affix T7", SV, Looks.T7(emphasized: true, beam: true), 7), Rule("t6", "affix T6+", LR, Looks.Exalted(), 6) };
            plan.Rules.AddRange(pair);
            made[item.Unique] = pair;
        }
    }

    // The planned idol altar: its own shape (the grid it gives depends on it) with its planned affixes.
    private static void AltarRules(PlannedGear gear, FilterPlan plan, LevelBand? band, string prefix, string label)
    {
        if (!gear.Slots.TryGetValue("altar", out var altar) || altar.Unique != 0) return;
        if (altar.Unmakeable || altar.Unknown.Contains("base"))
        {
            plan.LeftOut.Add(new LeftOut($"{label}Idol altar", "base not in this game version"));
            return;
        }
        var known = AffixList.get();
        var affixes = new List<int>();
        foreach (var affix in altar.Affixes)
        {
            if (known?.GetAffix(affix.Id) == null) plan.LeftOut.Add(new LeftOut($"{label}Idol altar", $"affix {affix.Id} isn't in this game version"));
            else if (!affixes.Contains(affix.Id)) affixes.Add(affix.Id);
        }
        string baseName = altar.Name.Length > 0 ? altar.Name : "idol altar";
        PlanRule Rule(string suffix, string name, Steps steps, RuleLook look, int min)
        {
            var rule = new PlanRule { Key = $"altar.{prefix}{suffix}", Name = $"PLANNER: {label}{baseName} · {name}", Steps = steps, Look = look };
            rule.Conditions.Types = new List<int> { (int)EquipmentType.IDOL_ALTAR };
            rule.Conditions.SubTypes = new List<int> { altar.SubType };
            if (min > 0) rule.Conditions.Affixes.Add(new AffixNeed { Ids = affixes.ToList(), Min = min });
            if (band is { } b) rule.Conditions.Levels = (b.Min, b.Max);
            return rule;
        }
        // Very strict: a planned affix at T7 only.
        if (affixes.Count >= 1)
        {
            var t7 = Rule("t7", "affix T7", Steps.VeryStrict, Looks.T7(emphasized: true, beam: true), 1);
            t7.Conditions.Affixes[0].TierBar = 7;
            plan.Rules.Add(t7);
        }
        if (affixes.Count >= 2) plan.Rules.Add(Rule("two", "2 affixes", LRS, Looks.Idol(emphasized: true), 2));
        if (affixes.Count >= 1) plan.Rules.Add(Rule("one", "1 affix", LR, Looks.Idol(emphasized: false), 1));
        // The shape alone, while levelling: an altar of the planned shape is worth picking up to start with.
        plan.Rules.Add(Rule("any", "any", Steps.Levelling, new RuleLook(), 0));
    }

    private static void WeaverRules(PlannedGear gear, FilterPlan plan, LevelBand? band, string prefix, string label)
    {
        var types = new List<int>();
        var affixes = new List<int>();
        var known = AffixList.get();
        foreach (var item in gear.WeaverItems.Values)
        {
            if (item.Unique != 0 || item.Unmakeable) continue;
            foreach (var affix in item.Affixes)
                if (known?.GetAffix(affix.Id) != null && !affixes.Contains(affix.Id)) affixes.Add(affix.Id);
            if (!types.Contains(item.ItemType)) types.Add(item.ItemType);
        }
        if (types.Count == 0 || affixes.Count == 0) return;
        var rule = new PlanRule { Key = $"weaver{(prefix.Length > 0 ? "." + prefix.TrimEnd('.') : string.Empty)}", Name = $"PLANNER: {label}weaver items · affixes", Steps = LRSV, Look = Looks.Weaver() };
        rule.Conditions.Types = types;
        rule.Conditions.Affixes.Add(new AffixNeed { Ids = affixes, Min = 1 });
        if (band is { } b) rule.Conditions.Levels = (b.Min, b.Max);
        plan.Rules.Add(rule);
    }

    private static PlanRule HideClassesRule(int classId)
    {
        int own = classId is >= 0 and <= 4 ? 1 << classId : 0;
        var rule = new PlanRule { Key = "hide.classes", Name = "Items for other classes", Hide = true, Steps = LRSV };
        rule.Conditions.Classes = 31 & ~own;
        return rule;
    }

    // The player's per-rule changes, then each rule's on or off from its steps, the player's own on or off winning.
    private static void ApplyChoices(FilterPlan plan, StageChoices choices)
    {
        var step = StepOf(plan.Strictness);
        foreach (var rule in plan.Rules)
        {
            if (rule.Origin != RuleOrigin.Base) rule.Enabled = (rule.Steps & step) != 0;
            if (rule.Origin == RuleOrigin.EaBase && BaseGroupOff(rule.Key)) rule.Enabled = false;
            if (UtilityGroupOf(rule.Key) is { } group && !UtilityOn(group, choices)) rule.Enabled = false;
            if (choices.Potential == false && Array.IndexOf(s_potentialKeys, rule.Key) >= 0) rule.Enabled = false;
            if (!choices.Rules.TryGetValue(rule.Key, out var change)) continue;
            if (change.Enabled is bool enabled) rule.Enabled = enabled;
            if (change.Hide is bool hide) rule.Hide = hide;
            if (change.Look != null) rule.Look = change.Look.Copy();
            if (change.Conditions != null)
            {
                // Built from the kept conditions, even for a rule a base filter would give.
                rule.Conditions = change.Conditions.Copy();
                rule.GameConditions = true;
                rule.BaseIndex = -1;
            }
        }
    }

    /// <summary>A UTILITY: group on for the stage: its choice, else the setting.</summary>
    public static bool UtilityOn(UtilityGroup group, StageChoices choices) =>
        group == UtilityGroup.Shatter ? (choices.ShatterTier ?? (int)PlannerSettings.LootFilterShatterTier.Value) > 0
        : choices.Utility.TryGetValue(group, out bool on) ? on : PlannerSettings.UtilityDefault(group).Value;

    /// <summary>The UTILITY: group a rule key belongs to, or null for any other rule.</summary>
    public static UtilityGroup? UtilityGroupOf(string key)
    {
        if (key == "ascend") return UtilityGroup.Ascend;
        if (!key.StartsWith("build.", StringComparison.Ordinal)) return null;
        string last = key.EndsWith(".class.t4", StringComparison.Ordinal) ? "class" : key[(key.LastIndexOf('.') + 1)..];
        return last switch
        {
            "havoc" => UtilityGroup.Havoc,
            "class" => UtilityGroup.ClassShatter,
            "low" => UtilityGroup.LowShards,
            "shatter" => UtilityGroup.Shatter,
            _ => null,
        };
    }

    // An EA · Base rule whose group is switched off in the settings.
    private static bool BaseGroupOff(string key)
    {
        foreach (var (toggle, ids) in PlannerSettings.LootFilterBaseGroups)
            if (!toggle.Value && Array.IndexOf(ids, key.StartsWith("base.", StringComparison.Ordinal) ? key[5..] : key) >= 0) return true;
        return false;
    }

    // Over the limit: disabled rules go first, from the bottom up; then planner show rules from the bottom up. The hide
    // rule always stays.
    private static void FitLimit(FilterPlan plan)
    {
        for (int i = plan.Rules.Count - 1; i >= 0 && plan.Rules.Count > plan.Limit; i--)
        {
            if (plan.Rules[i].Enabled || plan.Rules[i].Key == "hide.rest") continue;
            plan.LeftOut.Add(new LeftOut(plan.Rules[i].Name, "over the limit"));
            plan.Rules.RemoveAt(i);
        }
        for (int i = plan.Rules.Count - 1; i >= 0 && plan.Rules.Count > plan.Limit; i--)
        {
            var rule = plan.Rules[i];
            if (rule.Origin != RuleOrigin.Planner || rule.Hide) continue;
            plan.LeftOut.Add(new LeftOut(rule.Name, "over the limit"));
            plan.Rules.RemoveAt(i);
        }
    }

    /// <summary>
    /// What a plan makes, as plain data (no game object): the filter's name, base, the base file's fingerprint and
    /// strictness, and per rule its key, name, on or off, outcome and short hashes of its look and conditions.
    /// </summary>
    public static PlanSnapshot SnapshotOf(FilterPlan plan)
    {
        string? baseFile = plan.Base == NoBase ? null : plan.Base == EaBaseName
            ? LootFilterStore.RecordFor(string.Empty, "base") is { } record ? record.File : null
            : plan.Base + ".xml";
        var snapshot = new PlanSnapshot
        {
            Name = plan.Name,
            Description = plan.Description,
            Base = plan.Base,
            BaseHash = baseFile != null && LootFilterGame.FilterFolder is { } folder ? LootFilterGame.HashFileCached(Path.Combine(folder, baseFile)) ?? string.Empty : string.Empty,
            Strictness = plan.Strictness,
        };
        foreach (var rule in plan.Rules)
        {
            var c = rule.Conditions;
            var l = rule.Look;
            string look = LookHash(l);
            var cond = new System.Text.StringBuilder();
            cond.Append(rule.BaseIndex).Append(';').Append(c.Types == null ? "" : string.Join(",", c.Types)).Append('/').Append(c.SubTypes == null ? "" : string.Join(",", c.SubTypes))
                .Append(';').Append(c.Uniques == null ? "" : string.Join(",", c.Uniques)).Append(';').Append(c.Potential).Append(';').Append(c.Rarity)
                .Append(';').Append(c.Corruption).Append(';').Append(c.Levels).Append(';').Append(c.Classes);
            if (c.Factions != null) cond.Append(";F").Append(string.Join(",", c.Factions));
            if (c.Forging != null) cond.Append(";P").Append(c.Forging);
            if (c.Resource != null) cond.Append(";R").Append(c.Resource);
            foreach (var a in c.Affixes) cond.Append(";A").Append(string.Join(",", a.Ids)).Append('>').Append(a.TierBar).Append('x').Append(a.Min).Append('s').Append(a.Sum).Append('r').Append(a.Raw);
            snapshot.Rules.Add(new SnapRule(rule.Key, rule.Name, rule.Enabled, rule.Hide, look, ShortHash(cond.ToString())));
        }
        return snapshot;
    }

    /// <summary>A rule look's short hash, as snapshots keep it.</summary>
    public static string LookHash(RuleLook l) => ShortHash($"{l.Colour},{l.Emphasized},{l.Sound},{l.MapIcon},{l.Beam}");

    private static string ShortHash(string text) => LootFilterGame.Hash(System.Text.Encoding.UTF8.GetBytes(text))[..12];

    /// <summary>A plan's fingerprint: equal fingerprints make the same filter.</summary>
    public static string PlanFingerprint(FilterPlan plan) => SnapshotOf(plan).Fingerprint;

    /// <summary>
    /// What changed from a saved snapshot to the plan as the page shows it, in the page's words: name, base, strictness,
    /// rules added, removed, turned on or off, and rules whose outcome, look or conditions changed.
    /// </summary>
    public static List<string> PendingChanges(PlanSnapshot saved, PlanSnapshot now, PlanSnapshot? inGame = null)
    {
        // A change the game's copy in use has already (made in its editor) says so.
        var game = new Dictionary<string, SnapRule>();
        if (inGame != null)
            foreach (var rule in inGame.Rules) game.TryAdd(rule.Key, rule);
        string Already(SnapRule rule) => game.TryGetValue(rule.Key, out var g) && g.On == rule.On && g.Hide == rule.Hide && g.Look == rule.Look
            ? " · already in the game" : string.Empty;
        var changes = new List<string>();
        if (saved.Name != now.Name) changes.Add($"Name: {saved.Name} → {now.Name}");
        if (saved.Base != now.Base) changes.Add($"Base: {BaseWords(saved.Base)} → {BaseWords(now.Base)}");
        else if (saved.BaseHash != now.BaseHash && now.BaseHash.Length == 0 && saved.BaseHash.Length > 0)
            changes.Add(now.Base == EaBaseName ? "EA · Base isn't in the game's filter list · Update will recreate it"
                : $"{now.Base} isn't in the game's filter list");
        else if (saved.BaseHash != now.BaseHash) changes.Add($"{now.Base} was edited");
        if (saved.Strictness != now.Strictness) changes.Add($"Strictness: {StrictnessNames[saved.Strictness]} → {StrictnessNames[now.Strictness]}");
        var before = new Dictionary<string, SnapRule>();
        foreach (var rule in saved.Rules) before.TryAdd(rule.Key, rule);
        var seen = new HashSet<string>();
        // Rule numbers as the game's editor shows them: the page's for rules it has, the saved filter's for removed ones.
        for (int i = 0; i < now.Rules.Count; i++)
        {
            var rule = now.Rules[i];
            string number = $"#{i + 1} ";
            seen.Add(rule.Key);
            if (!before.TryGetValue(rule.Key, out var old))
            {
                changes.Add($"+ {number}{rule.Name}{(rule.On ? string.Empty : " (off)")}");
                continue;
            }
            var what = new List<string>();
            if (old.On != rule.On) what.Add(rule.On ? "turned on" : "turned off");
            if (old.Hide != rule.Hide) what.Add(rule.Hide ? "now hides" : "now shows");
            if (old.Look != rule.Look) what.Add("look changed");
            if (old.Cond != rule.Cond && old.Name == rule.Name) what.Add("conditions changed");
            if (old.Name != rule.Name) changes.Add($"{number}{old.Name} → {rule.Name}{(what.Count > 0 ? ": " + string.Join(", ", what) : string.Empty)}");
            // "Already" can only be told for on or off, outcome and look; the game's conditions are fingerprinted differently.
            else if (what.Count > 0) changes.Add($"{number}{rule.Name}: {string.Join(", ", what)}{(what.Contains("conditions changed") ? string.Empty : Already(rule))}");
        }
        for (int i = 0; i < saved.Rules.Count; i++)
            if (!seen.Contains(saved.Rules[i].Key)) changes.Add($"− #{i + 1} {saved.Rules[i].Name}");
        return changes;
    }

    /// <summary>The look roles of the made filter, as game ids (contracts: look roles).</summary>
    internal static class Looks
    {
        public static RuleLook UniqueTop(bool emphasized, bool beam, bool sound = true) =>
            new() { Colour = 0, Emphasized = emphasized, Sound = sound ? 2 : 0, MapIcon = 8, Beam = beam ? (1, 0) : null };
        public static RuleLook Unique(bool emphasized, bool beam) => new() { Colour = 6, Emphasized = emphasized, Sound = 13, MapIcon = 3, Beam = beam ? (3, 8) : null };
        public static RuleLook T7(bool emphasized, bool beam, bool sound = true) =>
            new() { Colour = 7, Emphasized = emphasized, Sound = sound ? 10 : 0, MapIcon = sound ? 7 : 0, Beam = beam ? (3, 10) : null };
        public static RuleLook Slot(bool emphasized) => new() { Colour = 13, Emphasized = emphasized, Sound = 8, Beam = (4, 16) };
        public static RuleLook Exalted(bool loud = false, bool emphasized = false, bool largest = false) =>
            new() { Colour = 3, Emphasized = emphasized, Sound = loud ? 9 : 0, MapIcon = loud ? 8 : 0, Beam = largest ? (1, 4) : loud ? (3, 4) : null };
        public static RuleLook Idol(bool emphasized) => new() { Colour = 15, Emphasized = emphasized, Beam = (4, 19) };
        public static RuleLook Weaver(bool emphasized = false) => new() { Colour = 2, Emphasized = emphasized, Beam = (4, 3) };
        public static RuleLook Set() => new() { Colour = 17 };
        public static RuleLook Base() => new() { Colour = 13 };
        public static RuleLook Craft() => new() { Colour = 16, Emphasized = true };
        public static RuleLook Shatter(bool loud = false) =>
            new() { Colour = 4, Emphasized = loud, Sound = loud ? 4 : 0, MapIcon = loud ? 9 : 0, Beam = loud ? (3, 6) : null };
    }
}

/// <summary>
/// The player's shard count per affix, read from the game at most every two seconds. <see cref="Generation"/> moves
/// when a count asked about changes, so a page showing a plan makes it again; the plan's fingerprint changes only when
/// an affix crosses the highly-needed count.
/// </summary>
internal static class ShardCounts
{
    private static readonly Dictionary<int, int> s_counts = new();
    private static float s_next;
    private static int s_generation;

    public static int Generation
    {
        get
        {
            Refresh();
            return s_generation;
        }
    }

    /// <summary>Shards held of the affix, the crafting slot's included; null while no character is loaded.</summary>
    public static int? Of(int affixId)
    {
        if (!s_counts.ContainsKey(affixId) && Read(affixId) is int n) s_counts[affixId] = n;
        return s_counts.TryGetValue(affixId, out int count) ? count : null;
    }

    private static void Refresh()
    {
        if (Clock.Now < s_next) return;
        s_next = Clock.Now + 2f;
        bool moved = false;
        foreach (int id in s_counts.Keys.ToList())
        {
            int? now = Read(id);
            if (now is not int n)
            {
                s_counts.Clear();
                moved = true;
                break;
            }
            if (n != s_counts[id]) moved = true;
            s_counts[id] = n;
        }
        if (moved) s_generation++;
    }

    private static int? Read(int affixId)
    {
        try
        {
            var tracker = PlayerFinder.getGlobalDataTracker();
            // Its dataLoaded flag stays false in play; a live tracker answers.
            return Game.Alive(tracker) ? tracker.GetCountOfShard(affixId, true) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
