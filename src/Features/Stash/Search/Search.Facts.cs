using System.Diagnostics;
using EpochAssist.Core;
using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class StashSearch
{
    /// <summary>One stat line on an item: what it is, its shown tier (affixes), each number with its range.</summary>
    internal sealed record StatFact(StatRef Ref, string Text, bool Prefix, bool Suffix, bool Sealed, RowKind Kind, int? Tier, StatValue[] Numbers);

    /// <summary>Everything a search checks about one item, read once per item key.</summary>
    internal sealed class ItemFacts
    {
        public string Key = string.Empty;
        public string Name = string.Empty;
        public int Type, Sub, UniqueId, Lp, Ww, Wt, Fp, LevelReq, Class, W, H;
        /// <summary>Prefixes and suffixes, sealed ones aside, as the game counts them; -1 when unread.</summary>
        public int Prefixes = -1, Suffixes = -1;
        /// <summary>One of <see cref="RarityWords"/>.</summary>
        public string Rarity = "normal";
        public bool Corrupted, Sealed;
        public List<StatFact> Stats = new();
        /// <summary>Totals by key (contracts table), in shown units.</summary>
        public Dictionary<string, double> Pseudo = new();
    }

    /// <summary>Where one copy of an item is: its entry (the window's element points at it), tab and cells.</summary>
    internal readonly record struct ItemPlace(string Key, IntPtr Entry, int TabId, int X, int Y, int W, int H);

    // Facts per item key; places from the last whole pass; the pass being walked.
    private static readonly Dictionary<string, ItemFacts> s_facts = new();
    private static List<ItemPlace> s_places = new();
    private static List<ItemPlace>? s_pass;
    private static int s_passContainer, s_passEntry, s_passNew;
    private static float s_nextPass;
    private static Stash.StashSnapshot? s_snap;
    // Per entry: the item data and id array it pointed at, and the key read from them; a forge or swap changes one.
    private static readonly Dictionary<IntPtr, (IntPtr Data, IntPtr Id, string Key)> s_seen = new();
    private static readonly HashSet<IntPtr> s_seenThisPass = new();
    // Per container: its item list and that list's version at the last pass, and the places found in it. A container
    // whose list is unchanged is taken as it was (stash items change only by leaving and entering a tab); one container
    // a pass is walked anyway, in turn, for moves within a tab.
    private static readonly Dictionary<int, (IntPtr List, int Version, List<ItemPlace> Places)> s_containers = new();
    private static readonly Dictionary<int, (IntPtr List, int Version, List<ItemPlace> Places)> s_passContainers = new();
    private static int s_passCount;
    // Changes whenever the facts or places change (results regroup, matching re-runs for changed keys).
    private static int s_factsGeneration;
    private static readonly HashSet<string> s_changedKeys = new();

    // The facts slice and the scan's own cost, for `stash search time`.
    private const double FactsMsPerFrame = 1.0;
    private static double s_scanMsTotal, s_scanMsMax;
    private static int s_scanFrames, s_scanItems;

    /// <summary>Items with facts read.</summary>
    public static int FactsCount => s_facts.Count;

    /// <summary>A pass over the stash is under way (reading new items or checking known ones).</summary>
    public static int FactsQueued => s_pass != null ? Math.Max(1, s_passNew) : 0;

    /// <summary>Every place of every item, from the last whole pass.</summary>
    public static IReadOnlyList<ItemPlace> Places => s_places;

    /// <summary>Facts for a key; null when not read.</summary>
    public static ItemFacts? FactsOf(string key) => s_facts.TryGetValue(key, out var f) ? f : null;

    /// <summary>The stash read the last pass used for tab names, folders and order.</summary>
    public static Stash.StashSnapshot? Snapshot => s_snap;

    /// <summary>The scan's cost so far: items read, total and largest ms in a frame, frames.</summary>
    public static (int Items, double TotalMs, double MaxMs, int Frames) ScanCost => (s_scanItems, s_scanMsTotal, s_scanMsMax, s_scanFrames);

    private static void ResetFacts()
    {
        s_facts.Clear();
        s_places = new List<ItemPlace>();
        s_pass = null;
        s_seen.Clear();
        s_seenThisPass.Clear();
        s_containers.Clear();
        s_passContainers.Clear();
        s_changedKeys.Clear();
        s_snap = null;
        s_nextPass = 0f;
        s_factsGeneration++;
        s_scanMsTotal = s_scanMsMax = 0;
        s_scanFrames = s_scanItems = 0;
    }

    // Twice a second: starts a pass over the stash if none is under way.
    private static void Poll()
    {
        if (s_pass != null || Time.unscaledTime < s_nextPass) return;
        s_snap = Stash.Read();
        if (s_snap == null)
        {
            if (s_places.Count > 0 || s_facts.Count > 0) ResetFacts();
            return;
        }
        s_pass = new List<ItemPlace>(s_places.Count + 16);
        s_passContainer = s_passEntry = s_passNew = 0;
        s_seenThisPass.Clear();
        s_passContainers.Clear();
        s_passCount++;
    }

    // Walks the pass for a slice of the frame: known entries cost a pointer check, new ones a facts read.
    private static void FactsSlice()
    {
        if (s_pass == null) return;
        long start = Stopwatch.GetTimestamp();
        long until = start + (long)(FactsMsPerFrame * Stopwatch.Frequency / 1000d);
        try
        {
            var containers = Containers();
            // Container n holds the items of the tab with id n; only trusted when there is one for each tab.
            if (containers == null || s_snap == null || containers.Count != s_snap.TabCount)
            {
                EndPass(complete: false);
                return;
            }
            while (s_passContainer < containers.Count)
            {
                int index = s_passContainer;
                var content = containers[index]?.content;
                if (content == null)
                {
                    s_passContainer++;
                    s_passEntry = 0;
                    continue;
                }
                if (s_passEntry == 0)
                {
                    IntPtr list = content.Pointer;
                    int version = content._version;
                    bool turn = s_passCount % Math.Max(1, containers.Count) == index;
                    if (!turn && s_containers.TryGetValue(index, out var known) && known.List == list && known.Version == version && known.Places.All(p => s_facts.ContainsKey(p.Key)))
                    {
                        s_pass.AddRange(known.Places);
                        foreach (var p in known.Places) s_seenThisPass.Add(p.Entry);
                        s_passContainers[index] = known;
                        s_passContainer++;
                        continue;
                    }
                    s_passContainers[index] = (list, version, new List<ItemPlace>());
                }
                if (s_passEntry >= content.Count)
                {
                    s_passContainer++;
                    s_passEntry = 0;
                    continue;
                }
                var e = content[s_passEntry++];
                if (e != null && Visit(e, index) is { } place) s_passContainers[index].Places.Add(place);
                if (Stopwatch.GetTimestamp() > until) return;
            }
            EndPass(complete: true);
        }
        catch (Exception ex)
        {
            Log.ErrorThrottled("search:facts", $"Stash search: stash items unreadable: {ex.Message}", 30);
            EndPass(complete: false);
        }
        finally
        {
            double ms = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
            s_scanMsTotal += ms;
            s_scanMsMax = Math.Max(s_scanMsMax, ms);
            s_scanFrames++;
        }
    }

    private static ItemPlace? Visit(ItemContainerEntry e, int tab)
    {
        var data = e.data;
        if (data == null) return null;
        IntPtr entry = e.Pointer, dataPtr = data.Pointer;
        var idArray = data.id;
        IntPtr idPtr = idArray?.Pointer ?? IntPtr.Zero;
        s_seenThisPass.Add(entry);
        string key;
        if (s_seen.TryGetValue(entry, out var seen) && seen.Data == dataPtr && seen.Id == idPtr && s_facts.ContainsKey(seen.Key))
            key = seen.Key;
        else
        {
            var unpacked = data.getAsUnpacked();
            if (unpacked == null) return null;
            key = idArray != null && idArray.Length > 0 ? Convert.ToHexString(idArray.ToArray()) : "entry:" + entry.ToInt64().ToString("x");
            s_seen[entry] = (dataPtr, idPtr, key);
            if (!s_facts.ContainsKey(key))
            {
                var size = e.size;
                s_facts[key] = ReadFacts(unpacked, key, size.x, size.y);
                s_changedKeys.Add(key);
                s_boxChanged.Add(key);
                s_passNew++;
                s_scanItems++;
            }
        }
        var pos = e.Position;
        var sz = e.size;
        var place = new ItemPlace(key, entry, tab, pos.x, pos.y, sz.x, sz.y);
        s_pass!.Add(place);
        return place;
    }

    private static void EndPass(bool complete)
    {
        var pass = s_pass;
        s_pass = null;
        s_nextPass = Time.unscaledTime + PollSeconds;
        if (!complete || pass == null) return;
        bool changed = s_passNew > 0 || pass.Count != s_places.Count || !pass.SequenceEqual(s_places);
        s_places = pass;
        s_containers.Clear();
        foreach (var (index, known) in s_passContainers) s_containers[index] = known;
        // Forget entries and facts no longer in the stash.
        foreach (var gone in s_seen.Keys.Where(k => !s_seenThisPass.Contains(k)).ToList()) s_seen.Remove(gone);
        var live = new HashSet<string>(pass.Select(p => p.Key));
        foreach (var gone in s_facts.Keys.Where(k => !live.Contains(k)).ToList())
        {
            s_facts.Remove(gone);
            s_changedKeys.Add(gone);
            changed = true;
        }
        if (changed) s_factsGeneration++;
    }

    private static Il2CppSystem.Collections.Generic.List<ItemContainer>? Containers()
    {
        var holder = ItemContainersManager.Instance?.stash;
        var all = holder?.stashItemContainers;
        if (all == null || all.Length == 0) return null;
        int index = holder!._CurrentStashIndex_k__BackingField;
        return (index >= 0 && index < all.Length ? all[index] : all[0])?.containers;
    }

    // ---- reading one item ----

    /// <summary>Reads an item's facts: fields, every affix with its numbers, implicits, unique rolls, totals.</summary>
    internal static ItemFacts ReadFacts(ItemDataUnpacked d, string key, int w, int h)
    {
        var f = new ItemFacts
        {
            Key = key, W = w, H = h,
            Name = d.FullName ?? string.Empty,
            Type = d.itemType, Sub = d.subType, UniqueId = d.uniqueID,
            Lp = d.legendaryPotential, Ww = d.weaversWill, Wt = d.weaversTouch, Fp = d.forgingPotential,
            LevelReq = d.LvlReq, Class = (int)d.classReq, Corrupted = d.corrupted,
            // A corruption seals its own affix; "sealed" means the player's seals.
            Sealed = d.hasSealedRegularAffix || d.hasSealedPrimordialAffix,
            Rarity = RarityOf(d),
        };
        if (Has(CountsGroup))
        {
            try
            {
                d.GetNonSealedPrefixAndSuffixCount(out int prefixes, out int suffixes);
                (f.Prefixes, f.Suffixes) = (prefixes, suffixes);
            }
            catch (Exception)
            {
            }
        }
        float modifier = GameData.AffixModifier(d) ?? 0f;
        var affixes = d.affixes;
        for (int i = 0; affixes != null && i < affixes.Count; i++)
        {
            var a = affixes[i];
            if (a == null) continue;
            int id = a.affixId;
            bool sealedByPlayer = a.sealedAffixType is SealedAffixType.Regular or SealedAffixType.Primordial;
            f.Stats.Add(new StatFact(new StatRef(StatSource.Affix, id.ToString()), GameData.Affix(id).Name ?? $"Affix {id}",
                a.affixType == AffixList.AffixType.PREFIX, a.affixType == AffixList.AffixType.SUFFIX, sealedByPlayer, KindOf(a), a.DisplayTier,
                GameData.AffixValues(id, a.affixTier, a.affixRoll, modifier)));
        }
        foreach (var line in GameData.ImplicitValues(d))
            f.Stats.Add(new StatFact(new StatRef(StatSource.Implicit, line.Key), line.Text, false, false, false, RowKind.Any, null, line.Numbers));
        foreach (var line in GameData.UniqueRollValues(d))
            f.Stats.Add(new StatFact(new StatRef(StatSource.UniqueRoll, line.Key), line.Text, false, false, false, RowKind.Any, null, line.Numbers));
        AddTotals(f);
        return f;
    }

    private static string RarityOf(ItemDataUnpacked d)
    {
        if (d.isLegendary()) return "legendary";
        if (d.isSet()) return "set";
        if (d.isUnique()) return "unique";
        if (d.isExaltedItem()) return "exalted";
        if (d.isRare()) return "rare";
        if (d.isMagic()) return "magic";
        return "normal";
    }

    private static RowKind KindOf(ItemAffix a)
    {
        if (a.IsChampion) return RowKind.Champion;
        return a.specialAffixType switch
        {
            AffixList.SpecialAffixType.Corrupted => RowKind.Corrupted,
            AffixList.SpecialAffixType.Experimental => RowKind.Experimental,
            AffixList.SpecialAffixType.Set => RowKind.Set,
            AffixList.SpecialAffixType.IdolEnchantment => RowKind.Enchant,
            AffixList.SpecialAffixType.IdolWeaver => RowKind.Weaver,
            _ => RowKind.Standard,
        };
    }

    // ---- totals ----

    /// <summary>A total: its key, shown name, and the (property, type) lines that add to it with how many times each counts.</summary>
    internal sealed record PseudoStat(string Key, string Name, (SP Property, BaseStats.ModType Type, int Times)[] Adds);

    private static readonly BaseStats.ModType Added = BaseStats.ModType.ADDED;

    /// <summary>The totals a row can ask for. A line counts when its tags are none: lines for minions, totems, while
    /// channelling and the like are conditional.</summary>
    public static readonly PseudoStat[] PseudoStats =
    {
        new("life", "Total maximum health", new[] { (SP.Health, Added, 1) }),
        new("mana", "Total maximum mana", new[] { (SP.Mana, Added, 1) }),
        Res("res.fire", "fire", SP.FireResistance, elemental: true),
        Res("res.cold", "cold", SP.ColdResistance, elemental: true),
        Res("res.lightning", "lightning", SP.LightningResistance, elemental: true),
        Res("res.void", "void", SP.VoidResistance, elemental: false),
        Res("res.necrotic", "necrotic", SP.NecroticResistance, elemental: false),
        Res("res.physical", "physical", SP.PhysicalResistance, elemental: false),
        Res("res.poison", "poison", SP.PoisonResistance, elemental: false),
        new("res.elemental", "Total elemental resistance", new[]
        {
            (SP.FireResistance, Added, 1), (SP.ColdResistance, Added, 1), (SP.LightningResistance, Added, 1),
            (SP.ElementalResistance, Added, 3), (SP.AllResistances, Added, 3),
        }),
        new("res.total", "Total resistance", new[]
        {
            (SP.FireResistance, Added, 1), (SP.ColdResistance, Added, 1), (SP.LightningResistance, Added, 1), (SP.VoidResistance, Added, 1),
            (SP.NecroticResistance, Added, 1), (SP.PhysicalResistance, Added, 1), (SP.PoisonResistance, Added, 1),
            (SP.ElementalResistance, Added, 3), (SP.AllResistances, Added, 7),
        }),
        Attr("attr.str", "strength", SP.Strength),
        Attr("attr.dex", "dexterity", SP.Dexterity),
        Attr("attr.int", "intelligence", SP.Intelligence),
        Attr("attr.att", "attunement", SP.Attunement),
        Attr("attr.vit", "vitality", SP.Vitality),
        new("attr.total", "Total attributes", new[]
        {
            (SP.Strength, Added, 1), (SP.Dexterity, Added, 1), (SP.Intelligence, Added, 1), (SP.Attunement, Added, 1), (SP.Vitality, Added, 1),
            (SP.AllAttributes, Added, 5),
        }),
        new("movespeed", "Total increased movement speed", new[] { (SP.Movespeed, BaseStats.ModType.INCREASED, 1) }),
        new("regen", "Total health regeneration", new[] { (SP.HealthRegen, Added, 1) }),
        new("armour", "Total armour", new[] { (SP.Armour, Added, 1) }),
        new("dodge", "Total dodge rating", new[] { (SP.DodgeRating, Added, 1) }),
        new("ward", "Total ward per second", new[] { (SP.WardRegen, Added, 1) }),
    };

    private static PseudoStat Res(string key, string name, SP property, bool elemental) =>
        new(key, $"Total {name} resistance", elemental
            ? new[] { (property, Added, 1), (SP.ElementalResistance, Added, 1), (SP.AllResistances, Added, 1) }
            : new[] { (property, Added, 1), (SP.AllResistances, Added, 1) });

    private static PseudoStat Attr(string key, string name, SP property) =>
        new(key, $"Total {name}", new[] { (property, Added, 1), (SP.AllAttributes, Added, 1) });

    // Sums every unconditional line once per total it adds to (an all-resistances line seven times in the total).
    private static void AddTotals(ItemFacts f)
    {
        foreach (var stat in f.Stats)
            foreach (var n in stat.Numbers)
            {
                if (n.Tags != 0) continue;
                foreach (var p in PseudoStats)
                    foreach (var (property, type, times) in p.Adds)
                        if ((int)property == n.Property && (int)type == n.ModType)
                            f.Pseudo[p.Key] = f.Pseudo.GetValueOrDefault(p.Key) + n.Value * times;
            }
    }
}
