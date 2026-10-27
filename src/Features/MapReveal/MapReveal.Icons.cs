using EpochAssist.Core;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using S = EpochAssist.Features.MapRevealSettings;

namespace EpochAssist.Features;

// The game's own icons it keeps switched off until the player walks near (a trigger switches the icon's object on).
// They are found from the game's click listener list and omen window list, never by a scene-wide search, and shown by
// switching the icon's own object on: never the world object, never the trigger.
internal static partial class MapReveal
{
    public const string IconsGroup = "icons";
    public const string OmenGroup = "icons: omen windows";

    private const float IconScanSeconds = 1f;

    /// <summary>A kind of icon the player can switch: its id, setting, the root names that mark it, and its group.</summary>
    internal sealed record IconKind(string Id, Toggle Toggle, string[] Names, string Group);

    // Root object names per kind, matched on the spawned root of each listener (or omen window). Kept here, not shared
    // with proximity interact, so each feature can ship on its own.
    public static readonly IconKind Shrines = new("shrines", S.Shrines, new[] { "Shrine" }, IconsGroup);
    public static readonly IconKind RunePrisons = new("runeprisons", S.RunePrisons, new[] { "Rune Prison Visuals" }, IconsGroup);
    public static readonly IconKind Caches = new("caches", S.Caches, new[] { "One Shot Cache" }, IconsGroup);
    public static readonly IconKind TombChests = new("tombchests", S.TombChests, new[] { "TOMB enhanced treasure chest", "Tomb Reward Chest", "CAVE enhanced treasure chest", "CAVE dragonChest" }, IconsGroup);
    public static readonly IconKind TombEntrances = new("tombentrances", S.TombEntrances, new[] { "Tomb Entrance Visuals" }, IconsGroup);
    public static readonly IconKind OmenWindows = new("omenwindows", S.OmenWindows, new[] { "ObserverWindowEncounter" }, OmenGroup);
    public static readonly IconKind Nemesis = new("nemesis", S.Nemesis, new[] { "Nemesis Soldier In Scene Visuals" }, IconsGroup);
    public static readonly IconKind Forges = new("forges", S.Forges, new[] { "Client_Forge" }, IconsGroup);

    public static readonly IconKind[] Kinds = { Shrines, RunePrisons, Caches, TombChests, TombEntrances, OmenWindows, Nemesis, Forges };

    // A hidden icon: its kind, its own object, and the trigger that switches it on near the player.
    private readonly record struct HiddenIcon(IconKind Kind, GameObject Icon, ConditionHandler? Trigger);

    private sealed class IconState
    {
        public readonly List<HiddenIcon> Hidden = new();
        public readonly HashSet<IntPtr> SeenRoots = new();
        // Roots of an offered kind whose icon wasn't ready (its trigger's parent not active yet; online the listener
        // comes first): looked at again each scan until it is.
        public readonly List<Transform> Pending = new();
        public readonly Dictionary<IconKind, Edit<bool>> Edits = new();
        public int ListenerCount = -1, OmenCount = -1;
        public float NextScan;
    }

    // The icon objects Apply switched on, with their triggers, so Restore switches off only those. Kept outside the run:
    // the host removes a feature's edits after its Shutdown has dropped the run.
    private static readonly Dictionary<IntPtr, ConditionHandler?> s_switchedOn = new();

    // One target per kind, each with its own name: the registry keeps one stack per name, with that name's Find.
    private static readonly Dictionary<IconKind, EditTarget<bool>> s_targets = Kinds.ToDictionary(kind => kind, MakeTarget);

    private static EditTarget<bool> MakeTarget(IconKind kind) => new()
    {
        Name = $"map: icon shown ({kind.Id})",
        Find = () => IconsOf(kind),
        Apply = (o, _) =>
        {
            var go = o.Cast<GameObject>();
            if (go.activeSelf || s_run is not { } run) return;
            go.SetActive(true);
            s_switchedOn[go.Pointer] = TriggerOf(run, go);
        },
        // Off again only if we switched it on and the game's own trigger hasn't fired since: then the player has been
        // near, and the game would show it anyway.
        Restore = (o, _) =>
        {
            var go = o.Cast<GameObject>();
            if (!s_switchedOn.Remove(go.Pointer, out var trigger) || !go.activeSelf) return;
            if (Game.Alive(trigger) && trigger!.triggered) return;
            go.SetActive(false);
        },
    };

    private static IEnumerable<UnityEngine.Object> IconsOf(IconKind kind)
    {
        if (s_run is not { } run) yield break;
        foreach (var hidden in run.Icons.Hidden)
        {
            if (hidden.Kind == kind && Game.Alive(hidden.Icon)) yield return hidden.Icon;
        }
    }

    private static ConditionHandler? TriggerOf(Run run, GameObject icon)
    {
        foreach (var hidden in run.Icons.Hidden)
        {
            if (hidden.Icon.Pointer == icon.Pointer) return hidden.Trigger;
        }
        return null;
    }

    /// <summary>Whether a kind's game members are there.</summary>
    public static bool Available(IconKind kind) => s_run?.Feature.Has(kind.Group) == true;

    // On a new zone map: the edits go (the registry restores what they switched on) and the list starts again.
    private static void ClearIcons(Run run)
    {
        foreach (var edit in run.Icons.Edits.Values) edit.Remove();
        run.Icons.Edits.Clear();
        run.Icons.Hidden.Clear();
        run.Icons.SeenRoots.Clear();
        run.Icons.Pending.Clear();
        // The removed edits restored what they could; what is left belonged to the old zone's objects.
        s_switchedOn.Clear();
        run.Icons.ListenerCount = -1;
        run.Icons.OmenCount = -1;
        run.Icons.NextScan = 0f;
    }

