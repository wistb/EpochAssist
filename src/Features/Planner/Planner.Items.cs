using EpochAssist.Core;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace EpochAssist.Features;

/// <summary>Where a planned item goes: a paper doll slot, an idol grid cell or a blessing timeline.</summary>
internal enum PlannedPlace { Slot, Idol, Blessing }

/// <summary>One planned affix: the game's id, its tier counted from 0, its roll as a byte, and how the item holds it.</summary>
internal readonly record struct PlannedAffix(ushort Id, byte Tier, byte Roll, SealedAffixType Sealed);

/// <summary>One equipment piece, idol or blessing of a stage, from the planner data. Runtime only, never stored.</summary>
internal sealed class PlannedItem
{
    public PlannedPlace Place;
    /// <summary>The paper doll slot (helmet, armor, weapon, offhand, amulet, leftRing, rightRing, belt, gloves, boots,
    /// relic); empty for an idol or a blessing.</summary>
    public string Slot = string.Empty;
    /// <summary>An idol's top-left grid cell.</summary>
    public int X, Y;
    /// <summary>A blessing's timeline, counted from 0.</summary>
    public int Timeline = -1;
    public byte ItemType;
    public ushort SubType;
    /// <summary>The unique or set id, 0 for none.</summary>
    public ushort Unique;
    /// <summary>As the game stores it: regular affix count up to 4, unique 7, set 8, unique with affixes 9.</summary>
    public byte Rarity;
    public bool Corrupted;
    public readonly List<PlannedAffix> Affixes = new();
    public byte[] Implicits = Array.Empty<byte>();
    public byte[] UniqueRolls = Array.Empty<byte>();
    /// <summary>The planner left out a roll, so the made item shows it at its best.</summary>
    public bool RollsFilled;
    /// <summary>What this game version lacks: "base", "unique" or "affix &lt;id&gt;". Filled by <see cref="Planner.Check"/>.</summary>
    public readonly List<string> Unknown = new();
    /// <summary>The base or the unique is unknown, so no tooltip can be made.</summary>
    public bool Unmakeable;
    /// <summary>Grid cells it covers (idols); 1x1 until checked against the game data.</summary>
    public int Width = 1, Height = 1;
    /// <summary>The game's name for it, filled when it's checked; the planner gives none.</summary>
    public string Name = string.Empty;
}

internal static partial class Planner
{
    /// <summary>The game keeps a fixed number of unique rolls; a planner lists only those the unique has.</summary>
    private const int UniqueRollCount = 8;
    // The byte a roll the planner doesn't give is made at: its best.
    private const byte BestRoll = 255;

    // Made items by planned item, for their tooltips; never placed in a container, saved or sent.
    private static readonly Dictionary<PlannedItem, ItemDataUnpacked> s_made = new();

    /// <summary>As the game stores it: the regular affix count up to 4; a unique 7, a set item 8, a unique with affixes 9.</summary>
    public static byte RarityOf(ushort unique, int regular)
    {
        if (unique == 0) return (byte)Math.Min(regular, 4);
        if (UniqueList.getUnique(unique) is { isSetItem: true }) return 8;
        return regular > 0 ? (byte)9 : (byte)7;
    }

    /// <summary>
    /// Checks a planned item against this game version's data: its base, unique and affixes; fills its name and grid
    /// size. An unknown base or unique makes it unmakeable; an unknown affix is only left out of the made item.
    /// </summary>
    public static void Check(PlannedItem item)
    {
        item.Unknown.Clear();
        var items = ItemList.get();
        BaseNameAndSize(items, item);
        if (item.Unique != 0)
        {
            var unique = UniqueList.getUnique(item.Unique);
            if (unique == null) item.Unknown.Add("unique");
            else if (FirstName(unique.displayName, unique.name) is { Length: > 0 } name) item.Name = name;
        }
        var affixes = AffixList.get();
        foreach (var affix in item.Affixes)
            if (affixes == null || affixes.GetAffix(affix.Id) == null) item.Unknown.Add($"affix {affix.Id}");
        item.Unmakeable = item.Unknown.Contains("base") || item.Unknown.Contains("unique");
    }

    private static string FirstName(params string?[] names) => names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty;

