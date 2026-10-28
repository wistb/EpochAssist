using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;

namespace EpochAssist.Features;

/// <summary>One item the character has: base, subtype, unique and affixes (id, tier from 0). Runtime only.</summary>
internal sealed class WornItem
{
    public byte ItemType;
    public ushort SubType;
    public ushort Unique;
    public readonly List<(ushort Id, byte Tier)> Affixes = new();
    public bool TwoHanded;
}

/// <summary>What the character has, as read from the game: equipment by slot, idols by top-left cell, blessings by
/// timeline, weaver items by node. Rebuilt in place when its signature changes.</summary>
internal sealed class EquippedSide
{
    public readonly Dictionary<string, WornItem> Slots = new();
    public readonly Dictionary<(int X, int Y), WornItem> Idols = new();
    public readonly Dictionary<int, WornItem> Blessings = new();
    /// <summary>Idol grid cells the character hasn't unlocked.</summary>
    public readonly HashSet<(int X, int Y)> LockedCells = new();
    /// <summary>Weaver tree points by node, entries at 0 left out.</summary>
    public readonly Dictionary<byte, byte> Weaver = new();
    /// <summary>Weaver socket (the game's container id) → the item in it.</summary>
    public readonly Dictionary<int, WornItem> WeaverItems = new();
    public ulong Signature;
}

/// <summary>A planned item against what the character has there.</summary>
internal sealed class ItemGuide
{
    public GuidanceState State;
    /// <summary>This game version lacks the planned item's base or unique, or (an idol) it covers cells the character
    /// hasn't unlocked: shown grey, never as a tooltip.</summary>
    public bool Unknown;
    /// <summary>An idol planned over cells the character hasn't unlocked.</summary>
    public bool Locked;
    /// <summary>Planned affixes absent or below their tier, as (id, planned tier from 0).</summary>
    public readonly List<(ushort Id, byte Tier)> Missing = new();
    public PlannedItem Planned = null!;
}

/// <summary>A stage's gear compared with the character: per slot, idol cell and blessing timeline, with a checklist each.
/// Checklist ids: the slot's index in <see cref="Planner.GearSlots"/>, an idol's cell as y × 5 + x, a blessing's
/// timeline.</summary>
internal sealed class GearGuidance
{
    public readonly Dictionary<string, ItemGuide> Slots = new();
    public readonly Dictionary<(int X, int Y), ItemGuide> Idols = new();
    public readonly SortedDictionary<int, ItemGuide> Blessings = new();
    /// <summary>Null when the stage plans no weaver points.</summary>
    public TreeGuide? Weaver { get; set; }
    public readonly ChecklistSection Gear = new();
    public readonly ChecklistSection IdolList = new();
    public readonly ChecklistSection BlessingList = new();
    /// <summary>Weaver socket (the game's container id) → the planned item against the socketed one.</summary>
    public readonly SortedDictionary<int, ItemGuide> WeaverItems = new();
    public readonly ChecklistSection WeaverItemList = new();
}

internal static partial class Planner
{
    /// <summary>The game's id for the weaver tree.</summary>
    public const string WeaverTreeId = "weaver";
    private static readonly HashSet<(string, byte)> s_noUnknown = new();
    private static readonly Dictionary<(string, byte), byte> s_noCaps = new();

    // The base types the game counts as two-handed weapons (its EquipmentType 12-16, bow, crossbow), read from the type
    // alone so neither side needs an item made.
    private static bool TwoHandedType(byte type) => type is >= 12 and <= 16 or 23 or 24;

    // The weaver tree's sockets, container id → the node it sits on, from the game's own lists; read once, empty when they
    // can't be.
    private static Dictionary<int, byte>? s_sockets;

    /// <summary>The weaver tree's item sockets as the game lists them: each one's container id (the key Last Epoch Tools
    /// uses) → the weaver node it sits on.</summary>
    public static IReadOnlyDictionary<int, byte> WeaverSockets
    {
        get
        {
            if (s_sockets != null) return s_sockets;
            s_sockets = new Dictionary<int, byte>();
            try
            {
                foreach (var list in new[] { ItemContainersManager.WeaverTreeContainer.WeaverTreeLargeContainerNodeIDs, ItemContainersManager.WeaverTreeContainer.WeaverTreeSmallContainerNodeIDs })
                    if (list != null)
                        foreach (var pair in list) s_sockets[Convert.ToInt32(pair.Value, s_inv)] = pair.Key;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:sockets", $"Planner: the weaver tree's sockets couldn't be read: {e.Message}", 30);
            }
            return s_sockets;
        }
    }