    private static void TickIcons(Run run, bool on)
    {
        var icons = run.Icons;
        if (on && Clock.Now >= icons.NextScan) Scan(run);
        foreach (var kind in Kinds)
        {
            bool want = on && kind.Toggle.Value && Available(kind);
            bool has = icons.Edits.TryGetValue(kind, out var edit) && !edit.Removed;
            if (want && !has) icons.Edits[kind] = Edits.Apply(run.Feature, s_targets[kind], true);
            else if (!want && has)
            {
                edit!.Remove();
                icons.Edits.Remove(kind);
            }
        }
    }

    // New listeners or omen windows since the last look: each one's spawned root is looked at once for hidden icons.
    private static void Scan(Run run)
    {
        var icons = run.Icons;
        icons.NextScan = Clock.Now + IconScanSeconds;
        for (int i = icons.Pending.Count - 1; i >= 0; i--)
        {
            var root = icons.Pending[i];
            if (!Game.Alive(root) || LookInto(run, root)) icons.Pending.RemoveAt(i);
        }
        if (run.Feature.Has(IconsGroup))
        {
            var all = WorldObjectClickListener.allListener;
            if (all != null && all.Count != icons.ListenerCount)
            {
                icons.ListenerCount = all.Count;
                for (int i = 0; i < all.Count; i++)
                {
                    var listener = all[i];
                    if (Game.Alive(listener)) LookAt(run, listener.transform);
                }
            }
        }
        if (run.Feature.Has(OmenGroup))
        {
            var omens = ObserverWindowVisuals.allVisuals;
            if (omens != null && omens.Count != icons.OmenCount)
            {
                icons.OmenCount = omens.Count;
                for (int i = 0; i < omens.Count; i++)
                {
                    var omen = omens[i];
                    if (Game.Alive(omen)) LookAt(run, omen.transform);
                }
            }
        }
    }

    private static void LookAt(Run run, Transform from)
    {
        var root = from;
        for (var t = from; Game.Alive(t); t = t!.parent)
        {
            if (t!.gameObject.name.Contains("(Clone)")) root = t;
        }
        if (!run.Icons.SeenRoots.Add(root.Pointer)) return;
        if (!LookInto(run, root)) run.Icons.Pending.Add(root);
    }

    // True when the root is settled: not an offered kind, or its icons are found (hidden ones listed) or already shown.
    private static bool LookInto(Run run, Transform root)
    {
        if (KindOf(root.gameObject.name) is not { } kind) return true;
        int before = run.Icons.Hidden.Count;
        bool settled = AddHidden(run, kind, root.GetComponentsInChildren(Il2CppType.Of<Il2CppDMM.BaseDMMapIcon>(), true));
        settled |= AddHidden(run, kind, root.GetComponentsInChildren(Il2CppType.Of<DMMapWorldIcon>(), true));
        return settled || run.Icons.Hidden.Count > before;
    }

    private static IconKind? KindOf(string name)
    {
        foreach (var kind in Kinds)
        {
            foreach (string part in kind.Names)
            {
                if (name.Contains(part, StringComparison.Ordinal)) return kind;
            }
        }
        return null;
    }

    // Only an icon whose own object is the one switched off, inside an active parent: that is the game's near trigger.
    // True when an icon here is already shown by the game (nothing to do, settled).
    private static bool AddHidden(Run run, IconKind kind, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Component> found)
    {
        bool shown = false;
        foreach (var component in found)
        {
            if (!Game.Alive(component)) continue;
            var go = component.gameObject;
            if (go.activeInHierarchy) shown = true;
            var parent = go.transform.parent;
            if (go.activeSelf || !Game.Alive(parent) || !parent!.gameObject.activeInHierarchy) continue;
            var trigger = parent.GetComponent<ConditionHandler>();
            run.Icons.Hidden.Add(new HiddenIcon(kind, go, Game.Alive(trigger) ? trigger : null));
        }
        return shown;
    }

    // Icons of enabled kinds now shown: on in the scene, whether by us or by the game.
    private static int ShownIcons(Run run)
    {
        int n = 0;
        foreach (var hidden in run.Icons.Hidden)
        {
            if (hidden.Kind.Toggle.Value && Game.Alive(hidden.Icon) && hidden.Icon.activeInHierarchy) n++;
        }
        return n;
    }

    private static IEnumerable<string> DescribeIcons(Run run)
    {
        var icons = run.Icons;
        yield return $"icons: {icons.Hidden.Count} hidden found from {icons.SeenRoots.Count} roots ({icons.ListenerCount} listeners, {icons.OmenCount} omen windows), {ShownIcons(run)} shown, {s_switchedOn.Count} switched on by us";
        foreach (var kind in Kinds)
        {
            int found = icons.Hidden.Count(h => h.Kind == kind);
            int shown = icons.Hidden.Count(h => h.Kind == kind && Game.Alive(h.Icon) && h.Icon.activeInHierarchy);
            yield return $"  {kind.Id,-14} {(kind.Toggle.Value ? "on " : "off")} {(Available(kind) ? string.Empty : "unavailable ")}found {found}, shown {shown}";
        }
    }
}
