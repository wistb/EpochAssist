using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal enum DataKind { Affix, MonsterMod, Ability, Item, Buff }

/// <summary>A name looked up in the game's data; Name null means the data doesn't have the key (unknown).</summary>
internal sealed class DataEntry
{
    public DataKind Kind { get; init; }
    /// <summary>The key as looked up: an int, a string, or for an item its id.</summary>
    public object? Key { get; init; }
    public string? Name { get; init; }
    /// <summary>Monster mods: the mod's internal (asset) name, where Name is the nameplate's text.</summary>
    public string? InternalName { get; init; }
    /// <summary>Affixes: the title the item name takes from it ("Rogue's").</summary>
    public string? Title { get; init; }
    /// <summary>Affixes: the tier as the tooltip shows it (tier + 1).</summary>
    public int? ShownTier { get; init; }
    /// <summary>Affixes: the asked tier's value range, as stored (0.48 for 48%).</summary>
    public float? RangeMin { get; init; }
    public float? RangeMax { get; init; }
    /// <summary>Affixes: the value the asked roll gives within the range.</summary>
    public float? Value { get; init; }
    /// <summary>Items: the base type's name.</summary>
    public string? BaseName { get; init; }
    /// <summary>Items: the unique or set item's name.</summary>
    public string? UniqueName { get; init; }

    public bool Known => Name != null;
}

/// <summary>
/// Names and affix tiers from the game's own data, read on first use and kept for the session, misses too. Lookups
/// never throw: a failure, a missing group or a stopped data feature answers unknown. Only the game's read-only
/// lookups are called.
/// </summary>
internal static class GameData
{
    public const string Affixes = "data: affixes";
    public const string MonsterMods = "data: monster mods";
    public const string Abilities = "data: abilities";
    public const string Items = "data: items";
    public const string Buffs = "data: buffs";

    private static readonly Dictionary<int, DataEntry> s_buffs = new();
    private static readonly Dictionary<string, DataEntry> s_abilities = new();
    private static readonly Dictionary<int, DataEntry> s_mods = new();
    private static readonly Dictionary<int, DataEntry> s_affixNames = new();
    private static readonly Dictionary<(int Id, int Tier, int Roll, float Modifier), DataEntry> s_affixValues = new();
    private static readonly Dictionary<string, DataEntry> s_items = new();

    /// <summary>Lookups that read the game this session, for the dev check that a repeat doesn't.</summary>
    public static int GameReads { get; private set; }

    private static Feature? s_feature;

    private static Feature? Running
    {
        get
        {
            if (s_feature == null || s_feature.State != FeatureState.Running) s_feature = FeatureHost.Find("data");
            return s_feature?.State == FeatureState.Running ? s_feature : null;
        }
    }

    /// <summary>True while the data feature runs and the kind's group is present.</summary>
    public static bool Has(string kind) => Running is { } feature && feature.Has(kind);

    public static void Clear()
    {
        s_buffs.Clear();
        s_abilities.Clear();
        s_mods.Clear();
        s_affixNames.Clear();
        s_affixValues.Clear();
        s_items.Clear();
        GameReads = 0;
        s_feature = null;
    }

    /// <summary>A buff or debuff by ailment id; the id's enum name when the ailment has no display name.</summary>
    public static DataEntry Buff(int ailmentId) =>
        Cached(s_buffs, ailmentId, Buffs, DataKind.Buff, () =>
        {
            // An id the list doesn't have still returns some ailment, so the id is checked.
            Ailment? ailment = AilmentList.getAilment(ailmentId);
            if (Game.Alive(ailment) && (int)ailment!.id != ailmentId) ailment = null;
            string? name = Game.Alive(ailment) && !string.IsNullOrEmpty(ailment!.displayName) ? ailment.displayName : null;
            // AilmentID is byte-sized, so an id outside 0–255 has no enum name.
            if (name == null && ailmentId is >= 0 and <= byte.MaxValue && Enum.IsDefined(typeof(AilmentID), (byte)ailmentId)) name = ((AilmentID)ailmentId).ToString();
            return new DataEntry { Kind = DataKind.Buff, Key = ailmentId, Name = name };
        });

