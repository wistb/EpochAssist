using System.Globalization;
using System.Text.Json.Nodes;
using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;

namespace EpochAssist.Features;

/// <summary>A stage's planned gear: equipment by paper doll slot, idols by grid cell, blessings by timeline and the weaver
/// tree. Read from the build's stored planner data on first use; runtime only, never stored.</summary>
internal sealed class PlannedGear
{
    /// <summary>Paper doll slot name → planned item.</summary>
    public readonly Dictionary<string, PlannedItem> Slots = new();
    public readonly List<PlannedItem> Idols = new();
    /// <summary>Timeline (from 0) → planned blessing.</summary>
    public readonly SortedDictionary<int, PlannedItem> Blessings = new();
    /// <summary>Null when the stage plans no weaver points.</summary>
    public PlannedTree? Weaver;

    /// <summary>The stage plans no gear of any kind.</summary>
    public bool Empty => Slots.Count == 0 && Idols.Count == 0 && Blessings.Count == 0 && Weaver == null;
}

internal static partial class Planner
{
    /// <summary>The game's paper doll slots and the idol altar, with each site's names for them: Maxroll, then Last Epoch
    /// Tools.</summary>
    public static readonly (string Slot, string Maxroll, string Let)[] GearSlots =
    {
        ("helmet", "head", "head"), ("armor", "body", "chest"), ("weapon", "weapon", "weapon1"), ("offhand", "offhand", "weapon2"),
        ("amulet", "neck", "amulet"), ("leftRing", "finger1", "ring1"), ("rightRing", "finger2", "ring2"), ("belt", "waist", "waist"),
        ("gloves", "hands", "hands"), ("boots", "feet", "feet"), ("relic", "relic", "relic"), ("altar", "altar", "idol_altar"),
    };

    /// <summary>The game's container for a paper doll slot or the idol altar (any case), or null.</summary>
    public static IItemContainer? SlotContainer(string slot)
    {
        var manager = ItemContainersManager.Instance;
        if (slot.Equals("altar", StringComparison.OrdinalIgnoreCase)) return manager?.idolsInventory?.TryCast<IItemContainer>();
        var e = manager?.equipment;
        if (e == null) return null;
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase? box = slot.ToLowerInvariant() switch
        {
            "helmet" => e.helmet, "armor" => e.armor, "weapon" => e.weapon, "offhand" => e.offhand, "amulet" => e.amulet,
            "leftring" => e.leftRing, "rightring" => e.rightRing, "belt" => e.belt, "gloves" => e.gloves, "boots" => e.boots,
            "relic" => e.relic, _ => null,
        };
        return box?.TryCast<IItemContainer>();
    }

    /// <summary>The idol grid's width; Maxroll lists its cells in rows from the top left.</summary>
    public const int IdolGridWidth = 5;

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    // The planned gear of the stages read so far, for the library generation they were read in.
    private static readonly Dictionary<(string Build, int Stage), PlannedGear> s_gear = new();
    private static int s_gearGeneration = -1;

    /// <summary>
    /// The stage's planned gear, read from the build's planner data on first use and kept until the library changes.
    /// Each item is checked against this game version's data as it's read.
    /// </summary>
    public static PlannedGear GearOf(Build build, int stage)
    {
        if (s_gearGeneration != Library.Generation)
        {
            s_gearGeneration = Library.Generation;
            s_gear.Clear();
            ForgetMade();
        }
        if (s_gear.TryGetValue((build.Id, stage), out var gear)) return gear;
        try
        {
            gear = ReadGear(build, stage);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:gear", $"Planner: the build's gear couldn't be read: {e.Message}", 30);
            gear = new PlannedGear();
        }
        s_gear[(build.Id, stage)] = gear;
        return gear;
    }

    /// <summary>Drops every stage's planned gear and made items (the feature stopping).</summary>
    public static void ForgetGear()
    {
        s_gear.Clear();
        s_gearGeneration = -1;
        ForgetMade();
    }

    private static PlannedGear ReadGear(Build build, int stage)
    {
        var gear = new PlannedGear();
        if (MaxrollFormat.Parse(build.Raw) is not JsonObject raw) return gear;
        if (build.Source == LetFormat.Source) ReadLetGear(raw, gear);
        else ReadMaxrollGear(raw, stage, gear);
        foreach (var item in gear.Slots.Values) Check(item);
        foreach (var item in gear.Idols) Check(item);
        foreach (var item in gear.Blessings.Values) Check(item);
        return gear;
    }

    // ---- Maxroll ----

