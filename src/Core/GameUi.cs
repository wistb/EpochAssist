using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

/// <summary>An item as a tooltip line provider sees it: its names, rarity and affixes (with tiers and rolls where the data has them).</summary>
internal readonly record struct TooltipItem(string? Name, string? BaseName, int Rarity, IReadOnlyList<DataEntry> Affixes);

/// <summary>
/// Extra lines in the game's own item tooltip, in its lore text style, below its content: the main tooltip and the
/// comparison one, for inventory, stash and ground items. The patch is on only while a provider exists, and a
/// provider goes with its owner. Postfixes only, so other mods' tooltip patches keep working.
/// </summary>
internal static class TooltipLines
{
    // Our block in the text, so a rebuilt tooltip never shows lines twice or lines of a stopped owner.
    private const string BlockStart = "<link=\"epochassist\">";
    private const string BlockEnd = "</link>";

    private static readonly List<(Feature Owner, Func<TooltipItem, IEnumerable<string>?> Lines)> s_providers = new();
    // The item each tooltip was last set to: UpdateInfo, which sets the lore text, runs after SetAs…Tooltip returns.
    private static readonly Dictionary<IntPtr, ItemDataUnpacked> s_items = new();

    public static int Count => s_providers.Count;

    /// <summary>Adds a provider: for each item tooltip it is asked for lines (none or null adds nothing).</summary>
    public static void Add(Feature owner, Func<TooltipItem, IEnumerable<string>?> lines)
    {
        s_providers.Add((owner, lines));
        Patch();
    }

    /// <summary>Removes the owner's providers.</summary>
    public static void Remove(Feature owner)
    {
        if (s_providers.RemoveAll(p => p.Owner == owner) > 0) Patch();
    }

    /// <summary>The host, with Edits.RemoveOwner (stop, fault, unavailable).</summary>
    internal static void RemoveOwner(Feature owner) => Remove(owner);

    internal static void Reset()
    {
        s_providers.Clear();
        s_items.Clear();
    }

    private static void Patch()
    {
        if (FeatureHost.Find("gameui") is { State: FeatureState.Running } feature) FeatureHost.SetPatchGroup(feature, GameUiFeature.TooltipGroup, s_providers.Count > 0);
        if (s_providers.Count == 0) s_items.Clear();
    }

    /// <summary>The group's patch is live (the feature runs and a provider exists).</summary>
    public static bool Patched => FeatureHost.Find("gameui") is { } feature && feature.DemandGroupsOn.Contains(GameUiFeature.TooltipGroup);

    private static TooltipItem Describe(ItemDataUnpacked item)
    {
        var names = GameData.Item(item);
        var affixes = new List<DataEntry>();
        float modifier = GameData.AffixModifier(item) ?? 0f;
        var list = item.affixes;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            var affix = list[i];
            affixes.Add(GameData.Affix(affix.affixId, affix.affixTier, affix.affixRoll, modifier));
        }
        return new TooltipItem(names.Name ?? item.FullName, names.BaseName, item.rarity, affixes);
    }

    // Each provider in its owner's guard: a throw adds nothing and counts against that owner.
    private static string? Block(ItemDataUnpacked? item)
    {
        if (item == null || !Game.Alive(item) || s_providers.Count == 0) return null;
        var described = Describe(item);
        var lines = new List<string>();
        foreach (var (owner, provider) in s_providers.ToList())
        {
            if (owner.State != FeatureState.Running) continue;
            FeatureHost.Guarded(owner, "tooltip lines", () =>
            {
                if (provider(described) is { } more) lines.AddRange(more.Where(l => !string.IsNullOrEmpty(l)));
            });
        }
        return lines.Count == 0 ? null : BlockStart + string.Join("\n", lines) + BlockEnd;
    }

    private static void Append(Il2CppTMPro.TextMeshProUGUI? text, ItemDataUnpacked? item)
    {
        if (text == null || !Game.Alive(text)) return;
        string current = Strip(text.text ?? string.Empty);
        string? block = Block(item);
        string next = block == null ? current : current + (current.Length > 0 ? "\n" : string.Empty) + block;
        if (next != text.text) text.text = next;
        if (block != null && !text.gameObject.activeSelf) text.gameObject.SetActive(true);
    }

    private static string Strip(string text)
    {
        int start = text.IndexOf(BlockStart, StringComparison.Ordinal);
        if (start < 0) return text;
        int cut = start > 0 && text[start - 1] == '\n' ? start - 1 : start;
        return text.Substring(0, cut);
    }

    private static void Guard(string phase, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"gameui:{phase}", $"Tooltip lines ({phase}) failed: {e.Message}", 30);
        }
    }

    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.SetAsItemTooltip))]
    private static class SetAsItemTooltip_Patch
    {
        private static void Postfix(UITooltipItem __instance, ItemDataUnpacked __3) => Guard("item", () => s_items[__instance.Pointer] = __3);
    }

    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.SetAsGroundTooltip))]
    private static class SetAsGroundTooltip_Patch
    {
        private static void Postfix(UITooltipItem __instance, ItemDataUnpacked __3) => Guard("ground", () => s_items[__instance.Pointer] = __3);
    }

    // Other tooltip kinds (affix shards, blessings) get no lines.
    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.SetAsAffixTooltip))]
    private static class SetAsAffixTooltip_Patch
    {
        private static void Postfix(UITooltipItem __instance) => Guard("affix", () => s_items.Remove(__instance.Pointer));
    }

    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.UpdateInfo))]
    private static class UpdateInfo_Patch
    {
        private static void Postfix(UITooltipItem __instance) =>
            Guard("main", () => Append(__instance.loreText, s_items.TryGetValue(__instance.Pointer, out var item) ? item : null));
    }

    [HarmonyPatch(typeof(UITooltipItem), nameof(UITooltipItem.ExpandTooltip))]
    private static class ExpandTooltip_Patch
    {
        private static void Postfix(UITooltipItem __instance, ItemDataUnpacked __3) => Guard("compare", () => Append(__instance.compareLoreText, __3));
    }
}