    /// <summary>The game's container for a weaver tree socket (by its container id), or null.</summary>
    public static IItemContainer? SocketContainer(int socket)
    {
        var sockets = ItemContainersManager.Instance?.weaverTreeItems;
        if (sockets == null || !WeaverSockets.TryGetValue(socket, out byte node)) return null;
        IItemContainer? box = null;
        return sockets.TryGetContainer(node, out box) ? box : null;
    }

    /// <summary>
    /// Reads what the character has into <paramref name="side"/>, only when its signature changed; false when the game's
    /// containers aren't there (a zone load), leaving the last read as it was. Weaver sockets are read only when
    /// <paramref name="weaverItems"/> says the game has them.
    /// </summary>
    public static bool ReadEquipped(EquippedSide side, bool weaverItems)
    {
        var manager = ItemContainersManager.Instance;
        if (!Game.Alive(manager) || manager!.equipment == null) return false;
        ulong h = 14695981039346656037UL;
        foreach (var (slot, _, _) in GearSlots) MixBox(SlotContainer(slot), ref h);
        var idols = manager.idols?.TryCast<IItemContainer>();
        MixBox(idols, ref h);
        var blessings = manager.blessings?.Containers;
        for (int i = 0; blessings != null && i < blessings.Count; i++) MixBox(blessings[i]?.TryCast<IItemContainer>(), ref h);
        if (manager.idols != null) Mix(ref h, manager.idols.unlockIdx);
        var weaver = Game.LocalTree?.weaverTree?.nodes;
        for (int i = 0; weaver != null && i < weaver.Count; i++)
            if (weaver[i] is { pointsAllocated: > 0 } node) Mix(ref h, node.id << 8 | node.pointsAllocated);
        if (weaverItems)
            foreach (int socket in WeaverSockets.Keys) MixBox(SocketContainer(socket), ref h);
        if (side.Signature == h && h != 0) return true;

        side.Slots.Clear();
        side.Idols.Clear();
        side.Blessings.Clear();
        side.LockedCells.Clear();
        foreach (var (slot, _, _) in GearSlots)
            if (First(SlotContainer(slot)) is { } entry && Worn(entry) is { } worn) side.Slots[slot] = worn;
        var content = idols?.GetContent();
        for (int i = 0; content != null && i < content.Count; i++)
            if (content[i] is { } entry && Worn(entry) is { } worn) side.Idols[(entry.Position.x, entry.Position.y)] = worn;
        if (manager.idols is { } grid)
            for (int y = 0; y < grid.size.y; y++)
                for (int x = 0; x < grid.size.x; x++)
                    if (!grid.CheckSlotUnlocked(x, y)) side.LockedCells.Add((x, y));
        for (int i = 0; blessings != null && i < blessings.Count; i++)
            if (First(blessings[i]?.TryCast<IItemContainer>()) is { } entry && Worn(entry) is { } worn) side.Blessings[i] = worn;
        side.Weaver.Clear();
        for (int i = 0; weaver != null && i < weaver.Count; i++)
            if (weaver[i] is { pointsAllocated: > 0 } node) side.Weaver[node.id] = node.pointsAllocated;
        side.WeaverItems.Clear();
        if (weaverItems)
            foreach (int socket in WeaverSockets.Keys)
                if (First(SocketContainer(socket)) is { } entry && Worn(entry) is { } worn) side.WeaverItems[socket] = worn;
        side.Signature = h;
        return true;
    }

    private static ItemContainerEntry? First(IItemContainer? box)
    {
        var content = box?.GetContent();
        return content != null && content.Count > 0 ? content[0] : null;
    }

    private static WornItem? Worn(ItemContainerEntry entry) => Worn(entry.data);

    private static WornItem? Worn(ItemData? data)
    {
        if (data == null) return null;
        var worn = new WornItem { ItemType = data.itemType, SubType = data.subType, Unique = data.uniqueID, TwoHanded = TwoHandedType(data.itemType) };
        var affixes = data.affixes;
        for (int a = 0; affixes != null && a < affixes.Count; a++)
            if (affixes[a] is { } affix) worn.Affixes.Add((affix.affixId, affix.affixTier));
        return worn;
    }