    /// <summary>A player ability by its id (the bar's playerAbilityID).</summary>
    public static DataEntry Ability(string playerAbilityId) =>
        Cached(s_abilities, playerAbilityId, Abilities, DataKind.Ability, () =>
        {
            Ability? ability = AbilityManager.getPlayerAbility(playerAbilityId);
            string? name = Game.Alive(ability) && !string.IsNullOrEmpty(ability!.abilityName) ? ability.abilityName : null;
            return new DataEntry { Kind = DataKind.Ability, Key = playerAbilityId, Name = name };
        });

    /// <summary>A monster mod by its key (Actor.appliedModKeys).</summary>
    public static DataEntry MonsterMod(int key) =>
        Cached(s_mods, key, MonsterMods, DataKind.MonsterMod, () =>
        {
            var byKey = MonsterRarityManager.monsterModByKey;
            Il2Cpp.MonsterMod? mod = null;
            if (byKey != null && byKey.ContainsKey(key)) mod = byKey[key];
            return ModEntry(key, mod);
        });

    /// <summary>A monster mod an enemy carries (ActorDisplayInformation.appliedMods), kept by its key.</summary>
    public static DataEntry MonsterMod(Il2Cpp.MonsterMod mod)
    {
        if (!Has(MonsterMods) || !Game.Alive(mod)) return Unknown(DataKind.MonsterMod, null);
        int key;
        try
        {
            key = mod.modKey;
        }
        catch (Exception e)
        {
            Failed(DataKind.MonsterMod, e);
            return Unknown(DataKind.MonsterMod, null);
        }
        return Cached(s_mods, key, MonsterMods, DataKind.MonsterMod, () => ModEntry(key, mod));
    }

    // The nameplate shows the mod's description ("Regenerates Health", title-cased there); its asset name is internal
    // ("Added Health Regen") and stands in only when there is no description.
    private static DataEntry ModEntry(int key, Il2Cpp.MonsterMod? mod)
    {
        if (!Game.Alive(mod)) return Unknown(DataKind.MonsterMod, key);
        string? asset = string.IsNullOrEmpty(mod!.name) ? null : mod.name;
        string? shown = string.IsNullOrEmpty(mod.description) ? asset : mod.description;
        return new DataEntry { Kind = DataKind.MonsterMod, Key = key, Name = shown, InternalName = asset };
    }

    /// <summary>
    /// An affix by id, with the shown tier for a tier, and the tier's range and the value of a roll as the game works
    /// them out (its own rounding included). The effect modifier is the item's extra scaling (<see cref="AffixModifier"/>:
    /// 0.5 on body armour, 0 on most items). For a multi-property affix the range and value are its first property's.
    /// </summary>
    public static DataEntry Affix(int id, int? tier = null, byte? roll = null, float modifier = 0f)
    {
        DataEntry named = Cached(s_affixNames, id, Affixes, DataKind.Affix, () =>
        {
            AffixList.Affix? affix = AffixList.get()?.GetAffix(id);
            if (affix == null) return Unknown(DataKind.Affix, id);
            // The localised display name is the one items and the forge show; the plain name is internal and can carry
            // a class prefix ("Rogue Increased Throwing Physical Damage"), and the display name field is often empty.
            string? shown = affix.getAffixDisplayName()?.Trim();
            string? name = !string.IsNullOrEmpty(shown) ? shown : string.IsNullOrEmpty(affix.affixName) ? null : affix.affixName;
            string? title = string.IsNullOrEmpty(affix.affixTitle) ? null : affix.affixTitle;
            return new DataEntry { Kind = DataKind.Affix, Key = id, Name = name, Title = title };
        });
        if (tier is not int t || !named.Known) return named;

        return Cached(s_affixValues, (id, t, roll ?? -1, modifier), Affixes, DataKind.Affix, () =>
        {
            float? min = null, max = null, value = null;
            var list = AffixList.get();
            var affix = list?.GetAffix(id);
            if (affix?.tiers is { } tiers && t >= 0 && t < tiers.Count)
            {
                // The range is the value of the lowest and highest roll: the game's range methods return a tuple that
                // doesn't come through interop, and this way the ends get the same rounding as the tooltip.
                // The set-property count only matters for set affixes; an item's own affixes carry 1.
                if (affix.TryCast<AffixList.SingleAffix>() is { } single)
                {
                    min = list!.GetSingleAffixValue(single, modifier, t, byte.MinValue, 1);
                    max = list.GetSingleAffixValue(single, modifier, t, byte.MaxValue, 1);
                    if (roll is byte r) value = list.GetSingleAffixValue(single, modifier, t, r, 1);
                }
                else if (affix.TryCast<AffixList.MultiAffix>() is { } multi)
                {
                    min = list!.GetMultiAffixPropertyValue(multi, modifier, t, byte.MinValue, 0, 1);
                    max = list.GetMultiAffixPropertyValue(multi, modifier, t, byte.MaxValue, 0, 1);
                    if (roll is byte r) value = list.GetMultiAffixPropertyValue(multi, modifier, t, r, 0, 1);
                }
            }
            return new DataEntry
            {
                Kind = DataKind.Affix, Key = id, Name = named.Name, Title = named.Title, ShownTier = t + 1,
                RangeMin = min, RangeMax = max, Value = value,
            };
        });
    }