    // A link holds profiles that point into one item list; an export is one profile with its items inline.
    private static void ReadMaxrollGear(JsonObject raw, int stage, PlannedGear gear)
    {
        JsonObject? profile = raw, items = null;
        if (Build.Str(raw["data"]) is string dataText && MaxrollFormat.Parse(dataText) is JsonObject data)
        {
            // Stages are the profiles that are objects, in order.
            profile = (data["profiles"] as JsonArray)?.OfType<JsonObject>().ElementAtOrDefault(Math.Max(0, stage));
            items = data["items"] as JsonObject;
        }
        if (profile == null) return;
        JsonObject? Item(JsonNode? node) => node is JsonObject inline ? inline
            : items != null && Build.Int(node) is int index ? items[index.ToString(s_inv)] as JsonObject : null;

        if (profile["items"] is JsonObject slotted)
            foreach (var (slot, maxroll, _) in GearSlots)
                if (Item(slotted[maxroll]) is { } o) gear.Slots[slot] = MaxrollItem(o, PlannedPlace.Slot, slot);

        if (profile["idols"] is JsonArray idols)
            for (int i = 0; i < idols.Count; i++)
                if (Item(idols[i]) is { } o)
                {
                    var idol = MaxrollItem(o, PlannedPlace.Idol, string.Empty);
                    idol.X = i % IdolGridWidth;
                    idol.Y = i / IdolGridWidth;
                    gear.Idols.Add(idol);
                }

        if (profile["blessings"] is JsonArray blessings)
            for (int i = 0; i < blessings.Count; i++)
                if (Item(blessings[i]) is { } o)
                {
                    var blessing = MaxrollItem(o, PlannedPlace.Blessing, string.Empty);
                    blessing.Timeline = i;
                    gear.Blessings[i] = blessing;
                }

        var weaver = MaxrollFormat.ReadTree(string.Empty, profile["weaver"] as JsonObject);
        if (weaver.Total > 0) gear.Weaver = weaver;
    }

    // Maxroll tiers count from 1 and its rolls run 0 to 1; the game's tiers count from 0 and its rolls are bytes. It
    // states no rarity: a unique with affixes is legendary, else by affix count.
    private static PlannedItem MaxrollItem(JsonObject o, PlannedPlace place, string slot)
    {
        var item = new PlannedItem
        {
            Place = place,
            Slot = slot,
            ItemType = (byte)Math.Clamp(Build.Int(o["itemType"]) ?? 0, 0, byte.MaxValue),
            SubType = (ushort)Math.Clamp(Build.Int(o["subType"]) ?? 0, 0, ushort.MaxValue),
            Unique = (ushort)Math.Clamp(Build.Int(o["uniqueID"]) ?? 0, 0, ushort.MaxValue),
            Corrupted = o["corrupted"] is JsonValue c && c.TryGetValue(out bool corrupted) && corrupted,
        };
        void Add(JsonNode? node, SealedAffixType sealedType)
        {
            if (node is not JsonObject affix || Build.Int(affix["id"]) is not int id || id is < 0 or > ushort.MaxValue) return;
            item.Affixes.Add(new PlannedAffix((ushort)id, Tier(affix["tier"]), MaxrollRoll(affix["roll"], item), sealedType));
        }
        foreach (var a in o["affixes"] as JsonArray ?? new JsonArray()) Add(a, SealedAffixType.None);
        foreach (var a in o["corruptedAffixes"] as JsonArray ?? new JsonArray()) Add(a, SealedAffixType.FromCorruption);
        Add(o["sealedAffix"], SealedAffixType.Regular);
        Add(o["primordialAffix"], SealedAffixType.Primordial);
        item.Rarity = RarityOf(item.Unique, (o["affixes"] as JsonArray)?.Count ?? 0);
        item.Implicits = MaxrollRolls(o["implicits"], item);
        item.UniqueRolls = MaxrollRolls(o["uniqueRolls"], item);
        // Idols and blessings may have no implicits; equipment always has some.
        if (place == PlannedPlace.Slot && o["implicits"] is not JsonArray || item.Unique != 0 && o["uniqueRolls"] is not JsonArray) item.RollsFilled = true;
        return item;
    }

    private static byte Tier(JsonNode? n) => (byte)Math.Clamp((Build.Int(n) ?? 1) - 1, 0, byte.MaxValue);

    private static byte MaxrollRoll(JsonNode? n, PlannedItem item)
    {
        if (n is JsonValue v && v.TryGetValue(out double d)) return (byte)Math.Clamp(Math.Round(d * 255d), 0, 255);
        item.RollsFilled = true;
        return BestRoll;
    }

    private static byte[] MaxrollRolls(JsonNode? n, PlannedItem item) =>
        (n as JsonArray)?.Select(r => MaxrollRoll(r, item)).ToArray() ?? Array.Empty<byte>();