    private static void BaseNameAndSize(ItemList? items, PlannedItem item)
    {
        if (items == null)
        {
            item.Unknown.Add("base");
            return;
        }
        var baseItem = items.GetEquipmentBaseItem(item.ItemType);
        var sub = baseItem != null ? items.GetEquipmentSubItem(item.ItemType, item.SubType) : null;
        if (baseItem == null || sub == null)
        {
            // Blessings sit outside the equipment list.
            var other = items.GetNonEquipmentSubItem(item.ItemType, item.SubType);
            if (other == null)
            {
                item.Unknown.Add("base");
                return;
            }
            item.Name = FirstName(other.displayName, other.name, items.GetItemDisplayName(item.ItemType, item.SubType));
            return;
        }
        item.Name = FirstName(sub.displayName, sub.name, items.GetItemDisplayName(item.ItemType, item.SubType));
        var size = baseItem.gridSize;
        item.Width = Math.Max(1, size.x);
        item.Height = Math.Max(1, size.y);
    }

    /// <summary>
    /// The planned item as a game item in memory, made on first use and kept until <see cref="ForgetMade"/>; null when this
    /// game version lacks its base or unique, or the game refuses it. Its id bytes and values are filled by the game's
    /// own refresh, so its tooltip reads as a real item's. It's never placed in any container.
    /// </summary>
    public static ItemDataUnpacked? Made(PlannedItem planned)
    {
        if (s_made.TryGetValue(planned, out var made)) return made;
        if (planned.Unmakeable) return null;
        try
        {
            made = Make(planned, out string? refresh);
            if (refresh != null)
            {
                Log.ErrorThrottled("planner:make", $"Planner: the game refused a planned item ({refresh})", 30);
                made = null;
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:make", $"Planner: a planned item couldn't be made: {e.Message}", 30);
            made = null;
        }
        // Not tried again on every hover.
        if (made == null) planned.Unmakeable = true;
        else s_made[planned] = made;
        return made;
    }

    /// <summary>
    /// Makes a new item from a planned one, uncached. <paramref name="refresh"/> says whether the game's refresh worked;
    /// an item it refused is still returned, for the dev bridge to show.
    /// </summary>
    public static ItemDataUnpacked Make(PlannedItem planned, out string? refresh, byte? rarity = null)
    {
        var item = new ItemDataUnpacked
        {
            itemType = planned.ItemType,
            subType = planned.SubType,
            uniqueID = planned.Unique,
            rarity = rarity ?? planned.Rarity,
            corrupted = planned.Corrupted,
            affixes = new Il2CppSystem.Collections.Generic.List<ItemAffix>(),
            // The game keeps a fixed number of each; a planner lists only those the base or unique has.
            implicitRolls = new Il2CppStructArray<byte>(Padded(planned.Implicits, ItemData.numberOfImplicitRolls)),
            uniqueRolls = new Il2CppStructArray<byte>(Padded(planned.UniqueRolls, UniqueRollCount)),
        };
        var known = AffixList.get();
        foreach (var a in planned.Affixes)
        {
            if (known == null || known.GetAffix(a.Id) == null) continue;
            item.affixes.Add(new ItemAffix { affixId = a.Id, affixTier = a.Tier, affixRoll = a.Roll, sealedAffixType = a.Sealed });
            // The item's own flags say it holds a sealed affix; without them the tooltip leaves it out.
            if (a.Sealed == SealedAffixType.Regular) item.hasSealedRegularAffix = true;
            else if (a.Sealed == SealedAffixType.Primordial) item.hasSealedPrimordialAffix = true;
            else if (a.Sealed == SealedAffixType.FromCorruption) item.hasSealedAffixFromCorruption = true;
        }
        // The game's own step after an item's fields change: its id bytes and the values its tooltip reads.
        try
        {
            item.RefreshIDAndValues();
            refresh = null;
        }
        catch (Exception e)
        {
            refresh = $"{e.GetType().Name}: {e.Message}";
        }
        return item;
    }

    /// <summary>Drops every made item (a stage, build or scene change, or the feature stopping).</summary>
    public static void ForgetMade() => s_made.Clear();

    private static byte[] Padded(byte[] rolls, int count)
    {
        var all = new byte[Math.Max(count, rolls.Length)];
        Array.Fill(all, BestRoll);
        rolls.CopyTo(all, 0);
        return all;
    }
}