    // The item's id bytes say everything the comparison reads (base, subtype, unique, affixes and tiers), with its place.
    private static void MixBox(IItemContainer? box, ref ulong h)
    {
        var content = box?.GetContent();
        Mix(ref h, content?.Count ?? -1);
        for (int i = 0; content != null && i < content.Count; i++)
        {
            var entry = content[i];
            if (entry?.data?.id is not { } id) continue;
            Mix(ref h, entry.Position.x << 8 | entry.Position.y);
            for (int b = 0; b < id.Length; b++) Mix(ref h, id[b]);
        }
    }

    private static void Mix(ref ulong h, int value)
    {
        h ^= (uint)value;
        h *= 1099511628211UL;
    }

    /// <summary>
    /// Compares a stage's planned gear with what the character has. Matches: the same base and subtype, or the same unique
    /// or set item, with every planned affix at its tier or higher (rolls ignored). Partial: that item with a planned
    /// affix missing or lower. To place: nothing there. Not in build: anything else there. Rings pair with the closer
    /// worn ring. A planned two-handed weapon leaves the off-hand unplanned.
    /// </summary>
    public static GearGuidance CompareGear(PlannedGear gear, EquippedSide side)
    {
        var guidance = new GearGuidance();
        bool twoHanded = gear.Slots.TryGetValue("weapon", out var weapon) && TwoHandedType(weapon.ItemType);
        foreach (var (slot, planned) in gear.Slots)
        {
            if (slot is "leftRing" or "rightRing" || slot == "offhand" && twoHanded) continue;
            guidance.Slots[slot] = Guide(planned, side.Slots.TryGetValue(slot, out var worn) ? worn : null);
        }
        PairRings(gear, side, guidance);
        for (int i = 0; i < GearSlots.Length; i++)
            if (guidance.Slots.TryGetValue(GearSlots[i].Slot, out var guide)) Count(guidance.Gear, guide, i);

        foreach (var planned in gear.Idols)
        {
            var guide = Guide(planned, side.Idols.TryGetValue((planned.X, planned.Y), out var worn) ? worn : null);
            // An idol planned over cells this character hasn't unlocked can't be placed yet.
            for (int y = planned.Y; y < planned.Y + planned.Height && !guide.Unknown; y++)
                for (int x = planned.X; x < planned.X + planned.Width; x++)
                    if (side.LockedCells.Contains((x, y))) guide.Unknown = guide.Locked = true;
            guidance.Idols[(planned.X, planned.Y)] = guide;
            Count(guidance.IdolList, guide, planned.Y * IdolGridWidth + planned.X);
        }

        foreach (var (timeline, planned) in gear.Blessings)
        {
            var guide = BlessingGuide(planned, side.Blessings.TryGetValue(timeline, out var worn) ? worn : null);
            guidance.Blessings[timeline] = guide;
            Count(guidance.BlessingList, guide, timeline);
        }

        // The weaver tree is compared as the passive tree is: points per node, and next steps when the planner gives an
        // order.
        if (gear.Weaver != null)
        {
            guidance.Weaver = new TreeGuide();
            CompareTree(guidance.Weaver, WeaverTreeId, gear.Weaver, side.Weaver, s_noUnknown, s_noCaps, null, NextStepCount);
        }

        foreach (var (socket, planned) in gear.WeaverItems)
        {
            var guide = Guide(planned, side.WeaverItems.TryGetValue(socket, out var worn) ? worn : null);
            // A socket this game version doesn't have.
            if (!WeaverSockets.ContainsKey(socket)) guide.Unknown = true;
            guidance.WeaverItems[socket] = guide;
            Count(guidance.WeaverItemList, guide, socket);
        }
        return guidance;
    }

    private static ItemGuide Guide(PlannedItem planned, WornItem? worn)
    {
        var guide = new ItemGuide { Planned = planned, Unknown = planned.Unmakeable && (planned.Unknown.Contains("base") || planned.Unknown.Contains("unique")) };
        if (worn == null)
        {
            guide.State = GuidanceState.ToPlace;
            return guide;
        }
        bool same = planned.Unique != 0 ? worn.Unique == planned.Unique : worn.Unique == 0 && worn.ItemType == planned.ItemType && worn.SubType == planned.SubType;
        if (!same)
        {
            guide.State = GuidanceState.NotInBuild;
            return guide;
        }
        foreach (var affix in planned.Affixes)
        {
            // An affix this game version lacks can't be checked.
            if (planned.Unknown.Contains($"affix {affix.Id}")) continue;
            if (!worn.Affixes.Exists(a => a.Id == affix.Id && a.Tier >= affix.Tier)) guide.Missing.Add((affix.Id, affix.Tier));
        }
        guide.State = guide.Missing.Count == 0 ? GuidanceState.Matches : GuidanceState.Partial;
        return guide;
    }