/// <summary>The game's screen UI hidden and shown again, as an edit on its root canvases.</summary>
internal static class GameUi
{
    // Left visible: what the player must always see (loading, errors, disconnects, the on-screen keyboard, the item on
    // the cursor).
    private static readonly string[] s_kept =
    {
        "Canvas (loading screen)", "Canvas (Disconnected Prompt)", "Canvas (Error Prompt)", "Canvas (Bug Report)",
        "Canvas (Screen Keyboard)", "Draw Over Login Canvas",
    };
    // FloatingBarksCanvas (NPC barks) is left out: the game enables it again on every load.
    private static readonly string[] s_extraRoots = { "PlayerInteractionMarker" };

    /// <summary>The game UI's root canvases, enabled true while shown; restored canvas by canvas.</summary>
    public static readonly EditTarget<bool> Target = new()
    {
        Name = "game ui",
        Find = RootCanvases,
        Read = o => o.Cast<Canvas>().enabled,
        Write = (o, v) => o.Cast<Canvas>().enabled = v,
        Show = v => v ? "shown" : "hidden",
        Scene = false,
    };

    /// <summary>Hides the game's screen UI until the edit is removed (or its owner stops); null when the part is missing.</summary>
    public static Edit<bool>? Hide(Feature owner) =>
        FeatureHost.Find("gameui") is { State: FeatureState.Running } f && f.Has(GameUiFeature.HideGroup) ? Edits.Apply(owner, Target, false) : null;

    // Every enabled canvas under the GUI object's root canvases (nested ones too: Unity draws a nested canvas while its
    // parent canvas is disabled), and under two other overlays, but the kept roots. Found from the game's UI root and
    // its scene's root objects, not by an object search, which walks every object.
    private static IEnumerable<UnityEngine.Object> RootCanvases()
    {
        var gui = Game.UI?.transform.root;
        if (gui == null || gui.name != "GUI") yield break;
        for (int i = 0; i < gui.childCount; i++)
        {
            var child = gui.GetChild(i);
            if (Array.IndexOf(s_kept, child.name) >= 0 || !child.gameObject.activeInHierarchy) continue;
            foreach (var canvas in Enabled(child)) yield return canvas;
        }
        foreach (var root in gui.gameObject.scene.GetRootGameObjects())
        {
            if (Array.IndexOf(s_extraRoots, root.name) >= 0 && root.activeInHierarchy)
            {
                foreach (var canvas in Enabled(root.transform)) yield return canvas;
            }
        }
    }

    private static IEnumerable<Canvas> Enabled(Transform root)
    {
        foreach (var canvas in root.GetComponentsInChildren<Canvas>(false))
        {
            if (Game.Alive(canvas) && canvas.enabled && canvas.rootCanvas is { } top && top.renderMode != RenderMode.WorldSpace) yield return canvas;
        }
    }
}

