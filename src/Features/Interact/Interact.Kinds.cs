using EpochAssist.Core;
using Il2Cpp;
using UnityEngine;
using S = EpochAssist.Features.InteractSettings;

namespace EpochAssist.Features;

/// <summary>One kind the feature uses: its switch, the requirement group it needs, and whether it is a ground item.</summary>
internal sealed class InteractKind
{
    public InteractKind(string id, string label, string one, Toggle toggle, Toggle counted, string group, bool isItem)
    {
        Id = id;
        Counted = counted;
        Label = label;
        One = one;
        Toggle = toggle;
        Group = group;
        IsItem = isItem;
    }

    public string Id { get; }
    public string Label { get; }
    /// <summary>One of the kind, for a use without a name of its own ("Used Rune prison").</summary>
    public string One { get; }
    public Toggle Toggle { get; }
    /// <summary>Whether the mini HUD count adds this kind's uses.</summary>
    public Toggle Counted { get; }
    /// <summary>The feature's optional group the kind needs; while it is missing the kind is unavailable.</summary>
    public string Group { get; }
    public bool IsItem { get; }
}

internal static partial class Interact
{
    // Optional requirement groups, one per way of reaching the game; a missing one makes its kinds unavailable.
    public const string ObjectsGroup = "objects: click listener";
    public const string ItemsGroup = "items: ground label pick-up";
    public const string FilterGroup = "items: filter verdict";
    public const string InventoryGroup = "items: inventory count";

    public static readonly InteractKind Shrines = new("shrines", "Shrines", "Shrine", S.Shrines, S.HudShrines, ObjectsGroup, false);
    public static readonly InteractKind Chests = new("chests", "Chests", "Chest", S.Chests, S.HudChests, ObjectsGroup, false);
    public static readonly InteractKind RunePrisons = new("runeprisons", "Rune prisons", "Rune prison", S.RunePrisons, S.HudRunePrisons, ObjectsGroup, false);
    public static readonly InteractKind Forges = new("forges", "Forges", "Forge", S.Forges, S.HudForges, ObjectsGroup, false);
    public static readonly InteractKind Rifts = new("rifts", "Unstable rifts", "Unstable rift", S.Rifts, S.HudRifts, ObjectsGroup, false);
    public static readonly InteractKind Crystals = new("crystals", "Blood crystals", "Blood crystal", S.Crystals, S.HudCrystals, ObjectsGroup, false);
    public static readonly InteractKind Keys = new("keys", "Keys", "Key", S.Keys, S.HudKeys, ItemsGroup, true);
    public static readonly InteractKind Materials = new("materials", "Materials", "Material", S.Materials, S.HudMaterials, ItemsGroup, true);
    public static readonly InteractKind Filtered = new("filter", "Loot filter items", "Item", S.FilterShown, S.HudFilter, FilterGroup, true);

    public static readonly InteractKind[] Kinds = { Shrines, Chests, RunePrisons, Forges, Rifts, Crystals, Keys, Materials, Filtered };

    // Base types the game gives crafting materials and keys (affix shards, runes, glyphs; keys). The loot filter never
    // judges these, so only their own switches pick them up.
    private const int ShardType = 101, RuneType = 102, GlyphType = 103, KeyType = 104;

    // Echo chests aren't world-model chests: caches, echo reward chests and cemetery reward chests are told apart by
    // the name of the listener or one of its parents in the game's own click listener list.
    private static readonly string[] s_echoChestNames = { "Cache Click Listener", "Monolith Reward Chest", "Tomb Reward Chest" };
    // A rune prison: opening it starts the Exiled Mage fight, the one encounter the feature starts (its own switch).
    private const string RunePrisonName = "Rune Prison Visuals";
    // An echo's forge: using it starts its event (its own switch). It can trigger again, so a used one is remembered.
    private const string ForgeName = "Client_Forge";
    // An echo's unstable rift: using it starts its event where the player stands (the Time Beast rift, which moves the
    // player, is never one). It can trigger again too.
    private const string RiftName = "Client_UnstableRift";
    // An echo's blood crystal: using it starts the Rage of Morditas event where the player stands.
    private const string CrystalName = "RageOfMorditas_StartInteract";