    // A blessing is its subtype; its roll is ignored as an item's are. A planned Grand blessing's normal one (another
    // subtype, the same name without "Grand ") is partial: the right blessing, not yet its Grand form.
    private static ItemGuide BlessingGuide(PlannedItem planned, WornItem? worn) => new()
    {
        Planned = planned,
        Unknown = planned.Unmakeable,
        State = worn == null ? GuidanceState.ToPlace
            : worn.SubType == planned.SubType && worn.ItemType == planned.ItemType ? GuidanceState.Matches
            : worn.ItemType == planned.ItemType && NormalOfGrand(planned, worn.SubType) ? GuidanceState.Partial
            : GuidanceState.NotInBuild,
    };

    private const string GrandPrefix = "Grand ";

    /// <summary>Whether a blessing subtype is the normal form of a planned Grand blessing, by the game's names.</summary>
    public static bool NormalOfGrand(PlannedItem planned, ushort subType)
    {
        if (!planned.Name.StartsWith(GrandPrefix, StringComparison.Ordinal)) return false;
        return SubTypeName(planned.ItemType, subType).Equals(planned.Name[GrandPrefix.Length..], StringComparison.OrdinalIgnoreCase);
    }

    // Each planned ring takes the worn ring it's closer to, so a ring worn on the other hand still matches. The guide is
    // kept under the worn slot it's paired with, where its icon goes.
    private static void PairRings(PlannedGear gear, EquippedSide side, GearGuidance guidance)
    {
        gear.Slots.TryGetValue("leftRing", out var left);
        gear.Slots.TryGetValue("rightRing", out var right);
        if (left == null && right == null) return;
        side.Slots.TryGetValue("leftRing", out var wornLeft);
        side.Slots.TryGetValue("rightRing", out var wornRight);
        ItemGuide? Of(PlannedItem? p, WornItem? w) => p != null ? Guide(p, w) : null;
        var (straightL, straightR) = (Of(left, wornLeft), Of(right, wornRight));
        var (crossedL, crossedR) = (Of(right, wornLeft), Of(left, wornRight));
        bool crossed = Score(crossedL) + Score(crossedR) > Score(straightL) + Score(straightR);
        var (onLeft, onRight) = crossed ? (crossedL, crossedR) : (straightL, straightR);
        if (onLeft != null) guidance.Slots["leftRing"] = onLeft;
        if (onRight != null) guidance.Slots["rightRing"] = onRight;
    }

    // How close a worn item is to the planned one: matching beats partial (fewer missing affixes closer) beats the rest.
    private static int Score(ItemGuide? guide) => guide?.State switch
    {
        GuidanceState.Matches => 100,
        GuidanceState.Partial => 50 - guide.Missing.Count,
        _ => 0,
    };

    private static void Count(ChecklistSection list, ItemGuide guide, int id)
    {
        if (guide.Unknown)
        {
            list.Unknown++;
            return;
        }
        list.Total++;
        switch (guide.State)
        {
            case GuidanceState.Matches: list.Done++; break;
            case GuidanceState.Partial: list.Partial.Add(id); break;
            case GuidanceState.ToPlace: list.Missing.Add(id); break;
            default: list.OffPlan.Add(id); break;
        }
    }

    /// <summary>A planned item against an item the character holds rather than wears (the one on the cursor).</summary>
    public static ItemGuide GuideAgainst(PlannedItem planned, ItemData item) =>
        planned.Place == PlannedPlace.Blessing ? BlessingGuide(planned, Worn(item)) : Guide(planned, Worn(item));

    /// <summary>The planned item for a slot whose icon or hovered item it is: rings take the one paired with that slot.</summary>
    public static ItemGuide? SlotGuide(GearGuidance? guidance, string slot) =>
        guidance != null && guidance.Slots.TryGetValue(slot, out var guide) ? guide : null;
}