/// <summary>A game prompt kind: its panel type and title (the game's English text, matched exactly), and whether it may
/// ever be confirmed.</summary>
internal sealed record PromptKind(string Id, string Panel, string Title, bool Confirmable);

/// <summary>
/// The game prompts the mod knows. Confirm presses Yes only on a known, confirmable kind the request names;
/// prompts that can't be undone are listed as never confirmable. A feature adds confirmable kinds with the feature that
/// needs them, each checked in game.
/// </summary>
internal static class PromptKinds
{
    public const string ConfirmationPanel = "ConfirmationModal";

    private static readonly List<PromptKind> s_all = new()
    {
        new("exit-to-login", ConfirmationPanel, "Exit to Login", false),
        new("exit-to-desktop", ConfirmationPanel, "Are you sure you want to logout?", false),
#if DEV
        new("dev-test", ConfirmationPanel, DevTestTitle, true),
#endif
    };

#if DEV
    public const string DevTestTitle = "Epoch Assist dev test";
#endif

    public static IReadOnlyList<PromptKind> All => s_all;

    /// <summary>The open prompt's modal, or null with none open.</summary>
    internal static Il2CppLE.UI.PanelSystem.ModalTemplate? OpenModal()
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppLE.UI.PanelSystem.ModalTemplate>()))
        {
            var modal = obj.TryCast<Il2CppLE.UI.PanelSystem.ModalTemplate>();
            if (Game.Alive(modal) && modal!.gameObject.activeInHierarchy) return modal;
        }
        return null;
    }

    /// <summary>The open prompt's kind, an "unknown" kind for one not on the list, or null with none open.</summary>
    public static PromptKind? Open => OpenModal() is { } modal ? KindOf(modal) : null;

    internal static PromptKind KindOf(Il2CppLE.UI.PanelSystem.ModalTemplate modal)
    {
        string title = Game.Alive(modal._title) ? modal._title.text ?? string.Empty : string.Empty;
        string panel = PanelOf(modal);
        return s_all.Find(k => k.Panel == panel && k.Title == title) ?? new PromptKind("unknown", panel, title, false);
    }

    // The modal panel's type is the component next to the template ("ConfirmationModal", "WarningConfirmationModal", …).
    private static string PanelOf(Il2CppLE.UI.PanelSystem.ModalTemplate modal)
    {
        foreach (var component in modal.GetComponents<Component>())
        {
            string name = component.GetIl2CppType().Name;
            if (name.EndsWith("Modal", StringComparison.Ordinal)) return name;
        }
        return "unknown";
    }
}

/// <summary>
/// The core game UI layer: tooltip lines and hiding the game UI. UI presses are the dispatcher's; the prompt
/// kinds they check are here.
/// </summary>
internal sealed class GameUiFeature : Feature
{
    public const string TooltipGroup = "gameui: tooltip lines";
    public const string HideGroup = "gameui: hide";

    public override string Id => "gameui";
    public override string Name => "Game UI";
    public override string[] Needs => new[] { "game" };
    public override Type[] PatchClasses => NestedPatches(typeof(TooltipLines));
    public override IReadOnlyDictionary<Type, string> PatchGroups => PatchClasses.ToDictionary(t => t, _ => TooltipGroup);
    public override string[] DemandGroups => new[] { TooltipGroup };

    public override Requirement[] Requirements => new[]
    {
        Need<UITooltipItem>(nameof(UITooltipItem.loreText), TooltipGroup),
        Need<UITooltipItem>(nameof(UITooltipItem.compareLoreText), TooltipGroup),
        Need<ItemDataUnpacked>(nameof(ItemDataUnpacked.affixes), TooltipGroup),
        Need<UIBase>(nameof(UIBase.instance), HideGroup),
        Need<Canvas>(nameof(Canvas.rootCanvas), HideGroup),
        Need<Canvas>(nameof(Canvas.enabled), HideGroup),
    };

    // Providers registered before this started (it restarted) get the patch back.
    public override void Start()
    {
        if (TooltipLines.Count > 0) FeatureHost.SetPatchGroup(this, TooltipGroup, true);
    }

    public override IEnumerable<string> DiagnosticNotes
    {
        get
        {
            yield return $"tooltip providers {TooltipLines.Count}, patch {(TooltipLines.Patched ? "on" : "off")}";
        }
    }
}