    // ---- Last Epoch Tools ----

    // One stage: equipment by its slot names, idols at their cells, blessings by timeline number from 1, and the weaver
    // tree's points with its order when the planner gives one.
    private static void ReadLetGear(JsonObject raw, PlannedGear gear)
    {
        if (raw["data"] is not JsonObject data) return;
        if (data["equipment"] is JsonObject equipment)
            foreach (var (slot, _, let) in GearSlots)
                if (equipment[let] is JsonObject o && LetItem(o, PlannedPlace.Slot, slot) is { } item) gear.Slots[slot] = item;

        if (data["idols"] is JsonArray idols)
            foreach (var node in idols)
                if (node is JsonObject o && LetItem(o, PlannedPlace.Idol, string.Empty) is { } idol)
                {
                    idol.X = Build.Int(o["x"]) ?? 0;
                    idol.Y = Build.Int(o["y"]) ?? 0;
                    gear.Idols.Add(idol);
                }

        if (data["blessings"] is JsonObject blessings)
            foreach (var (key, node) in blessings)
                if (int.TryParse(key, NumberStyles.Integer, s_inv, out int number) && number >= 1 && node is JsonObject o
                    && LetItem(o, PlannedPlace.Blessing, string.Empty) is { } blessing)
                {
                    blessing.Timeline = number - 1;
                    gear.Blessings[blessing.Timeline] = blessing;
                }

        var weaver = LetFormat.ReadTree(string.Empty, (data["weaverTree"] as JsonObject)?["selected"] as JsonObject, data["weaverTreeProgression"] as JsonArray);
        if (weaver.Total > 0) gear.Weaver = weaver;
    }

    // "I" ids hold 1 + base 3 + subtype 3 + rarity 1 + unique; "U" ids subtype 3 + unique 3 with the base from the game's
    // unique list. Affix ids are "A" + digits; tiers count from 1, rolls are bytes already.
    private static PlannedItem? LetItem(JsonObject o, PlannedPlace place, string slot)
    {
        var (tag, digits) = LzString.DecodeLetId(Build.Str(o["id"]) ?? string.Empty);
        var item = new PlannedItem { Place = place, Slot = slot, Corrupted = o.ContainsKey("corruptedAffix") };
        int regular = (o["affixes"] as JsonArray)?.Count ?? 0;
        if (tag == 'U' && digits is { Length: 6 })
        {
            item.Unique = ushort.Parse(digits.Substring(3), s_inv);
            item.SubType = ushort.Parse(digits.Substring(0, 3), s_inv);
            item.ItemType = UniqueList.getUniqueItemType(item.Unique);
            item.Rarity = RarityOf(item.Unique, regular);
        }
        else if (tag == 'I' && digits is { Length: >= 8 })
        {
            item.ItemType = byte.Parse(digits.Substring(1, 3), s_inv);
            item.SubType = ushort.Parse(digits.Substring(4, 3), s_inv);
            item.Unique = digits.Length > 8 ? ushort.Parse(digits.Substring(8), s_inv) : (ushort)0;
            // The id's rarity digit reads 0 on magic idols; the game stores the affix count.
            item.Rarity = RarityOf(item.Unique, regular);
        }
        else
        {
            return null;
        }
        void Add(JsonNode? node, SealedAffixType sealedType)
        {
            if (node is not JsonObject affix || LzString.DecodeLetId(Build.Str(affix["id"]) ?? string.Empty) is not ('A', { } id)
                || !ushort.TryParse(id, NumberStyles.Integer, s_inv, out ushort affixId)) return;
            byte roll = BestRoll;
            if (Build.Int(affix["r"]) is int r) roll = (byte)Math.Clamp(r, 0, 255);
            else item.RollsFilled = true;
            item.Affixes.Add(new PlannedAffix(affixId, Tier(affix["tier"]), roll, sealedType));
        }
        foreach (var a in o["affixes"] as JsonArray ?? new JsonArray()) Add(a, SealedAffixType.None);
        Add(o["corruptedAffix"], SealedAffixType.FromCorruption);
        Add(o["sealedAffix"], SealedAffixType.Regular);
        Add(o["primordialAffix"], SealedAffixType.Primordial);
        static byte[] Bytes(JsonNode? n) => (n as JsonArray)?.Select(x => (byte)Math.Clamp(Build.Int(x) ?? 0, 0, 255)).ToArray() ?? Array.Empty<byte>();
        item.Implicits = Bytes(o["ir"]);
        item.UniqueRolls = Bytes(o["ur"]);
        if (place == PlannedPlace.Slot && o["ir"] is not JsonArray) item.RollsFilled = true;
        return item;
    }
}