    /// <summary>The item's extra affix scaling (0.5 on body armour, 0 on most items); null when it can't be read.</summary>
    public static float? AffixModifier(ItemData item)
    {
        if (!Has(Affixes) || !Game.Alive(item)) return null;
        try
        {
            return ItemList.get()?.getAffixEffectModifier(item);
        }
        catch (Exception e)
        {
            Failed(DataKind.Affix, e);
            return null;
        }
    }

    /// <summary>An item's full name, base type name and unique or set name. Kept per item id.</summary>
    public static DataEntry Item(ItemDataUnpacked item)
    {
        if (!Has(Items) || !Game.Alive(item)) return Unknown(DataKind.Item, null);
        string? key;
        try
        {
            var id = item.id;
            key = id != null && id.Length > 0 ? Convert.ToHexString(id.ToArray()) : null;
        }
        catch (Exception e)
        {
            Failed(DataKind.Item, e);
            return Unknown(DataKind.Item, null);
        }
        // Items without an id aren't kept: nothing tells two of them apart.
        return key != null ? Cached(s_items, key, Items, DataKind.Item, () => ItemEntry(item, key)) : Read(DataKind.Item, () => ItemEntry(item, null));
    }

    private static DataEntry ItemEntry(ItemDataUnpacked item, string? key)
    {
        string? unique = null;
        if (item.isSet()) unique = UniqueList.getUniqueName(item.getSetItemUniqueId(), true);
        else if (item.isUniqueOrLegendary()) unique = UniqueList.getUniqueName(item.uniqueID, true);
        return new DataEntry
        {
            Kind = DataKind.Item, Key = key,
            Name = string.IsNullOrEmpty(item.FullName) ? null : item.FullName,
            BaseName = string.IsNullOrEmpty(item.LocalisedBaseTypeName) ? null : item.LocalisedBaseTypeName,
            UniqueName = string.IsNullOrEmpty(unique) ? null : unique,
        };
    }

    // The first lookup of a key reads the game; a failure answers unknown this time and is tried again next time.
    private static DataEntry Cached<TKey>(Dictionary<TKey, DataEntry> cache, TKey key, string group, DataKind kind, Func<DataEntry> read)
        where TKey : notnull
    {
        if (!Has(group)) return Unknown(kind, key);
        if (cache.TryGetValue(key, out var entry)) return entry;
        try
        {
            entry = read();
        }
        catch (Exception e)
        {
            Failed(kind, e);
            return Unknown(kind, key);
        }
        GameReads++;
        cache[key] = entry;
        Log.Debug($"Game data: {kind} {key} read: {entry.Name ?? "unknown"}");
        return entry;
    }

    private static DataEntry Read(DataKind kind, Func<DataEntry> read)
    {
        try
        {
            GameReads++;
            return read();
        }
        catch (Exception e)
        {
            Failed(kind, e);
            return Unknown(kind, null);
        }
    }

    private static DataEntry Unknown(DataKind kind, object? key) => new() { Kind = kind, Key = key };

    private static void Failed(DataKind kind, Exception e) =>
        Log.ErrorThrottled($"data:{kind}", $"Game data {kind} lookup failed: {e.Message}", 30);
}