    // Kept below the game's reach, so a frame of movement can't put the call past the edge: the game's click handler
    // walks the character to anything farther.
    public const float ReachMargin = 0.1f;

    /// <summary>The kind of a ground item from its base type: keys or materials, else null (left to the filter switch).</summary>
    public static InteractKind? ItemKind(ItemDataUnpacked? item)
    {
        if (item == null) return null;
        return item.itemType switch
        {
            ShardType or RuneType or GlyphType => Materials,
            KeyType => Keys,
            _ => null,
        };
    }

    /// <summary>The game's pick-up range for every ground item (one value for all labels).</summary>
    public static float ItemReach => GroundItemLabel.pickupDistance;

    // A shrine or chest listener's object, cached per zone: GetComponent makes a new wrapper each call.
    private static WorldObjectClickListener? ListenerOf(Run run, WorldEntry entry)
    {
        if (!Game.Alive(entry.Object)) return null;
        var obj = entry.Object!;
        if (run.Listeners.TryGetValue(obj.Pointer, out var cached) && (cached == null || Game.Alive(cached))) return cached;
        var listener = obj.GetComponent<WorldObjectClickListener>() ?? obj.GetComponentInChildren<WorldObjectClickListener>();
        run.Listeners[obj.Pointer] = Game.Alive(listener) ? listener : null;
        return run.Listeners[obj.Pointer];
    }

    // The kind of a listener found in the game's list: an echo cache or reward chest, a rune prison's entrance, or an
    // event's start (forge, rift, blood crystal), by its own name or a parent's; null for anything else.
    private static InteractKind? ListedKind(WorldObjectClickListener listener)
    {
        for (var t = listener.transform; Game.Alive(t); t = t!.parent)
        {
            string name = t!.gameObject.name;
            if (name.Contains(RunePrisonName)) return RunePrisons;
            if (name.Contains(ForgeName)) return Forges;
            if (name.Contains(RiftName)) return Rifts;
            if (name.Contains(CrystalName)) return Crystals;
            foreach (var marker in s_echoChestNames)
            {
                if (name.Contains(marker)) return Chests;
            }
        }
        return null;
    }

    // A listener is used once the game says it can't be clicked again, or its handler fired.
    private static bool ListenerUsed(WorldObjectClickListener listener)
    {
        if (!Game.Alive(listener)) return true;
        if (!listener.CanInteract()) return true;
        var handler = listener.handler;
        return Game.Alive(handler) && handler!.triggered;
    }

    // The target the dispatcher checks and uses: the listener's own click, from in reach only.
    internal static InteractTarget ObjectTarget(InteractKind kind, string name, WorldObjectClickListener listener, float reach) =>
        new(kind.Id, name, listener, () => listener.transform.position, reach, () => ListenerUsed(listener),
            () => listener.ObjectClick(Player.Object, true));

    internal static InteractTarget ItemTarget(InteractKind kind, string name, GroundItemVisuals visual, uint id, float reach)
    {
        var label = visual.label!;
        return new(kind.Id, name, visual, () => visual.transform.position, reach, () => ItemGone(visual, id), label.requestPickup);
    }

    // A ground item is gone once its visual is destroyed, put back in the pool or reused for another item.
    private static bool ItemGone(GroundItemVisuals visual, uint id) =>
        !Game.Alive(visual) || !visual.gameObject.activeInHierarchy || visual.id != id;

    /// <summary>Items in the player's bag; null when it can't be read.</summary>
    private static int? InventoryCount()
    {
        var manager = ItemContainersManager.Instance;
        if (!Game.Alive(manager)) return null;
        var inventory = manager!.inventory;
        return inventory?.ContentCount();
    }
}
