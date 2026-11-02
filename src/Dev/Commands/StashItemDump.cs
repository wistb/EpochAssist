using EpochAssist.Core;
using EpochAssist.Features;
using Il2Cpp;

namespace EpochAssist.Dev;

// stash item <tab> [x y] | hovered: an item's raw fields as the game holds them (affix ids, tiers, rolls, kinds, sealed
// type, implicit and unique rolls, FP, WT, item level, requirements, id bytes) and, per number, the game's own value at
// the roll, at roll 0 and at roll 255 with the property's display flags, beside the line the game's tooltip formatter
// writes for it. A tab alone lists its items with their cells. Only reads.
internal static class StashItemDump
{
    public static CommandResult Run(CommandCall call)
    {
        if (call.Lower(2) == "hovered")
        {
            var tips = Planner.Panels.Find<TooltipItemManager>();
            if (!Game.Alive(tips) || tips!.ActiveItem is not { } shown) return CommandResult.Ok("stash item: no item tooltip shown");
            return new CommandResult(DevOutput.Reply("stash-item", Dump(shown, "hovered")));
        }
        if (call.Lower(2) == "time")
        {
            // Every stash item dumped once, timed: an upper bound for a facts read (the dump also formats text).
            var all = Containers();
            int count = 0;
            double max = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int c = 0; all != null && c < all.Count; c++)
            {
                var items = all[c]?.content;
                for (int k = 0; items != null && k < items.Count; k++)
                    if (items[k]?.data?.getAsUnpacked() is { } d)
                    {
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        Dump(d, string.Empty);
                        max = Math.Max(max, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000d / System.Diagnostics.Stopwatch.Frequency);
                        count++;
                    }
            }
            return CommandResult.Ok($"stash item time: {count} items in {sw.Elapsed.TotalMilliseconds:0.00} ms, {sw.Elapsed.TotalMilliseconds / Math.Max(1, count):0.000} ms an item, slowest {max:0.000} ms");
        }
        if (!int.TryParse(call.Lower(2), out int tab)) return CommandResult.Error("stash item <tab> [x y] | hovered");
        var containers = Containers();
        if (containers == null || tab < 0 || tab >= containers.Count) return CommandResult.Error($"stash item: no tab {tab}");
        var content = containers[tab]?.content;
        if (content == null) return CommandResult.Ok($"stash item: tab {tab} has no content list");
        if (!int.TryParse(call.Lower(3), out int x) || !int.TryParse(call.Lower(4), out int y))
        {
            var lines = new List<string> { $"ok stash item: tab {tab}, {content.Count} items" };
            for (int k = 0; k < content.Count; k++)
                if (content[k] is { } e && e.data?.getAsUnpacked() is { } d)
                    lines.Add($"  ({e.Position.x},{e.Position.y}) {e.size.x}x{e.size.y} type {d.itemType} sub {d.subType} rarity {d.rarity} \"{d.FullName}\"");
            return new CommandResult(DevOutput.Reply("stash-item", lines));
        }
        for (int k = 0; k < content.Count; k++)
            if (content[k] is { } e && e.Position.x == x && e.Position.y == y && e.data?.getAsUnpacked() is { } d)
                return new CommandResult(DevOutput.Reply("stash-item", Dump(d, $"tab {tab} ({x},{y}) {e.size.x}x{e.size.y} entry 0x{e.Pointer.ToInt64():x}")));
        return CommandResult.Ok($"stash item: nothing at ({x},{y}) in tab {tab}");
    }

    /// <summary>stash affixes &lt;property&gt;: every affix with a property of that name (any case, part of the name), with
    /// its kind, type and each property's modifier type and tags; for the pseudo totals table.</summary>
    public static CommandResult Affixes(CommandCall call)
    {
        string want = call.Lower(2);
        if (want.Length == 0) return CommandResult.Error("stash affixes <property name part>");
        var list = AffixList.get();
        var lines = new List<string>();
        void Add(AffixList.Affix a, IEnumerable<(SP P, BaseStats.ModType M, AT T, byte St)> props)
        {
            var ps = props.ToList();
            if (!ps.Any(p => p.P.ToString().ToLowerInvariant().Contains(want))) return;
            lines.Add($"  {a.affixId} \"{a.getAffixDisplayName()}\" {a.type} {a.specialAffixType} rollsOn {a.rollsOn}: "
                + string.Join("; ", ps.Select(p => $"{p.P} {p.M} [{p.T}] st {p.St}")));
        }
        foreach (var a in list.singleAffixes) if (a != null) Add(a, new[] { (a.property, a.modifierType, a.tags, a.specialTag) });
        foreach (var a in list.multiAffixes)
            if (a?.affixProperties != null)
            {
                var ps = new List<(SP, BaseStats.ModType, AT, byte)>();
                for (int i = 0; i < a.affixProperties.Count; i++) { var p = a.affixProperties[i]; ps.Add((p.property, p.modifierType, p.tags, p.specialTag)); }
                Add(a, ps);
            }
        lines.Insert(0, $"ok stash affixes: {lines.Count} with \"{want}\"");
        return new CommandResult(DevOutput.Reply("stash-affixes", lines));
    }

    /// <summary>stash press tab|folder &lt;id&gt; [onclick|pointer] [vert|row]: presses a stash window button from code, the
    /// way a search's go-to would, and reports the shown tab before and after. Only switches what the window shows.</summary>
    public static CommandResult Press(CommandCall call)
    {
        bool folder = call.Lower(2) == "folder";
        if (!int.TryParse(call.Lower(3), out int id)) return CommandResult.Error("stash press tab|folder <id> [onclick|pointer] [vert|row]");
        string how = call.Lower(4) == "pointer" ? "pointer" : "onclick";
        bool vert = call.Lower(5) != "row";
        var ui = Planner.Panels.Find<StashTabbedUIControls>();
        if (!Game.Alive(ui)) return CommandResult.Error("stash press: no stash window");
        TabbedUITabWithIcon? target = null;
        if (folder)
        {
            var pairs = vert ? ui!.localVertCategories : ui!.localCategories;
            for (int i = 0; pairs != null && i < pairs.Count; i++)
                if (pairs[i]?.category?.CategoryId == id) target = pairs[i].tabUI;
        }
        else
        {
            var pairs = vert ? ui!.localVertTabs : ui!.localTabs;
            for (int i = 0; pairs != null && i < pairs.Count; i++)
                if (pairs[i]?.tab?.TabId == id) target = pairs[i].tabUI;
        }
        if (!Game.Alive(target)) return CommandResult.Error($"stash press: no {(vert ? "quick view" : "row")} {(folder ? "folder" : "tab")} {id}");
        int before = ActiveTab();
        string path = target!.gameObject.name + " active " + target.gameObject.activeInHierarchy + " ID " + target.ID + " vertTab " + target.vertTab;
        if (how == "onclick")
        {
            if (!Game.Alive(target.button)) return CommandResult.Error("stash press: the tab has no Button");
            target.button.onClick.Invoke();
        }
        else
        {
            var data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left };
            target.OnPointerClick(data);
        }
        return CommandResult.Ok($"stash press: {(folder ? "folder" : "tab")} {id} by {how} ({path}); active tab {before} → {ActiveTab()} (re-read next frame with stash ui)");
    }

    /// <summary>stash box: both game search fields' text and whether they show, and the search the grid last ran.</summary>
    public static CommandResult Box(CommandCall call)
    {
        var ui = Planner.Panels.Find<StashTabbedUIControls>();
        if (!Game.Alive(ui)) return CommandResult.Ok("stash box: no stash window");
        if (call.Lower(2) == "set") ui!.SetStashWideSearch(call.Rest(3), true);
        else if (call.Lower(2) == "clear")
        {
            ui!.ClearSearchField();
            if (Game.Alive(ui.primarySearchBar)) ui.primarySearchBar.text = string.Empty;
            if (Game.Alive(ui.verticalSearchBar)) ui.verticalSearchBar.text = string.Empty;
        }
        else if (call.Lower(2) == "text") ui!.verticalSearchBar.text = call.Rest(3);
        string Field(Il2CppTMPro.TMP_InputField? f) => Game.Alive(f) ? $"\"{f!.text}\" shown {f.gameObject.activeInHierarchy} focused {f.isFocused}" : "none";
        string last = Game.Alive(ui!.searchUI) ? ui.searchUI.lastSearch ?? "(null)" : "no search UI";
        return CommandResult.Ok($"stash box: quick view {Field(ui.verticalSearchBar)}; grid {Field(ui.primarySearchBar)}; grid last search \"{last}\"; clear when closed {ui._clearSearchWhenClosed}");
    }

    /// <summary>stash tree quickview|bottom|&lt;object name&gt;: the stash window's Quick View or its bottom bar as objects, with each
    /// one's rect, images (sprite, type, colour) and texts (font, size, style, colour). Only reads.</summary>
    public static CommandResult Tree(CommandCall call)
    {
        var ui = Planner.Panels.Find<StashTabbedUIControls>();
        if (!Game.Alive(ui)) return CommandResult.Ok("stash tree: no stash window");
        string what = call.Rest(2).Trim();
        UnityEngine.Transform? root = what == "bottom" ? ui!.primarySearchBar?.transform.parent?.parent
            : what == "quickview" ? ui!.verticalTabWrapper?.transform
            : ui!.transform.root.GetComponentsInChildren<UnityEngine.Transform>(true).FirstOrDefault(t => t.name == what)
                ?? UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.RectTransform>(), true)
                    .Select(o => o.TryCast<UnityEngine.RectTransform>()?.transform).FirstOrDefault(t => t != null && t.name == what);
        if (root == null) return CommandResult.Ok("stash tree: not found");
        var lines = new List<string> { $"ok stash tree: {root.name}" };
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.Vector3>(4);
        void Walk(UnityEngine.Transform t, int depth)
        {
            if (depth > 4 || lines.Count > 160) return;
            string rect = string.Empty;
            if (t.TryCast<UnityEngine.RectTransform>() is { } r)
            {
                r.GetWorldCorners(corners);
                rect = $" [{corners[0].x:0},{UnityEngine.Screen.height - corners[2].y:0} {corners[2].x - corners[0].x:0}x{corners[2].y - corners[0].y:0}] pivot {r.pivot} amin {r.anchorMin} amax {r.anchorMax} size {r.sizeDelta}";
            }
            var parts = new List<string>();
            foreach (var img in t.GetComponents<UnityEngine.UI.Image>())
                parts.Add($"image {(Game.Alive(img.sprite) ? img.sprite.name : "-")} {img.type} {img.color} on {img.enabled} mat {(Game.Alive(img.material) ? img.material.name : "-")} alpha {img.canvasRenderer?.GetAlpha()}");
            foreach (var tx in t.GetComponents<Il2CppTMPro.TextMeshProUGUI>())
                parts.Add($"text \"{tx.text}\" {(Game.Alive(tx.font) ? tx.font.name : "-")} {tx.fontSize} {tx.fontStyle} {tx.color} cs {tx.characterSpacing} align {tx.alignment}");
            if (t.GetComponent<UnityEngine.UI.Button>() != null) parts.Add("button");
            if (t.GetComponent<UnityEngine.UI.LayoutGroup>() is { } lg) parts.Add("layout " + lg.GetIl2CppType().Name);
            lines.Add($"{new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (off)")}{rect} {string.Join("; ", parts)}");
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
        }
        Walk(root, 0);
        return CommandResult.Ok(string.Join("\n", lines));
    }

    /// <summary>stash syntax: what the game's own search box understands, read from its types by reflection: the
    /// macro kinds and comparisons, its patterns, and the members of its expression, macro and regex classes.</summary>
    public static CommandResult Syntax(CommandCall call)
    {
        if (call.Lower(2) == "read")
        {
            string text = call.Rest(3);
            var node = StashSearch.ParseBox(text.TrimStart('~'), out string? error);
            return node == null ? CommandResult.Error($"stash syntax read: {error}") : CommandResult.Ok($"stash syntax read: {node.Show()}");
        }
        if (call.Lower(2) == "find")
        {
            string want = call.Rest(3).Trim();
            var found = new List<string> { $"ok stash syntax find {want}:" };
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Il2Cpp") == true))
            {
                Type[] types;
                try { types = a.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                        if ((want.StartsWith("*") ? m.Name.Contains(want[1..], StringComparison.OrdinalIgnoreCase) : m.Name.Equals(want, StringComparison.OrdinalIgnoreCase)) && found.Count < 60) found.Add($"  {t.FullName}.{m.Name} ({m.MemberType}) {m}");
            }
            return CommandResult.Ok(string.Join("\n", found));
        }
        if (call.Lower(2) == "test")
        {
            // stash syntax test <tab> <text>: the game's own reading of a search text on every item of a tab, with the
            // game's prefix and suffix counts.
            if (!int.TryParse(call.Lower(3), out int tab)) return CommandResult.Error("stash syntax test <tab> <text>");
            string text = string.Join(" ", call.Args.Skip(3));
            var content = Containers()?[tab]?.content;
            if (!ItemSearchExpression.TryGet(text, out var expr) || expr == null) return CommandResult.Error($"stash syntax test: the game can't read \"{text}\"");
            var lines3 = new List<string> { $"ok stash syntax test \"{text}\" on tab {tab}:" };
            for (int k = 0; content != null && k < content.Count; k++)
            {
                if (content[k]?.data?.getAsUnpacked() is not { } d) continue;
                d.GetNonSealedPrefixAndSuffixCount(out int pre, out int suf);
                lines3.Add($"  ({content[k].Position.x},{content[k].Position.y}) {d.FullName} rarity {d.rarity}: prefixes {pre} suffixes {suf} → game {expr.ItemMatches(d, string.Empty)}");
            }
            return CommandResult.Ok(string.Join("\n", lines3));
        }
        if (call.Lower(2) == "last") return CommandResult.Ok("stash syntax last: " + StashSearch.BoxState() + "\n  " + StashSearch.BoxRunState() + "\n  matched: " + string.Join("; ", StashSearch.BoxMatchedNames.OrderBy(n => n)));
        if (call.Lower(2) == "try")
        {
            var lines2 = new List<string> { "ok stash syntax try:" };
            foreach (string text in call.Rest(3).Split(" ;; "))
            {
                ItemSearchExpression? e = null;
                bool ok = ItemSearchExpression.TryGet(text, out e);
                lines2.Add($"  \"{text}\" -> {ok}, expression {(e == null ? "null" : "set")}");
            }
            return CommandResult.Ok(string.Join("\n", lines2));
        }
        var lines = new List<string> { "ok stash syntax:" };
        var asm = typeof(StashTabbedUIControls).Assembly;
        foreach (string name in new[] { "ItemSearchMacroType", "ItemSearchMacroComparisonType" })
            if (asm.GetTypes().FirstOrDefault(t => t.Name == name && t.IsEnum) is { } e)
                lines.Add($"{name}: " + string.Join(", ", Enum.GetNames(e).Select(n => $"{n}={Convert.ToInt64(Enum.Parse(e, n))}")));
        string[] names = call.Lower(2) == "members" ? new[] { call.Rest(3).Trim() } : new[] { "ItemSearchExpression", "ItemSearchMacro", "ItemSearchRegex", "ItemSearchAffixRegex" };
        foreach (string name in names)
        {
            var t = asm.GetTypes().FirstOrDefault(x => x.Name == name)
                ?? AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Il2Cpp") == true)
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } }).FirstOrDefault(x => x.Name == name);
            if (t == null) { lines.Add($"{name}: not found"); continue; }
            lines.Add($"{name}:");
            foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (m.Name.StartsWith("NativeMethodInfoPtr") || m.Name.StartsWith("NativeFieldInfoPtr") || m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                string value = string.Empty;
                try
                {
                    if (m is System.Reflection.PropertyInfo p && p.GetMethod?.IsStatic == true && p.GetIndexParameters().Length == 0)
                    {
                        var v = p.GetValue(null);
                        value = v is Il2CppSystem.Collections.Generic.Dictionary<string, ItemSearchMacroType> d ? DumpDict(d) : " = " + (v is Il2CppSystem.Object o ? o.ToString() : v?.ToString());
                    }
                }
                catch (Exception ex) { value = " ! " + ex.GetType().Name; }
                lines.Add($"  {m.MemberType} {m}{value}");
            }
        }
        return CommandResult.Ok(string.Join("\n", lines));

        static string DumpDict(Il2CppSystem.Collections.Generic.Dictionary<string, ItemSearchMacroType> d)
        {
            var parts = new List<string>();
            foreach (var kv in d) parts.Add($"{kv.Key}->{kv.Value}");
            return " = {" + string.Join(", ", parts) + "}";
        }
    }

    /// <summary>stash tip &lt;tab&gt; &lt;x&gt; &lt;y&gt; [slot] [compare] | close: opens the game's tooltip for a stash item from
    /// code with the Quick View's search field as its opener (a stand-in for a cell on the stash window), the slot type
    /// named (STASH by default), and optionally the comparison with the equipped item of its slot; prompts hidden.</summary>
    public static CommandResult Tip(CommandCall call)
    {
        var tips = Planner.Panels.Find<TooltipItemManager>();
        if (!Game.Alive(tips)) return CommandResult.Error("stash tip: no tooltip manager");
        if (call.Lower(2) == "close")
        {
            tips!.CloseTooltip();
            if (UITooltipItem.instance?._itemTooltipPrompts is { } p && Game.Alive(p)) p.gameObject.SetActive(true);
            return CommandResult.Ok("stash tip: closed");
        }
        if (!int.TryParse(call.Lower(2), out int tab) || !int.TryParse(call.Lower(3), out int x) || !int.TryParse(call.Lower(4), out int y))
            return CommandResult.Error("stash tip <tab> <x> <y> [slot] [compare] | close");
        var slot = TooltipItemManager.SlotType.STASH;
        if (call.Args.Length >= 5 && call.Lower(5) != "compare" && !Enum.TryParse(call.Args[4], true, out slot)) return CommandResult.Error($"stash tip: no slot type {call.Args[4]}");
        bool compare = call.Line.ToLowerInvariant().Contains(" compare");
        var content = Containers()?[tab]?.content;
        ItemDataUnpacked? item = null;
        for (int k = 0; content != null && k < content.Count; k++)
            if (content[k] is { } e && e.Position.x == x && e.Position.y == y) item = e.data?.getAsUnpacked();
        if (item == null) return CommandResult.Error($"stash tip: nothing at ({x},{y}) in tab {tab}");
        var ui = Planner.Panels.Find<StashTabbedUIControls>();
        var opener = Game.Alive(ui) && Game.Alive(ui!.verticalSearchBar) ? ui.verticalSearchBar.gameObject : null;
        var rect = opener?.GetComponent<UnityEngine.RectTransform>();
        var size = rect != null ? rect.rect.size : UnityEngine.Vector2.zero;
        var at = opener != null ? opener.transform.position : new UnityEngine.Vector3(UnityEngine.Screen.width / 2f, UnityEngine.Screen.height / 2f, 0f);
        tips!.OpenItemTooltip(item, slot, UnityEngine.Vector2.zero, at, opener, size);
        string comparison = "not asked";
        if (compare && tips.activeParameters is { } shown)
        {
            ItemDataUnpacked? equipped = EquippedFor(item);
            comparison = equipped == null ? "nothing equipped in that slot"
                : tips.SetEquippedItemTextForComparison(equipped, shown) ? "set" : "refused";
            if (comparison == "set") tips.ShowComparison(shown);
        }
        if (UITooltipItem.instance?._itemTooltipPrompts is { } prompts && Game.Alive(prompts)) prompts.gameObject.SetActive(false);
        return CommandResult.Ok($"stash tip: \"{item.FullName}\" as {slot}, opener {(opener != null ? opener.name : "none")}, active item {(tips.ActiveItem?.Pointer == item.Pointer ? "this" : tips.ActiveItem == null ? "none" : "other")}, comparison {comparison}, auto compare {tips.autoCompareEnabled}, comparison shown {tips.IsComparisonEnabled}");
    }

    // The equipped item in the slot the item would go to, through the game's equipment container.
    private static ItemDataUnpacked? EquippedFor(ItemDataUnpacked item)
    {
        var equipment = ItemContainersManager.Instance?.equipment;
        if (equipment == null) return null;
        var content = equipment.TryCast<IItemContainer>()?.GetContent();
        for (int i = 0; content != null && i < content.Count; i++)
            if (content[i]?.data is { } d && d.itemType == item.itemType) return d.getAsUnpacked();
        return null;
    }

    private static int ActiveTab()
    {
        var holder = ItemContainersManager.Instance?.stash;
        var all = holder?.stashItemContainers;
        if (all == null || all.Length == 0) return -1;
        int index = holder!._CurrentStashIndex_k__BackingField;
        var current = index >= 0 && index < all.Length ? all[index] : all[0];
        return current?.currentlyActiveTab ?? -1;
    }

    private static Il2CppSystem.Collections.Generic.List<ItemContainer>? Containers()
    {
        var holder = ItemContainersManager.Instance?.stash;
        var all = holder?.stashItemContainers;
        if (all == null || all.Length == 0) return null;
        int index = holder!._CurrentStashIndex_k__BackingField;
        return (index >= 0 && index < all.Length ? all[index] : all[0])?.containers;
    }

    private static List<string> Dump(ItemDataUnpacked d, string where)
    {
        var lines = new List<string> { $"ok stash item: {where}: \"{d.FullName}\"" };
        void Try(string what, Action act)
        {
            try { act(); }
            catch (Exception e) { lines.Add($"  {what}: error {e.Message}"); }
        }
        Try("fields", () =>
        {
            var id = d.id;
            lines.Add($"  type {d.itemType} sub {d.subType} rarity {d.rarity} unique {d.uniqueID} set {d.SetId} corrupted {d.corrupted} ruined {d.ruined}");
            lines.Add($"  FP {d.forgingPotential} LP {d.legendaryPotential} WW {d.weaversWill} WT {d.weaversTouch} lvlReq {d.LvlReq} classReq {d.classReq} subClassReq {d.subClassReq}");
            lines.Add($"  sealed regular {d.hasSealedRegularAffix} primordial {d.hasSealedPrimordialAffix} corruption {d.hasSealedAffixFromCorruption}; sockets {d.sockets}");
            lines.Add($"  id {(id == null ? "none" : Convert.ToHexString(id.ToArray()))} individual {d.individualID}");
            lines.Add($"  implicitRolls [{Bytes(d.implicitRolls)}] uniqueRolls [{Bytes(d.uniqueRolls)}]");
        });
        float modifier = 0f;
        Try("modifier", () => modifier = ItemList.get().getAffixEffectModifier(d));
        lines.Add($"  affix effect modifier {modifier}");
        var list = AffixList.get();
        var props = PropertyList.get();
        var affixes = d.affixes;
        for (int i = 0; affixes != null && i < affixes.Count; i++)
        {
            var a = affixes[i];
            int n = i;
            Try($"affix {n}", () =>
            {
                var def = list.GetAffix(a.affixId);
                lines.Add($"  affix {n}: id {a.affixId} \"{def?.getAffixDisplayName()}\" tier {a.affixTier} (shown {a.DisplayTier}) roll {a.affixRoll} type {a.affixType} special {a.specialAffixType}"
                    + $" (def {def?.specialAffixType}, def type {def?.type}) sealed {a.sealedAffixType} exalted {a.IsExalted} champion {a.IsChampion} experimental {a.IsExperimental} personal {a.IsPersonal}"
                    + $" enchant {a.IsIdolEnchantment} weaver {a.IsIdolWeaver} setCount {a.affixCountForSetProps}");
                if (def?.TryCast<AffixList.SingleAffix>() is { } single)
                {
                    float v = list.GetSingleAffixValue(single, modifier, a.affixTier, a.affixRoll, 1);
                    float lo = list.GetSingleAffixValue(single, modifier, a.affixTier, byte.MinValue, 1);
                    float hi = list.GetSingleAffixValue(single, modifier, a.affixTier, byte.MaxValue, 1);
                    lines.Add($"    p0 {single.property} {single.modifierType} tags {single.tags} st {single.specialTag}: value {v} min {lo} max {hi} {Info(def.GetPropertyInfo(0), single.modifierType, single.specialTag, v)}");
                }
                else if (def?.TryCast<AffixList.MultiAffix>() is { } multi)
                {
                    for (int p = 0; p < multi.affixProperties.Count; p++)
                    {
                        var ap = multi.affixProperties[p];
                        float v = list.GetMultiAffixPropertyValue(multi, modifier, a.affixTier, a.affixRoll, p, 1);
                        float lo = list.GetMultiAffixPropertyValue(multi, modifier, a.affixTier, byte.MinValue, p, 1);
                        float hi = list.GetMultiAffixPropertyValue(multi, modifier, a.affixTier, byte.MaxValue, p, 1);
                        lines.Add($"    p{p} {ap.property} {ap.modifierType} tags {ap.tags} st {ap.specialTag}: value {v} min {lo} max {hi} {Info(def.GetPropertyInfo(p), ap.modifierType, ap.specialTag, v)}");
                    }
                }
                lines.Add("    text " + Clean(TooltipItemManager.FormatAffix(d, a, TooltipMode.DefaultInfo, TooltipItemManager.SlotType.STASH, false, -1, -1, false, false, ValueRangeFormat.None, false, true)));
            });
        }
        Try("implicits", () =>
        {
            var implicits = ItemList.get().GetItemImplicits(d);
            var rolls = d.implicitRolls;
            for (int i = 0; implicits != null && i < implicits.Count; i++)
            {
                var im = implicits[i];
                byte roll = rolls != null && i < rolls.Length ? rolls[i] : (byte)0;
                float v = im.GetValue(roll), lo = im.GetValue(byte.MinValue), hi = im.GetValue(byte.MaxValue);
                var info = props.GetPropertyInfo(im.property, im.tags, im.specialTag);
                lines.Add($"  implicit {i}: {im.property} {im.type} tags {im.tags} st {im.specialTag} roll {roll}: implicitValue {im.implicitValue} max {im.implicitMaxValue} GetValue(roll) {v} (0) {lo} (255) {hi} {Info(info, im.type, im.specialTag, im.implicitValue)}");
                var b = ItemList.get().GetItemImplicit(d.itemType, d.subType, i);
                if (b != null) lines.Add($"    base: implicitValue {b.implicitValue} max {b.implicitMaxValue} GetValue(roll) {b.GetValue(roll)} (0) {b.GetValue(byte.MinValue)} (255) {b.GetValue(byte.MaxValue)}");
                lines.Add("    text " + Clean(TooltipItemManager.ImplicitFormatter(d, im.property, im.type, im.implicitValue, im.tags, im.specialTag, im.extraTag, TooltipMode.DefaultInfo, TooltipItemManager.SlotType.STASH, i, false, ValueRangeFormat.None)));
            }
        });
        Try("unique", () =>
        {
            if (!d.isUniqueOrLegendary() && !d.isSet()) return;
            var entry = UniqueList.getUnique(d.uniqueID);
            if (entry?.mods == null) return;
            for (int i = 0; i < entry.mods.Count; i++)
            {
                var m = entry.mods[i];
                byte roll = m.canRoll ? d.getUniqueRoll(m.rollID) : byte.MaxValue;
                float v = m.canRoll ? m.getValue(roll) : m.value;
                string range = m.canRoll ? $" min {m.getValue(byte.MinValue)} max {m.getValue(byte.MaxValue)}" : string.Empty;
                lines.Add($"  unique mod {i}: {m.property} {m.type} tags {m.tags} st {m.specialTag} canRoll {m.canRoll} rollID {m.rollID} roll {roll} hidden {m.hideInTooltip}: value {v}{range} {Info(m.GetPropertyInfo(), m.type, m.specialTag, v)}");
                lines.Add("    text " + Clean(TooltipItemManager.UniqueBasicModFormatter(d, m.property, m.type, v, m.tags, m.specialTag, m.extraTag, TooltipMode.DefaultInfo, TooltipItemManager.SlotType.STASH, i, false)));
            }
        });
        // What stash search stored for this item, as it matches it.
        try
        {
            var id = d.id;
            string key = id != null && id.Length > 0 ? Convert.ToHexString(id.ToArray()) : string.Empty;
            if (StashSearch.FactsOf(key) is { } f)
            {
                lines.Add($"  search facts: {f.Rarity}, type {f.Type}/{f.Sub}, {f.W}x{f.H}, LP {f.Lp} WW {f.Ww} WT {f.Wt} FP {f.Fp}, level {f.LevelReq}, class {f.Class}, corrupted {f.Corrupted}, sealed {f.Sealed}");
                foreach (var st in f.Stats)
                    lines.Add($"    {st.Ref.Source}:{st.Ref.Id} \"{st.Text}\"{(st.Tier is int t ? $" T{t}" : string.Empty)}{(st.Prefix ? " prefix" : st.Suffix ? " suffix" : string.Empty)}{(st.Sealed ? " sealed" : string.Empty)} {st.Kind}: "
                        + string.Join("; ", st.Numbers.Select(n => $"{n.Value} ({n.Min}–{n.Max}) q {n.Quality * 100:0}%")));
                lines.Add("    totals: " + string.Join(", ", f.Pseudo.Select(p => $"{p.Key} {Math.Round(p.Value, 2)}")));
            }
            else lines.Add("  search facts: not read");
        }
        catch (Exception e)
        {
            lines.Add("  search facts: error " + e.Message);
        }
        return lines;
    }

    // The property's display flags and the game's rounding of the value.
    private static string Info(BasePropertyInfo? info, BaseStats.ModType type, byte specialTag, float value)
    {
        if (info == null) return "(no property info)";
        return $"[pct {info.displayAddedAsPercentage} tenth {info.displayAddedAsTenthOfValue} lessBetter {info.lessIsBetter} rounding {info.GetRounding(type, specialTag)} rounded {info.GetValueAfterRounding(type, specialTag, value)}]";
    }

    private static string Bytes(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>? b) =>
        b == null ? string.Empty : string.Join(",", b.ToArray());

    // Tooltip text without its rich-text tags, on one line.
    private static string Clean(string? text) =>
        text == null ? "(none)" : System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty).Replace("\n", " | ");
}
