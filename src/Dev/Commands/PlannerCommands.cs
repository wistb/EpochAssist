using System.Globalization;
using System.Text.Json.Nodes;
using EpochAssist.Core;
using EpochAssist.Features;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppLE.UI.PanelSystem;
using UnityEngine;

namespace EpochAssist.Dev;

// planner trees, nodes, skills and watch: the character's trees, the open tree and skill panels and when they change,
// read straight from the game. planner import, check, resolve, paste and host: the planner formats against the local
// fixtures, and the one request a pasted link makes. Nothing here writes to the character, its trees, the bar or the
// save.
internal static class PlannerCommands
{
    public const string Usage = "planner | planner trees | planner nodes [id] | planner skills | planner icons | planner tooltips | planner selector open|close | planner open passive|skills|tree <slot>|picker <slot>|inventory|mastery|close | planner tab <0-3> | planner mastery | planner gear | planner idols | planner blessings [timeline] | planner weaver | planner tooltip <slot>|idol <x> <y>|blessing <n>|weaver <socket> [compare] [rarity <n>] [as <slot type>] [against hovered|carried [reverse]] [at <x> <y>]|close | planner carried | planner slots | planner itemcheck <build id> | planner slotart <slot> | planner sprite <name> | planner sprites <regex> [max px] | planner tooltipcheck <n> | planner watch on|off | planner import <file> | planner check | planner resolve | planner show [stage] | planner paste <text> | planner host <url>|off | " + PlannerGuideCommands.Usage + " | " + PlannerFilterCommands.Usage;

    // Fixtures sit in .reference/fixtures/planner/ (local only); ./build.sh dev mirrors them here so the game can read them.
    private static string FixtureDir => Path.Combine(DevBridge.Dir, "fixtures", "planner");
    // The frames measured before a link paste, to compare the longest frame while it runs with.
    private const float BaselineSeconds = 10f;

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    // The decoder pairs: Last Epoch Tools ids and the digits they hold.
    private static readonly (string Id, string Digits)[] s_pairs =
    {
        ("IIwBjIVgDnI", "1000058000"),
        ("IIwBgzALMwEwvQ", "1034112000"),
        ("UAzDswFgDiA", "007048"),
        ("AMwTgLEA", "394"),
    };

    // The last build imported or pasted here, until the library holds builds.
    private static Build? s_build;
    private static Job? s_watch;
    private static string s_watchLast = string.Empty;

    public static CommandResult Run(CommandCall call) => call.Lower(1) switch
    {
        "" => Status(),
        "trees" => Trees(),
        "nodes" => Nodes(call),
        "skills" => Skills(),
        "icons" => Icons(),
        "tooltips" => Tooltips(),
        "selector" => Selector(call),
        "screen" => ScreenCommand(call),
        "open" => OpenCommand(call),
        "tab" => Tab(call),
        "mastery" => Mastery(),
        "gear" => Gear(),
        "idols" => Idols(),
        "blessings" => Blessings(call),
        "weaver" => Weaver(),
        "tooltip" => Tooltip(call),
        "carried" => Carried(),
        "slots" => Slots(),
        "cost" => Cost(),
        "itemcheck" => ItemCheck(call),
        "hintspot" => HintSpot(),
        "slotart" => SlotArt(call),
        "uitree" => UiTree(call),
        "nodeart" => NodeArt(call),
        "sprite" => SpriteSave(call),
        "sprites" => SpriteList(call),
        "tooltipcheck" => TooltipCheck(call),
        "watch" => WatchSwitch(call),
        "import" => Import(call),
        "check" => Check(),
        "resolve" => ResolveCommand(),
        "show" => Show(call),
        "paste" => PasteCommand(call),
        "host" => Host(call),
        "guide" => PlannerGuideCommands.Run(call),
        "filter" => PlannerFilterCommands.Run(call),
        _ => CommandResult.Error($"planner: usage {Usage}"),
    };

    /// <summary>Stops the watch and forgets the test host (the dev bridge stopping or reloading).</summary>
    public static void Reset()
    {
        s_watch?.Cancel();
        s_watch = null;
        s_watchLast = string.Empty;
        Planner.Fetch.TestHost(null);
        Planner.Panels.Forget();
    }

    // The feature's view: its switch, the character (mode and slot only), the active build and stage, the allocation's
    // signature, the guided panel open, the marks and the last fetch.
    private static CommandResult Status()
    {
        var feature = FeatureHost.Find("planner");
        string running = feature == null ? "not registered" : feature.State.ToString().ToLowerInvariant();
        var build = Planner.ActiveBuild;
        string active = build != null ? $"{build.Id} \"{build.Name}\" stage {Planner.ActiveStage + 1} of {build.Stages.Count}" : "none";
        var allocation = Planner.CurrentAllocation;
        string signature = allocation != null ? $"{allocation.Signature:X16}{(allocation.Stale ? " (stale)" : string.Empty)}" : "none";
        var last = Planner.Fetch.Last;
        string fetch = Planner.Fetch.Busy ? "running" : last != null ? $"{last.State} ({last.Status}, {last.Bytes} bytes, {F(last.Ms)} ms)" : "none";
        return CommandResult.Ok($"planner: switch {(PlannerSettings.Enabled.Value ? "on" : "off")} ({running}), character {Planner.CharacterLabel ?? "none"}, "
            + $"build {active}, signature {signature}, open {Planner.OpenPanel}, marks {Planner.Marks.Count}, last fetch {fetch}");
    }

    // ---- trees ----

    private static CommandResult Trees()
    {
        var tree = Game.LocalTree;
        if (tree == null) return CommandResult.Ok("planner trees: no tree data");
        var lines = new List<string>();
        var cls = tree.characterClass;
        string className = Game.Alive(cls) ? $"{(int)cls!.classID} {cls.className} (tree {cls.treeID})" : "none";
        lines.Add($"ok planner trees: class {className}, mastery {tree.chosenMastery}, level {tree.characterLevel}, unlocked slots {tree.numberOfUnlockedSlots}, online {GameState.Online?.ToString() ?? "?"}");
        lines.Add($"passive {tree.passiveTree?.treeID}: {Nodes(tree.passiveTree?.nodes)}");
        var skills = tree.specialisedSkillTrees;
        for (int i = 0; skills != null && i < skills.Count; i++)
        {
            var skill = skills[i];
            if (skill == null) continue;
            string name = Game.Alive(skill.ability) ? skill.ability!.abilityName : "?";
            lines.Add($"skill slot {skill.slot} {skill.treeID} {name}: {Nodes(skill.nodes)}");
        }
        lines.Add($"weaver {tree.weaverTree?.treeID}: {Nodes(tree.weaverTree?.nodes)}");
        var bar = PlayerModel.Slots;
        lines.Add("bar: " + (bar == null ? "unread" : string.Join(", ", bar.Select((s, i) => $"{i + 1} {s?.AbilityId ?? "-"}"))));
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    private static string Nodes(Il2CppSystem.Collections.Generic.List<LocalTreeData.NodeData>? nodes)
    {
        if (nodes == null) return "none";
        var parts = new List<string>();
        int sum = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node == null) continue;
            parts.Add($"{node.id}:{node.pointsAllocated}");
            sum += node.pointsAllocated;
        }
        return $"{parts.Count} nodes, {sum} points: {string.Join(" ", parts)}";
    }

    // ---- nodes and skills ----

    // The game's panels by type, found once and checked for being open each time, the same way the feature finds them.
    private static T? Panel<T>() where T : Component => Planner.Panels.Find<T>();

    private static bool Open(Component? c) => Planner.Panels.Open(c);

    private static string OpenPanels()
    {
        var open = new List<string>();
        if (Open(Panel<FullScreenPanelPassiveTree>())) open.Add("passive (full screen)");
        if (Game.Alive(PassivePanelManager.instance) && PassivePanelManager.instance!.gameObject.activeInHierarchy) open.Add("passive (panel manager)");
        if (Open(Panel<FullscreenPanelSkillTree>())) open.Add("skill tree");
        if (Game.Alive(AbilityBarSelectorManager.instance) && AbilityBarSelectorManager.instance!.IsMenuOpened) open.Add("skill selection");
        if (Open(Panel<SkillsPanelManager>())) open.Add("skills page");
        return open.Count > 0 ? string.Join(", ", open) : "none";
    }

    // The tree shown now, and which panel answered.
    private static (Il2Cpp.Tree? Tree, string Source) OpenTree()
    {
        var full = Panel<FullScreenPanelPassiveTree>();
        if (Open(full) && Game.Alive(full!._activeTree)) return (full._activeTree, $"FullScreenPanelPassiveTree (mastery page {full.GetActiveMastery()})");
        var manager = PassivePanelManager.instance;
        if (Game.Alive(manager) && manager!.gameObject.activeInHierarchy && Game.Alive(manager.activeTree)) return (manager.activeTree, $"PassivePanelManager (mastery {manager.activeMastery})");
        var weaver = Panel<Il2CppLE.Factions.WeaverTree>();
        if (Open(weaver)) return (weaver, $"WeaverTree ({weaver!.treeID})");
        var skill = Panel<FullscreenPanelSkillTree>();
        if (Open(skill) && Game.Alive(skill!._currentlyOpenTree))
            return (skill._currentlyOpenTree, $"FullscreenPanelSkillTree ({(Game.Alive(skill._currentAbility) ? skill._currentAbility!.playerAbilityID : "?")})");
        var page = Panel<SkillsPanelManager>();
        if (Open(page) && Game.Alive(page!.CurrentlyOpenTree)) return (page.CurrentlyOpenTree, "SkillsPanelManager");
        return (null, "none");
    }

    private static CommandResult Nodes(CommandCall call)
    {
        var (tree, source) = OpenTree();
        if (!Game.Alive(tree)) return CommandResult.Ok($"planner nodes: no tree panel open ({OpenPanels()})");
        var list = tree!.nodeList;
        if (call.Args.Length > 1)
        {
            if (!byte.TryParse(call.Lower(2), out byte id)) return CommandResult.Error($"planner: usage {Usage}");
            var node = tree.getNode(id);
            if (!Game.Alive(node)) return CommandResult.Ok($"planner nodes {id}: not in tree {tree.treeID}");
            var lines = new List<string> { $"ok planner nodes {id}: {node!.nodeName}, rect {Rect(node.transform)}" };
            // Every image under the node, so a mark can borrow the hexagon frame's sprite.
            foreach (var image in node.GetComponentsInChildren<UnityEngine.UI.Image>(true))
            {
                if (!Game.Alive(image) || image.name == "EA.PlannerMark") continue;
                var sprite = image.sprite;
                lines.Add($"image '{image.name}': sprite {(Game.Alive(sprite) ? $"'{sprite!.name}' tex '{sprite.texture?.name}' {sprite.rect.width}x{sprite.rect.height} border {sprite.border}" : "none")}, "
                    + $"type {image.type}, colour {image.color}, active {image.gameObject.activeInHierarchy}, rect {Rect(image.transform)}");
            }
            // And its texts, so a label can match the point counter's font.
            foreach (var text in node.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true))
                if (Game.Alive(text))
                    lines.Add($"tmp '{text.name}': '{text.text}' font '{text.font?.name}' material '{text.fontSharedMaterial?.name}' size {text.fontSize} style {text.fontStyle} colour {text.color} active {text.gameObject.activeInHierarchy} rect {Rect(text.transform)}");
            foreach (var text in node.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                if (Game.Alive(text) && !text.name.StartsWith("EA."))
                    lines.Add($"text '{text.name}': '{text.text}' font '{text.font?.name}' size {text.fontSize} colour {text.color} active {text.gameObject.activeInHierarchy}");
            for (var t = node.transform; t != null; t = t.parent)
                lines.Add($"{t.name}  local scale {V(t.localScale)}  lossy {V(t.lossyScale)}  pos {V(t.localPosition)}{(t.gameObject.activeSelf ? string.Empty : "  inactive")}");
            return new CommandResult(DevOutput.Reply("planner", lines));
        }
        var rows = new List<string> { $"ok planner nodes: {source}, tree {tree.treeID}, {list?.Count ?? 0} nodes" };
        for (int i = 0; list != null && i < list.Count; i++)
        {
            var node = list[i];
            if (!Game.Alive(node)) continue;
            rows.Add($"{node!.id,3} max {node.maxPoints,2} alloc {node.pointsAllocated,2} mastery {node.mastery} {(node.gameObject.activeInHierarchy ? "active" : "inactive")} rect {Rect(node.transform)}  {node.nodeName}");
        }
        return new CommandResult(DevOutput.Reply("planner", rows));
    }

    private static CommandResult Skills()
    {
        var lines = new List<string> { $"ok planner skills: open {OpenPanels()}" };
        var selector = AbilityBarSelectorManager.instance;
        if (Game.Alive(selector) && selector!.IsMenuOpened)
        {
            lines.Add($"selection panel: slot clicked {selector.abilityClicked}");
            var icons = selector._abilitySelectionIcons;
            for (int i = 0; icons != null && i < icons.Count; i++)
            {
                var icon = icons[i];
                if (Game.Alive(icon)) lines.Add($"  pick {AbilityText(icon!.ability)} rect {Rect(icon.transform)}");
            }
        }
        var page = Panel<SkillsPanelManager>();
        if (Open(page))
        {
            AddIcons(lines, "base", page!.baseClassPanelIcons);
            AddIcons(lines, "mastery 1", page.masteryClass1PanelIcons);
            AddIcons(lines, "mastery 2", page.masteryClass2PanelIcons);
            AddIcons(lines, "mastery 3", page.masteryClass3PanelIcons);
            foreach (var (n, icon) in new[] { (1, page.masteryAbilityPanelIcon1), (2, page.masteryAbilityPanelIcon2), (3, page.masteryAbilityPanelIcon3) })
                if (Game.Alive(icon)) lines.Add($"  mastery skill {n} {AbilityText(icon!.ability)} {(icon.gameObject.activeInHierarchy ? "shown" : "hidden")} rect {Rect(icon.transform)}");
            var specialised = Panel<SpecializedSkillsUIManager>();
            var icons = Open(specialised) ? specialised!._specializedSkillIcons : null;
            for (int i = 0; icons != null && i < icons.Count; i++)
            {
                var icon = icons[i];
                if (Game.Alive(icon)) lines.Add($"  specialised slot {icon!.AbilityNumber} {AbilityText(icon.Ability)} rect {Rect(icon.transform)}");
            }
            lines.Add($"picker: slot {page.currentSpecializationSlotIndex}, free slot {page.isAnyAvailableSpecializationSlot}");
            foreach (var group in new[] { page.baseClassPanelIcons, page.masteryClass1PanelIcons, page.masteryClass2PanelIcons, page.masteryClass3PanelIcons })
            {
                for (int i = 0; group != null && i < group.Count; i++)
                {
                    var icon = group[i];
                    if (Game.Alive(icon) && icon!.specMode && Game.Alive(icon.glowBackground) && icon.glowBackground.activeInHierarchy)
                        lines.Add($"  offered {AbilityText(icon.ability)} slot {icon.slotToSpec} rect {Rect(icon.transform)}");
                }
            }
            // The five specialisation slots above an open tree, with the images that draw them.
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpecialisedAbilityIcon>(), false))
            {
                var slot = obj.TryCast<SpecialisedAbilityIcon>();
                if (!Game.Alive(slot)) continue;
                lines.Add($"  slot icon {slot!.abilityNumber} {AbilityText(slot.ability)} rect {Rect(slot.transform)}");
                foreach (var image in slot.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                    if (Game.Alive(image) && image.name != "EA.PlannerMark")
                        lines.Add($"    image '{image.name}' sprite '{(Game.Alive(image.sprite) ? image.sprite!.name : "none")}' active {image.gameObject.activeInHierarchy} alpha {image.color.a:0.##} rect {Rect(image.transform)}");
            }
            var spec = page.SkillsSpecialisationUIManager;
            if (Game.Alive(spec))
                lines.Add($"skill respec: respeccing {spec!.Respeccing}, button {Control(spec.respecButton)}, stop {Control(spec.stopRespeccingButton)}");
        }
        var passive = PassivePanelManager.instance;
        if (Game.Alive(passive) && passive!.gameObject.activeInHierarchy)
        {
            var all = passive.respecAllButton;
            lines.Add($"passive respec: respeccing {passive.respeccing}, button active {passive.IsRespecButtonActive}, respec all {(Game.Alive(all) ? $"{Control(all!.gameObject)} interactable {all.interactable}" : "none")}");
        }
        if (lines.Count == 1) lines[0] += "; no skill panel open";
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // planner tooltips: active objects named like a tooltip anywhere (outermost of nested ones) with their screen rects,
    // components and first text, to find the panel a hover hint should stay clear of.
    private static CommandResult Tooltips()
    {
        var lines = new List<string> { $"ok planner tooltips: open {OpenPanels()}" };
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<RectTransform>(), false))
        {
            var rect = obj.TryCast<RectTransform>();
            if (!Game.Alive(rect) || !rect!.gameObject.activeInHierarchy || !rect.name.Contains("tooltip", StringComparison.OrdinalIgnoreCase)) continue;
            bool inner = false;
            for (var up = rect.parent; up != null && !inner; up = up.parent)
                inner = up.name.Contains("tooltip", StringComparison.OrdinalIgnoreCase) && up.GetComponent<Canvas>() == null;
            if (inner || rect.GetComponent<Canvas>() != null) continue;
            var types = string.Join(", ", rect.GetComponents<Component>().Where(c => c != null).Select(c => c.GetIl2CppType().Name));
            var text = rect.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(false).FirstOrDefault(x => Game.Alive(x) && !string.IsNullOrWhiteSpace(x.text))?.text;
            lines.Add($"{GameTypes.PathOf(rect.gameObject)} [{types}] rect {Rect(rect)} text '{(text == null ? string.Empty : text.Substring(0, Math.Min(30, text.Length)))}'");
        }
        if (lines.Count == 1) lines[0] += "; none";
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // planner icons: the images that draw one icon of each skill choice kind, to find the frame a mark copies.
    private static CommandResult Icons()
    {
        var lines = new List<string> { $"ok planner icons: open {OpenPanels()}" };
        void Dump(string kind, Component? icon)
        {
            if (!Game.Alive(icon)) return;
            lines.Add($"{kind} '{icon!.name}' active {icon.gameObject.activeInHierarchy} rect {Rect(icon.transform)}");
            foreach (var image in icon.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                if (Game.Alive(image))
                    lines.Add($"  image '{image.name}' sprite '{(Game.Alive(image.sprite) ? image.sprite!.name : "none")}' active {image.gameObject.activeInHierarchy} alpha {image.color.a:0.##} sibling {image.transform.GetSiblingIndex()} parent '{image.transform.parent?.name}' rect {Rect(image.transform)}");
        }
        var bar = Game.BarIcons;
        for (int i = 0; bar != null && i < bar.Count; i++)
        {
            var icon = bar[i];
            if (!Game.Alive(icon) || icon!.abilityNumber != 1) continue;
            Dump("bar", icon);
            if (icon.transform.parent is { } slot) Dump("bar slot", slot.TryCast<RectTransform>());
        }
        var selector = AbilityBarSelectorManager.instance;
        if (Game.Alive(selector) && selector!.IsMenuOpened && selector._abilitySelectionIcons is { Count: > 3 } picks) Dump("pick", picks[3]);
        var page = Panel<SkillsPanelManager>();
        if (Open(page))
        {
            if (page!.baseClassPanelIcons is { Count: > 0 } icons) Dump("page", icons[0]);
            Dump("mastery skill", page.masteryAbilityPanelIcon1);
            var specialised = Panel<SpecializedSkillsUIManager>();
            if (Open(specialised) && specialised!._specializedSkillIcons is { Count: > 0 } slots) Dump("specialised", slots[0]);
        }
        // Node labels and their plates, which sit in the node container rather than under a mark.
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UnityEngine.UI.Graphic>(), true))
        {
            var part = obj.TryCast<UnityEngine.UI.Graphic>();
            if (!Game.Alive(part) || !part!.name.StartsWith("EA.PlannerMark.")) continue;
            lines.Add($"{part.name} parent '{part.transform.parent?.name}' active {part.gameObject.activeInHierarchy} colour #{ColorUtility.ToHtmlStringRGBA(part.color)} rect {Rect(part.transform)}");
        }
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UnityEngine.UI.Image>(), false))
        {
            var image = obj.TryCast<UnityEngine.UI.Image>();
            if (!Game.Alive(image) || image!.name != "EA.PlannerMark") continue;
            var holder = image.transform.parent;
            var owner = holder != null ? holder.parent : null;
            lines.Add($"mark on '{owner?.name}/{holder?.name}' colour #{ColorUtility.ToHtmlStringRGBA(image.color)} rect {Rect(image.transform)}");
        }
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // planner selector open|close: the bar's skill selection panel through the game's own calls, for checking
    // planner skills when a click on a bar slot doesn't open it.
    private static CommandResult Selector(CommandCall call)
    {
        var selector = AbilityBarSelectorManager.instance;
        if (!Game.Alive(selector)) return CommandResult.Error("planner selector: no AbilityBarSelectorManager");
        switch (call.Lower(2))
        {
            case "open": selector!.OpenMenu(); break;
            case "close": selector!.CloseMenu(false); break;
            default: return CommandResult.Error("planner selector: usage planner selector open|close");
        }
        return CommandResult.Ok($"planner selector {call.Lower(2)}: open {selector.IsMenuOpened}");
    }

    // The menu UI scale and the resolution before the first change, so `restore` puts back exactly what was there.
    private static (float Scale, int Width, int Height, FullScreenMode Mode)? s_screenBefore;

    // planner screen [scale <v>|size <w> <h>|restore]: checks that marks stay on their nodes when the game's UI scale
    // or the resolution changes; the scale goes through the game's own setting, which it saves.
    private static CommandResult ScreenCommand(CommandCall call)
    {
        var gameplay = Il2CppLE.Settings.SettingsManager.Gameplay;
        if (gameplay == null) return CommandResult.Error("planner screen: no gameplay settings");
        string Now() => $"menu scale {gameplay.GetUIScale(UIScalingGroup.Menu).ToString("0.##", s_inv)}, {Screen.width}x{Screen.height} {Screen.fullScreenMode}";
        void Remember() => s_screenBefore ??= (gameplay.GetUIScale(UIScalingGroup.Menu), Screen.width, Screen.height, Screen.fullScreenMode);
        switch (call.Lower(2))
        {
            case "":
                return CommandResult.Ok($"planner screen: {Now()}");
            case "scale":
                if (!float.TryParse(call.Lower(3), NumberStyles.Float, s_inv, out float scale)) return CommandResult.Error("planner screen: usage planner screen scale <value>");
                Remember();
                gameplay.SetUIScale(UIScalingGroup.Menu, scale);
                return CommandResult.Ok($"planner screen scale: {Now()}");
            case "size":
                if (!int.TryParse(call.Lower(3), out int w) || !int.TryParse(call.Lower(4), out int h)) return CommandResult.Error("planner screen: usage planner screen size <w> <h>");
                Remember();
                Screen.SetResolution(w, h, Screen.fullScreenMode);
                return CommandResult.Ok($"planner screen size: asked {w}x{h}");
            case "restore":
                if (s_screenBefore is not { } before) return CommandResult.Ok($"planner screen restore: nothing changed; {Now()}");
                gameplay.SetUIScale(UIScalingGroup.Menu, before.Scale);
                Screen.SetResolution(before.Width, before.Height, before.Mode);
                s_screenBefore = null;
                return CommandResult.Ok($"planner screen restore: asked scale {before.Scale.ToString("0.##", s_inv)}, {before.Width}x{before.Height} {before.Mode}");
            default:
                return CommandResult.Error("planner screen: usage planner screen [scale <v>|size <w> <h>|restore]");
        }
    }

    private static string Control(GameObject? go) =>
        Game.Alive(go) ? $"{(go!.activeInHierarchy ? "shown" : "hidden")} rect {Rect(go.transform)}" : "none";

    // planner open: the guided panels through the game's own calls, so a check needs no key binds.
    private static CommandResult OpenCommand(CommandCall call)
    {
        var ui = UIBase.instance;
        if (!Game.Alive(ui)) return CommandResult.Error("planner open: no UIBase");
        switch (call.Lower(2))
        {
            case "passive":
                ui!.OpenPassivePanel(false);
                return CommandResult.Ok("planner open passive: UIBase.OpenPassivePanel");
            case "skills":
                ui!.openSkills(false, null);
                return CommandResult.Ok("planner open skills: UIBase.openSkills");
            case "tree":
                if (!int.TryParse(call.Lower(3), out int slot)) return CommandResult.Error("planner open: usage planner open tree <slot>");
                var skills = Game.LocalTree?.specialisedSkillTrees;
                for (int i = 0; skills != null && i < skills.Count; i++)
                {
                    var skill = skills[i];
                    if (skill == null || skill.slot != slot || !Game.Alive(skill.ability)) continue;
                    ui!.openSkills(false, skill.ability);
                    return CommandResult.Ok($"planner open tree {slot}: UIBase.openSkills with {AbilityText(skill.ability)}");
                }
                return CommandResult.Ok($"planner open tree {slot}: no specialised skill in that slot");
            case "picker":
                if (!int.TryParse(call.Lower(3), out int pick)) return CommandResult.Error("planner open: usage planner open picker <slot>");
                var page = Panel<SkillsPanelManager>();
                if (!Open(page)) return CommandResult.Ok("planner open picker: the Skills page isn't open");
                page!.SelectSkillToSpec(pick);
                return CommandResult.Ok($"planner open picker {pick}: SkillsPanelManager.SelectSkillToSpec");
            case "inventory":
                // The fourth argument picks the tab; "inventory <n>" tries another.
                int tab = int.TryParse(call.Lower(3), out int t) ? t : 0;
                ui!.openInventory(false, false, false, tab, false);
                return CommandResult.Ok($"planner open inventory: UIBase.openInventory tab {tab}; open {ui.IsInventoryOpen()}");
            case "weaver":
                ui!.OpenWeaverRankPanel(false, false);
                var weaver = Panel<Il2CppLE.Factions.FactionRankUIWeaver>();
                return CommandResult.Ok($"planner open weaver: UIBase.OpenWeaverRankPanel; panel {(Open(weaver) ? $"open, tree {(weaver!.IsWeaverTreeOpened ? "shown" : "hidden")}" : "closed")}");
            // The mastery choice the game offers after its story quest; opening it here skips that quest.
            case "mastery":
                ui!.openMasteryPanel(false, false);
                return CommandResult.Ok($"planner open mastery: UIBase.openMasteryPanel; open {ui.IsMasteryPanelOpen()}");
            case "close":
                ui!.closeInventory();
                ui.closePassiveTree();
                ui.closeSkills(false);
                if (Game.Alive(AbilityBarSelectorManager.instance) && AbilityBarSelectorManager.instance!.IsMenuOpened) AbilityBarSelectorManager.instance.CloseMenu(false);
                return CommandResult.Ok($"planner open close: open {OpenPanels()}");
            default:
                return CommandResult.Error("planner open: usage planner open passive|skills|tree <slot>|picker <slot>|inventory [tab]|weaver|mastery|close");
        }
    }

    // planner tab: the base (0) or a mastery page (1-3) in the open passive panel.
    private static CommandResult Tab(CommandCall call)
    {
        var manager = PassivePanelManager.instance;
        if (!Game.Alive(manager) || !manager!.gameObject.activeInHierarchy) return CommandResult.Ok("planner tab: the passive panel isn't open");
        if (!int.TryParse(call.Lower(2), out int page) || page < 0 || page > 3) return CommandResult.Error("planner tab: usage planner tab <0-3>");
        var banner = manager.openedBanner;
        if (!Game.Alive(banner)) return CommandResult.Ok("planner tab: no mastery banner");
        banner!.Clicked(page);
        return CommandResult.Ok($"planner tab {page}: MasteryBanner.Clicked; active mastery {manager.activeMastery}");
    }

    // planner mastery: the passive panel's mastery buttons and the mastery choice panel. Never chooses one.
    private static CommandResult Mastery()
    {
        var lines = new List<string> { $"ok planner mastery: chosen {Game.LocalTree?.chosenMastery.ToString() ?? "?"}, choice panel {(UIBase.instance?.IsMasteryPanelOpen() == true ? "open" : "closed")}" };
        var manager = PassivePanelManager.instance;
        var banner = Game.Alive(manager) && manager!.gameObject.activeInHierarchy ? manager.openedBanner : null;
        if (Game.Alive(banner))
        {
            lines.Add($"banner: class {banner!.characterClassID}, active mastery {manager!.activeMastery}");
            var buttons = banner.masteryButtons;
            for (int i = 0; buttons != null && i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (Game.Alive(b)) lines.Add($"  button {i} {b!.name} {(b.gameObject.activeInHierarchy ? "shown" : "hidden")} rect {Rect(b.transform)}");
            }
        }
        var choice = MasteryPanelManager.instance;
        if (Game.Alive(choice) && choice!.gameObject.activeInHierarchy)
        {
            lines.Add($"choice: class {choice._characterClassID}, chosen {choice._chosenMastery}, respec {choice._respec}");
            var panels = choice.panels;
            for (int i = 0; panels != null && i < panels.Count; i++)
            {
                var p = panels[i]?.masteryPanel;
                if (Game.Alive(p)) lines.Add($"  offered {i} {p!.name} {(p.gameObject.activeInHierarchy ? "shown" : "hidden")} rect {Rect(p.transform)}");
            }
        }
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // ---- gear, idols, blessings, weaver (read only) ----

    private static CommandResult Gear()
    {
        var manager = ItemContainersManager.Instance;
        if (!Game.Alive(manager) || manager!.equipment == null) return CommandResult.Ok("planner gear: no item containers");
        var lines = new List<string> { $"ok planner gear: inventory {(UIBase.instance?.IsInventoryOpen() == true ? "open" : "closed")}" };
        var gear = PlannedGear();
        foreach (var (slot, _, _) in Planner.GearSlots)
        {
            AddEntries(lines, slot, Planner.SlotContainer(slot));
            if (gear?.Slots.TryGetValue(slot, out var planned) == true) lines.Add($"    planned {PlannedText(planned!)}");
            if (Planner.SlotGuide(Planner.CurrentGear, slot) is { } guide)
                lines.Add($"    state {(guide.Unknown ? "unknown" : guide.State.ToString())}: {guide.Planned.Name} (planned for {guide.Planned.Slot}){(guide.Missing.Count > 0 ? $" missing [{string.Join(" ", guide.Missing.Select(m => $"{m.Id}/t{m.Tier}"))}]" : string.Empty)}");
        }
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    private static CommandResult Idols()
    {
        var manager = ItemContainersManager.Instance;
        if (!Game.Alive(manager) || manager!.idols == null) return CommandResult.Ok("planner idols: no item containers");
        var idols = manager.idols;
        var lines = new List<string> { $"ok planner idols: grid {idols.size.x}x{idols.size.y}, unlock {idols.unlockIdx}, inventory {(UIBase.instance?.IsInventoryOpen() == true ? "open" : "closed")}" };
        AddEntries(lines, "idol", idols.TryCast<IItemContainer>());
        foreach (var planned in PlannedGear()?.Idols ?? new List<PlannedItem>())
        {
            string state = Planner.CurrentGear?.Idols.TryGetValue((planned.X, planned.Y), out var guide) == true ? (guide!.Unknown ? "unknown" : guide.State.ToString()) : "none";
            lines.Add($"  planned at {planned.X},{planned.Y} size {planned.Width}x{planned.Height} {state}: {PlannedText(planned)}");
        }
        if (Planner.CurrentGear is { } guidance)
            lines.Add($"  checklist gear {guidance.Gear.Done}/{guidance.Gear.Total} idols {guidance.IdolList.Done}/{guidance.IdolList.Total} (unknown {guidance.IdolList.Unknown}) blessings {guidance.BlessingList.Done}/{guidance.BlessingList.Total}");
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    private static CommandResult Blessings(CommandCall call)
    {
        var manager = ItemContainersManager.Instance;
        if (!Game.Alive(manager) || manager!.blessings == null) return CommandResult.Ok("planner blessings: no item containers");
        var boxes = manager.blessings.Containers;
        var lines = new List<string> { $"ok planner blessings: {boxes?.Count ?? 0} slots; marks see {Planner.BlessingListText}" };
        // planner blessings frame: the inventory panel's own parts two levels down, to tell its frame art from its content.
        if (call.Lower(2) == "frame" && GameObject.Find("Right Panel Stack") is { } stack && stack.transform.Find("InventoryPanel (Clone)") is { } panel)
        {
            void Walk(Transform t, int depth)
            {
                var g = t.GetComponent<UnityEngine.UI.Image>();
                lines.Add($"  {new string(' ', depth * 2)}{t.name} active {t.gameObject.activeSelf}{(Game.Alive(g) ? $" image {(Game.Alive(g!.sprite) ? g.sprite!.name : "none")} {g.type}" : string.Empty)} rect {Rect(t)} children {t.childCount}");
                if (depth < 2)
                    for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
            }
            Walk(panel, 0);
            return new CommandResult(DevOutput.Reply("planner", lines));
        }
        for (int i = 0; boxes != null && i < boxes.Count; i++)
            AddEntries(lines, $"slot {i}", boxes[i]?.TryCast<IItemContainer>());
        var map = ItemContainersManager.BlessingItemContainer.TimelineToBlessingContainer;
        if (map != null)
        {
            var pairs = new List<string>();
            foreach (var pair in map) pairs.Add($"{pair.Key}={pair.Value}");
            lines.Add($"  timelines {string.Join(" ", pairs)}");
        }
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<InventoryBlessingSlotUI>(), false))
        {
            var ui = obj.TryCast<InventoryBlessingSlotUI>();
            if (!Game.Alive(ui) || !ui!.gameObject.activeInHierarchy) continue;
            var item = ui.itemData;
            if (SlotTimeline(ui) == (int.TryParse(call.Lower(2), out int look) ? look : 3) && ui.transform.parent is { } around)
                foreach (var image in around.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                    lines.Add($"    slot image {ui.name}<{around.name}: {image.transform.parent?.name}/{image.name} active {image.gameObject.activeInHierarchy} sprite {(Game.Alive(image.sprite) ? $"{image.sprite!.name} {image.sprite.rect.width}x{image.sprite.rect.height}" : "none")} type {image.type} preserve {image.preserveAspect} rect {Rect(image.transform)}");
            if (SlotTimeline(ui) < 0 && item?.subType == (ushort)(int.TryParse(call.Lower(3), out int sub) ? sub : -1))
                foreach (var image in ui.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                    lines.Add($"    entry image {image.transform.parent?.name}/{image.name} active {image.gameObject.activeInHierarchy} sprite {(Game.Alive(image.sprite) ? image.sprite!.name : "none")} colour {image.color} rect {Rect(image.transform)}");
            lines.Add($"  shown timeline {SlotTimeline(ui)} {(item != null ? $"{item.FullName} sub {item.subType}" : "empty")} at {ElementPath(ui.transform)} grand {ui.isGrand} roll {ui.powerRoll.ToString("0.##", s_inv)} rect {Rect(ui.transform)}");
        }
        string SubName(byte type, ushort sub) => $"'{Planner.SubTypeName(type, sub)}'";
        foreach (var (timeline, planned) in PlannedGear()?.Blessings ?? new SortedDictionary<int, PlannedItem>())
        {
            lines.Add($"  planned timeline {timeline}: {PlannedText(planned)}");
            var worn = timeline < (boxes?.Count ?? 0) ? boxes![timeline]?.TryCast<IItemContainer>()?.GetContent() : null;
            var first = worn is { Count: > 0 } ? worn[0] : null;
            var data = first?.data;
            lines.Add($"    equipped entries {(worn == null ? "none" : worn.Count.ToString(s_inv))}, data {(data == null ? "null" : data.subType.ToString(s_inv))}");
            if (data != null)
                lines.Add($"    planned {SubName(planned.ItemType, planned.SubType)}; equipped sub {data.subType} {SubName(data.itemType, data.subType)}; normal of grand {Planner.NormalOfGrand(planned, data.subType)}");
        }
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    private static CommandResult Weaver()
    {
        var tree = Game.LocalTree?.weaverTree;
        var lines = new List<string> { $"ok planner weaver: tree {tree?.treeID ?? "none"}: {Nodes(tree?.nodes)}" };
        var panel = Panel<Il2CppLE.Factions.FactionRankUIWeaver>();
        lines.Add($"weaver panel: {(Open(panel) ? $"open, tree {(panel!.IsWeaverTreeOpened ? "shown" : "hidden")}" : "closed")}");
        lines.Add($"gate: factions panel open {UIBase.instance?.IsFactionsPanelOpen()}, weaver guide {(Planner.CurrentGear?.Weaver != null ? "yes" : "no")}");
        if (PlannedGear()?.Weaver is { } planned)
        {
            lines.Add($"planned: {planned.Total} points: {string.Join(" ", planned.Points.Select(p => $"{p.Key}:{p.Value}"))}");
            lines.Add($"planned order: {(planned.Order is { } order ? string.Join(" ", order.Select(s => $"{s.Node}+{s.Add}")) : "none")}");
        }
        else
        {
            lines.Add("planned: none");
        }

        // The sockets: the game's container ids for them, its node id lists, the socketed items and the socket elements.
        var socketIds = Enum.GetValues(typeof(ContainerID)).Cast<ContainerID>().Where(id => id.ToString().Contains("WEAVER_TREE_ITEM_NODE", StringComparison.Ordinal));
        lines.Add("socket container ids: " + string.Join(" ", socketIds.Select(id => $"{id}={Convert.ToInt64(id, s_inv)} item node {ContainerIDExtensions.IsWeaverItemNode(id)}")));
        var owner = typeof(ItemContainersManager.WeaverTreeContainer);
        foreach (var (label, list) in new[] { ("large", ItemContainersManager.WeaverTreeContainer.WeaverTreeLargeContainerNodeIDs), ("small", ItemContainersManager.WeaverTreeContainer.WeaverTreeSmallContainerNodeIDs) })
        {
            var pairs = new List<string>();
            if (list != null)
                foreach (var pair in list) pairs.Add($"{pair.Key}={pair.Value}");
            lines.Add($"{owner.Name}.{label} node ids: {(list == null ? "null" : string.Join(" ", pairs))}");
        }
        lines.Add("socket map: " + string.Join(" ", Planner.WeaverSockets.Select(p => $"{p.Key}={p.Value}")));
        var smallTypes = ItemContainersManager.WeaverTreeContainer.WeaverTreeSmallContainerBaseTypes;
        lines.Add($"small socket base types: {(smallTypes == null ? "null" : string.Join(" ", smallTypes))}");
        var weaverData = GlobalTreeData.get()?.getTreeData(Planner.WeaverTreeId);
        foreach (int id in Planner.WeaverSockets.Values.Select(n => (int)n).Concat(Enumerable.Range(89, 9)))
        {
            var data = weaverData?.getNode((byte)id);
            lines.Add($"  game data node {id}: {(data == null ? "none" : $"{data.name} max {data.maxPoints}")}");
        }
        var sockets = ItemContainersManager.Instance?.weaverTreeItems;
        if (sockets == null)
        {
            lines.Add("weaverTreeItems: none");
        }
        else
        {
            var boxes = sockets.containers;
            var held = new List<string>();
            if (boxes != null)
                foreach (var pair in boxes) held.Add($"{pair.Key}->node {sockets.GetLinkedNodeIdFromContainerId(pair.Key)}");
            lines.Add($"weaverTreeItems: {sockets.GetType().Name}, containers {boxes?.Count.ToString(s_inv) ?? "null"} [{string.Join(" ", held)}], large {ItemContainersManager.WeaverTreeContainer.LargeContainerSize.x}x{ItemContainersManager.WeaverTreeContainer.LargeContainerSize.y}, small {ItemContainersManager.WeaverTreeContainer.SmallContainerSize.x}x{ItemContainersManager.WeaverTreeContainer.SmallContainerSize.y}");
            foreach (var (socket, node) in Planner.WeaverSockets) AddEntries(lines, $"socket {socket} node {node}", Planner.SocketContainer(socket));
        }
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppLE.Factions.WeaverTreeItemContainerUI>(), true))
        {
            var ui = obj.TryCast<Il2CppLE.Factions.WeaverTreeItemContainerUI>();
            if (!Game.Alive(ui)) continue;
            lines.Add($"  socket element {ElementPath(ui!.transform)} node {ui.LinkedNodeID} active {ui.gameObject.activeInHierarchy} rect {Rect(ui.transform)}");
            // One socket of each size is enough to see its art.
            if (ui.name is "ItemSlot" or "ItemSlotSmall")
            foreach (var image in ui.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                if (Game.Alive(image) && image.gameObject.activeInHierarchy && !image.name.StartsWith("EA.PlannerMark", StringComparison.Ordinal))
                    lines.Add($"    image {image.transform.parent?.name}/{image.name} active {image.gameObject.activeInHierarchy} sprite {(Game.Alive(image.sprite) ? $"{image.sprite!.name} {image.sprite.rect.width}x{image.sprite.rect.height} border {image.sprite.border}" : "none")} type {image.type} colour {image.color} rect {Rect(image.transform)}");
        }
        // Which canvas draws over which: the item tooltip's and the weaver page's.
        string CanvasText(Component c)
        {
            var canvas = c.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            return root == null ? "no canvas" : $"canvas {root.name} order {root.sortingOrder} layer {root.sortingLayerName} mode {root.renderMode}, own {canvas!.name} override {canvas.overrideSorting} order {canvas.sortingOrder}";
        }
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<TooltipItemManager>(), true))
            if (obj.TryCast<TooltipItemManager>() is { } manager && Game.Alive(manager))
                lines.Add($"  tooltip manager {ElementPath(manager.transform)}: {CanvasText(manager)}, active item {(manager.ActiveItem != null ? manager.ActiveItem.FullName : "none")}");
        if (UITooltipItem.instance is { } tipUi && Game.Alive(tipUi)) lines.Add($"  item tooltip ui {ElementPath(tipUi.transform)}: {CanvasText(tipUi)}");
        if (Panel<Il2CppLE.Factions.FactionRankUIWeaver>() is { } rank && Game.Alive(rank)) lines.Add($"  weaver page {ElementPath(rank.transform)}: {CanvasText(rank)}");
        // The tree's own layout: its direct children and what they draw, to tell its background from its nodes.
        if (Panel<Il2CppLE.Factions.WeaverTree>() is { } shown && Game.Alive(shown))
        {
            for (var at = shown.transform; at != null && at.name != "Panel System"; at = at.parent)
            {
                var own = at.GetComponent<UnityEngine.UI.Graphic>();
                lines.Add($"  tree chain {at.name}: {(Game.Alive(own) ? $"{own!.GetType().Name} {(own.TryCast<UnityEngine.UI.Image>()?.sprite is { } sp && Game.Alive(sp) ? sp.name : "-")} enabled {own.enabled}" : "no graphic")}, {at.childCount} children");
            }
            var groups = new Dictionary<string, int>();
            for (int i = 0; i < shown.transform.childCount; i++)
            {
                var child = shown.transform.GetChild(i);
                string key = System.Text.RegularExpressions.Regex.Replace(child.name, @"\s*\(\d+\)$", string.Empty);
                groups[key] = groups.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            lines.Add("  tree children: " + string.Join(", ", groups.Select(g => $"{g.Key} x{g.Value}")));
        }
        var socketNodes = tree?.nodes;
        var inTree = new List<string>();
        for (int i = 0; socketNodes != null && i < socketNodes.Count; i++)
            if (socketNodes[i] is { } n && Planner.WeaverSockets.Values.Contains(n.id)) inTree.Add($"{n.id}:{n.pointsAllocated}");
        lines.Add($"socket nodes in the character's weaver tree: {(inTree.Count == 0 ? "none" : string.Join(" ", inTree))}");
        var panelTree = Panel<Il2CppLE.Factions.WeaverTree>();
        if (Open(panelTree) && panelTree!.nodeList is { } list2)
        {
            var listed = new List<string>();
            for (int i = 0; i < list2.Count; i++)
                if (list2[i] is { } n && Planner.WeaverSockets.Values.Contains((byte)n.id))
                {
                    listed.Add($"{n.id}:{n.pointsAllocated} {n.nodeName}");
                    // The node's images and any mark on it, for the socket node nearest the first planned socket.
                    if (n.id == 45)
                        foreach (var image in n.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                            if (Game.Alive(image))
                                lines.Add($"    node 45 image {image.transform.parent?.name}/{image.name} active {image.gameObject.activeInHierarchy} enabled {image.enabled} sprite {(Game.Alive(image.sprite) ? image.sprite!.name : "none")} colour {image.color} sibling {image.transform.GetSiblingIndex()} rot {image.transform.localEulerAngles} scale {image.transform.localScale} size {image.rectTransform.rect.size}");
                }
            lines.Add($"socket nodes in the panel's nodeList ({list2.Count}): {(listed.Count == 0 ? "none" : string.Join(", ", listed))}");
        }
        foreach (var (node, socketItem) in PlannedGear()?.WeaverItems ?? new SortedDictionary<int, PlannedItem>())
        {
            string state = Planner.CurrentGear?.WeaverItems.TryGetValue(node, out var guide) == true
                ? (guide!.Unknown ? "unknown" : guide.State.ToString()) + (guide.Missing.Count > 0 ? $" missing [{string.Join(" ", guide.Missing.Select(m => $"{m.Id}/t{m.Tier}"))}]" : string.Empty)
                : "none";
            lines.Add($"  planned socket {node} on node {(Planner.WeaverSockets.TryGetValue(node, out byte on) ? on.ToString(s_inv) : "none")} {state}: {PlannedText(socketItem)}");
        }
        if (Planner.CurrentGear is { } guidance)
            lines.Add($"  checklist weaver items {guidance.WeaverItemList.Done}/{guidance.WeaverItemList.Total} (unknown {guidance.WeaverItemList.Unknown})");
        if (PlannedGear()?.MaxrollWeaverItemsSkipped == true) lines.Add("  maxroll weaver items skipped");
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // The active stage's planned gear, or null with no build in use.
    private static PlannedGear? PlannedGear() => Planner.ActiveBuild is { } build ? Planner.GearOf(build, Planner.ActiveStage) : null;

    private static string PlannedText(PlannedItem p)
    {
        string affixes = string.Join(" ", p.Affixes.Select(a => $"{a.Id}/t{a.Tier}/r{a.Roll}{(a.Sealed != SealedAffixType.None ? "/" + a.Sealed : string.Empty)}"));
        string unknown = p.Unknown.Count > 0 ? $" unknown [{string.Join(", ", p.Unknown)}]" : string.Empty;
        return $"{(p.Name.Length > 0 ? p.Name : "?")} (type {p.ItemType} sub {p.SubType} unique {p.Unique} rarity {p.Rarity}{(p.Corrupted ? " corrupted" : string.Empty)}) "
            + $"affixes [{affixes}] implicits [{string.Join(",", p.Implicits)}] unique rolls [{string.Join(",", p.UniqueRolls)}]{(p.RollsFilled ? " rolls filled" : string.Empty)}{unknown}";
    }

    // planner tooltip <slot>|idol <x> <y>|blessing <n>|weaver <socket> [compare] [rarity <n>] [as <slot type>] [against hovered|carried [reverse]] | close:
    // the active stage's planned item, made as a new item in memory (never placed in a container, saved or sent) and shown
    // with the game's own item tooltip, at the equipped item when the inventory shows it. "against hovered" compares it
    // with the item behind the game's open tooltip; "reverse" shows that item with the planned one as the comparison.
    private static CommandResult Tooltip(CommandCall call)
    {
        var tooltips = Panel<TooltipItemManager>();
        if (!Game.Alive(tooltips)) return CommandResult.Ok("planner tooltip: no tooltip manager");
        string slot = call.Lower(2);
        if (slot == "close")
        {
            tooltips!.CloseTooltip();
            TooltipItemManager.ForceClose();
            return CommandResult.Ok("planner tooltip close: closed");
        }
        var types = Enum.GetNames(typeof(TooltipItemManager.SlotType));
        if (PlannedGear() is not { } gear) return CommandResult.Ok("planner tooltip: no active build");
        int next = 3;
        PlannedItem? planned;
        string what = slot;
        if (slot == "idol" && int.TryParse(call.Lower(3), out int x) && int.TryParse(call.Lower(4), out int y))
        {
            planned = gear.Idols.Find(i => i.X == x && i.Y == y);
            what = $"idol {x} {y}";
            next = 5;
        }
        else if (slot == "blessing" && int.TryParse(call.Lower(3), out int timeline))
        {
            planned = gear.Blessings.TryGetValue(timeline, out var b) ? b : null;
            what = $"blessing {timeline}";
            next = 4;
        }
        else if (slot == "weaver" && int.TryParse(call.Lower(3), out int node))
        {
            planned = gear.WeaverItems.TryGetValue(node, out var w) ? w : null;
            what = $"weaver {node}";
            next = 4;
        }
        else
        {
            planned = gear.Slots.FirstOrDefault(p => p.Key.Equals(slot, StringComparison.OrdinalIgnoreCase)).Value;
        }
        if (planned == null)
            return CommandResult.Ok($"planner tooltip {what}: nothing planned; slots {string.Join(" ", gear.Slots.Keys)}; idols {string.Join(" ", gear.Idols.Select(i => $"{i.X},{i.Y}"))}; "
                + $"blessings {string.Join(" ", gear.Blessings.Keys)}; weaver {string.Join(" ", gear.WeaverItems.Keys)}; slot types {string.Join(" ", types)}");

        byte? rarity = null;
        string typeName = planned.Place switch
        {
            PlannedPlace.Blessing => "BLESSING",
            PlannedPlace.WeaverNode => nameof(TooltipItemManager.SlotType.WEAVER_TREE_NODE),
            _ => types.FirstOrDefault(t => t.Contains("Equip", StringComparison.OrdinalIgnoreCase)) ?? types[0],
        };
        bool compare = false, against = false, reverse = false, carried = false, noShow = false;
        Vector3? at = null;
        for (int i = next; i <= call.Args.Length; i++)
        {
            string word = call.Lower(i);
            if (word == "compare") compare = true;
            else if (word == "rarity" && byte.TryParse(call.Lower(++i), out byte r)) rarity = r;
            else if (word == "as") typeName = types.FirstOrDefault(t => t.Equals(call.Lower(++i), StringComparison.OrdinalIgnoreCase)) ?? typeName;
            else if (word == "against" && call.Lower(i + 1) == "hovered") { against = true; i++; }
            else if (word == "against" && call.Lower(i + 1) == "carried") { against = carried = true; i++; }
            else if (word == "reverse") reverse = true;
            else if (word == "noshow") noShow = true;
            else if (word == "at" && float.TryParse(call.Lower(i + 1), NumberStyles.Float, s_inv, out float ax) && float.TryParse(call.Lower(i + 2), NumberStyles.Float, s_inv, out float ay))
            {
                // Game-window pixels from the top left, as the input steps take them.
                at = new Vector3(ax, Screen.height - ay, 0f);
                i += 2;
            }
        }

        // The hovered item, read before our tooltip replaces the game's.
        var tip = UITooltipItem.instance;
        // The item behind the game's open tooltip is its manager's active item; UITooltipItem.cachedItem is the equipped side.
        ItemDataUnpacked? hovered = against && !carried && Panel<TooltipItemManager>() is { } open && Game.Alive(open) ? open.ActiveItem : null;
        if (carried)
        {
            var cursor = ItemContainersManager.Instance?.cursor?.TryCast<IItemContainer>()?.GetContent();
            hovered = cursor != null && cursor.Count > 0 ? cursor[0]?.data?.getAsUnpacked() : null;
        }
        if (against && hovered == null) return CommandResult.Ok($"planner tooltip {what}: no {(carried ? "carried item" : "item tooltip open")} to compare against");

        var item = Planner.Make(planned, out string? refused, rarity);
        string refresh = refused == null ? "refreshed" : $"refresh failed ({refused})";
        var opener = at != null ? null : planned.Place switch
        {
            PlannedPlace.Slot => EquippedOpener(planned.Slot),
            PlannedPlace.Idol => IdolOpener(planned.X, planned.Y),
            PlannedPlace.WeaverNode => SocketOpener(planned.Socket),
            _ => BlessingOpener(planned.Timeline),
        };
        var size = opener != null ? opener.GetComponent<RectTransform>()?.rect.size ?? Vector2.zero : Vector2.zero;
        var position = at ?? (opener != null ? opener.transform.position : new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
        var type = Enum.Parse<TooltipItemManager.SlotType>(typeName);
        string comparison = string.Empty;
        if (against)
        {
            var (main, other) = reverse ? (hovered!, item) : (item, hovered!);
            tooltips!.OpenItemTooltip(main, TooltipItemManager.SlotType.INVENTORY, Vector2.zero, position, opener, size);
            var parameters = tooltips.activeParameters;
            bool set = parameters != null && tooltips.SetEquippedItemTextForComparison(other, parameters);
            if (!noShow && tooltips.activeParameters is { } again) tooltips.ShowComparison(again);
            comparison = $", against hovered {hovered!.FullName}{(reverse ? " (reversed)" : string.Empty)}: comparison set {set}, parameters {(parameters != null ? "yes" : "none")}, comparison enabled {tooltips.IsComparisonEnabled}";
        }
        else
        {
            tooltips!.OpenItemTooltip(item, type, Vector2.zero, position, opener, size);
            // A socket has no equipped item: its socketed one is the comparison.
            var socketed = planned.Place == PlannedPlace.WeaverNode ? Planner.SocketContainer(planned.Socket)?.GetContent() : null;
            if (compare && socketed is { Count: > 0 } && socketed[0]?.data?.getAsUnpacked() is { } inSocket && tooltips.activeParameters is { } p)
                comparison = $", against socketed {inSocket.FullName}: comparison set {tooltips.SetEquippedItemTextForComparison(inSocket, p)}";
            if (compare && tooltips.activeParameters is { } shown) tooltips.ShowComparison(shown);
        }
        return CommandResult.Ok($"planner tooltip {what}: {item.FullName} {PlannedText(planned)} id {item.id?.Length ?? 0} implicits {item.implicitRolls?.Length ?? 0} of {ItemData.numberOfImplicitRolls} "
            + $"two-handed {ItemData.IsTwoHanded(item)}, {refresh}, as {(against ? "INVENTORY" : typeName)}, at {(opener != null ? opener.name : "screen centre")}, ready {tooltips.IsTooltipReady}, "
            + $"active item {(tooltips.ActiveItem?.Pointer == item.Pointer ? "ours" : "other")}, rect {(Game.Alive(tooltips.ActiveTooltipRect) ? Rect(tooltips.ActiveTooltipRect.transform) : "none")}{comparison}");
    }

    // planner cost: the planner's own frame time (its per-frame update, where the inventory icons, hover and gear read run).
    private static CommandResult Cost()
    {
        if (FeatureHost.Find("planner") is not { } f) return CommandResult.Ok("planner cost: no planner feature");
        var frames = f.Record.Frames;
        return CommandResult.Ok($"planner cost: avg {frames.AverageMs:F3} ms, worst {frames.WorstMs:F3} ms, last second {frames.LastSecondMs:F3} ms, budget {f.BudgetMs:F2} ms; "
            + $"inventory {(UIBase.instance?.IsInventoryOpen() == true ? "open" : "closed")}, marks {Planner.Marks.Count}");
    }

    // planner itemcheck <build id>: every planned item of every stage made as a game item: its name, and any the game
    // refuses or this version lacks (base, unique, affix). Reading only; nothing is shown or placed.
    private static CommandResult ItemCheck(CommandCall call)
    {
        string id = call.Args.Length >= 2 ? call.Args[1] : string.Empty;
        var build = Planner.Library.Find(id);
        if (build == null) return CommandResult.Ok($"planner itemcheck: no build {id}");
        var lines = new List<string>();
        int made = 0, refused = 0, unknown = 0;
        for (int stage = 0; stage < build.Stages.Count; stage++)
        {
            var gear = Planner.GearOf(build, stage);
            var all = gear.Slots.Values.Concat(gear.Idols).Concat(gear.Blessings.Values).Concat(gear.WeaverItems.Values);
            foreach (var p in all)
            {
                string where = p.Place switch { PlannedPlace.Slot => p.Slot, PlannedPlace.Idol => $"idol {p.X},{p.Y}", PlannedPlace.WeaverNode => $"weaver {p.Socket}", _ => $"blessing {p.Timeline}" };
                string missing = p.Unknown.Count > 0 ? $" UNKNOWN [{string.Join(", ", p.Unknown)}]" : string.Empty;
                if (p.Unknown.Count > 0) unknown++;
                if (p.Unmakeable && p.Unknown.Contains("base") || p.Unknown.Contains("unique"))
                {
                    lines.Add($"  stage {stage + 1} {where}: not made{missing}");
                    continue;
                }
                var item = Planner.Make(p, out string? refusal);
                if (refusal != null) { refused++; lines.Add($"  stage {stage + 1} {where}: REFUSED {refusal}{missing}"); continue; }
                made++;
                string affixes = string.Join(", ", p.Affixes.Select(a => $"{a.Id} T{a.Tier + 1}"));
                lines.Add($"  stage {stage + 1} {where}: {item.FullName} (type {p.ItemType} sub {p.SubType} unique {p.Unique} rarity {p.Rarity}) [{affixes}]{missing}");
            }
        }
        lines.Insert(0, $"ok planner itemcheck {build.Id} \"{build.Name}\": {build.Stages.Count} stages, {made} made, {refused} refused, {unknown} with unknown ids");
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // planner slots: each paper doll slot's own element (shown even when empty) and the idol grid's, with their screen
    // rects and children, and the idol grid's cell size and first cells' screen points.
    private static CommandResult Slots()
    {
        var manager = ItemContainersManager.Instance;
        if (!Game.Alive(manager)) return CommandResult.Ok("planner slots: no item containers");
        var lines = new List<string> { $"ok planner slots: inventory {(UIBase.instance?.IsInventoryOpen() == true ? "open" : "closed")}" };
        foreach (var (slot, _, _) in Planner.GearSlots)
        {
            var box = Planner.SlotContainer(slot);
            var ui = box != null ? ContainerUi(box) : null;
            lines.Add($"  {slot}: {(ui != null ? $"{ui.name} parent {ui.transform.parent?.name} rect {Rect(ui.transform)} children {Children(ui.transform)}" : "no element")}");
        }
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<ItemContainerUI>(), false))
        {
            var ui = obj.TryCast<ItemContainerUI>();
            if (!Game.Alive(ui) || !ui!.gameObject.activeInHierarchy) continue;
            var box = ui.Container;
            lines.Add($"  ui {ui.name} < {ui.transform.parent?.name}: {(box != null ? box.TryCast<Il2CppSystem.Object>()?.GetIl2CppType().Name : "no container")} rect {Rect(ui.transform)}");
        }
        var idols = manager!.idols?.TryCast<IItemContainer>();
        var grid = idols != null ? ContainerUi(idols) : null;
        string CanvasText(Transform? t)
        {
            var c = t != null ? t.GetComponentInParent<Canvas>() : null;
            return Game.Alive(c) ? $"{c!.name} order {c.sortingOrder} layer {c.sortingLayerName} override {c.overrideSorting}, root {c.rootCanvas.name} order {c.rootCanvas.sortingOrder} mode {c.rootCanvas.renderMode}" : "none";
        }
        lines.Add($"  canvas: idols {CanvasText(grid?.transform)}; helmet {CanvasText(ContainerUi(Planner.SlotContainer("helmet")!)?.transform)}; tooltip {CanvasText(Panel<TooltipItemManager>()?.itemTooltip?.transform)}");
        if (grid != null && grid.GetComponent<ItemContainerUI>() is { } gridUi)
        {
            lines.Add($"  idols: {grid.name} parent {grid.transform.parent?.name} rect {Rect(grid.transform)} slot size {gridUi.GetSlotSize().x.ToString("0.#", s_inv)}x{gridUi.GetSlotSize().y.ToString("0.#", s_inv)}");
            for (int y = 0; y < 5; y++)
            {
                var cells = new List<string>();
                for (int x = 0; x < 5; x++)
                {
                    var p = gridUi.GetSlotPositionInScreenSpace(new Vector2Int(x, y));
                    cells.Add($"{x},{y}@{p.x.ToString("0", s_inv)},{p.y.ToString("0", s_inv)}");
                }
                lines.Add($"    {string.Join(" ", cells)}");
            }
        }
        else
        {
            lines.Add("  idols: no element");
        }
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // planner slotart <slot>: every image under a paper doll slot's element (and its parent's), with its sprite, type,
    // 9-slice border, colour and rect, to choose a native frame for the icons.
    private static CommandResult SlotArt(CommandCall call)
    {
        var box = Planner.SlotContainer(call.Lower(2));
        var element = box != null ? ContainerUi(box) : null;
        if (element == null) return CommandResult.Ok("planner slotart: no element (inventory closed or no such slot)");
        var lines = new List<string> { $"ok planner slotart {call.Lower(2)}: {element.name} parent {element.transform.parent?.name}" };
        var root = element.transform.parent ?? element.transform;
        foreach (var obj in root.GetComponentsInChildren<UnityEngine.UI.Image>(true))
        {
            if (!Game.Alive(obj)) continue;
            var sprite = obj.sprite;
            string path = obj.transform == root ? obj.name : $"{obj.transform.parent?.name}/{obj.name}";
            lines.Add($"  {path} active {obj.gameObject.activeInHierarchy} sprite {(Game.Alive(sprite) ? $"{sprite!.name} {sprite.rect.width}x{sprite.rect.height} border {sprite.border.x},{sprite.border.y},{sprite.border.z},{sprite.border.w}" : "none")} type {obj.type} colour #{ColorUtility.ToHtmlStringRGBA(obj.color)} rect {Rect(obj.transform)}");
        }
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // planner sprites <regex> [max px]: loaded sprites whose name matches, no bigger than max px a side (default 128),
    // with their size; full list in the reply file.
    private static CommandResult SpriteList(CommandCall call)
    {
        var pattern = new System.Text.RegularExpressions.Regex(call.Lower(2), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int max = int.TryParse(call.Lower(3), out int m) ? m : 128;
        var names = new SortedSet<string>();
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
        {
            var sprite = obj.TryCast<Sprite>();
            if (!Game.Alive(sprite) || !pattern.IsMatch(sprite!.name)) continue;
            var r = sprite.rect;
            if (r.width > max || r.height > max) continue;
            names.Add($"{sprite.name} {r.width}x{r.height}");
        }
        var lines = new List<string> { $"ok planner sprites {call.Lower(2)}: {names.Count}" };
        lines.AddRange(names.Select(n => "  " + n));
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // planner sprite <name>: saves a loaded sprite's own pixels as dev/shots/sprite-<name>.png, to look at game art.
    private static CommandResult SpriteSave(CommandCall call)
    {
        // Arguments start after "planner sprite".
        string name = string.Join(" ", call.Args.Skip(1));
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
        {
            var sprite = obj.TryCast<Sprite>();
            if (!Game.Alive(sprite) || sprite!.name != name) continue;
            var area = sprite.textureRect;
            int w = Mathf.Max(1, Mathf.RoundToInt(area.width)), h = Mathf.Max(1, Mathf.RoundToInt(area.height));
            var before = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                var source = sprite.texture;
                Graphics.Blit(source, rt, new Vector2(area.width / source.width, area.height / source.height), new Vector2(area.x / source.width, area.y / source.height));
                RenderTexture.active = rt;
                var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                texture.Apply();
                string path = Path.Combine(DevBridge.Dir, "shots", $"sprite-{name}.png");
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(texture).ToArray());
                UnityEngine.Object.Destroy(texture);
                return CommandResult.Ok($"planner sprite {name}: {w}x{h} border {sprite.border} saved {path}");
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        return CommandResult.Ok($"planner sprite {name}: not loaded");
    }

    // planner uitree <name>: the first active object with that name, its parents, and every image under it (sprite, size,
    // rect), to find game UI pieces.
    private static CommandResult UiTree(CommandCall call)
    {
        string name = string.Join(" ", call.Args.Skip(1));
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<RectTransform>(), false))
        {
            var t = obj.TryCast<RectTransform>();
            if (!Game.Alive(t) || t!.name != name) continue;
            var path = new List<string>();
            for (var up = t.parent; up != null && path.Count < 5; up = up.parent) path.Add(up.name);
            var lines = new List<string> { $"ok planner uitree {name}: in {string.Join(" < ", path)} rect {Rect(t)} components {string.Join(",", t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name))}" };
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                lines.Add($"  child {child.name} active {child.gameObject.activeInHierarchy} rect {Rect(child)}");
            }
            foreach (var image in t.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                lines.Add($"  {image.transform.parent?.name}/{image.name} active {image.gameObject.activeInHierarchy} sprite {(Game.Alive(image.sprite) ? $"{image.sprite!.name} {image.sprite.rect.width}x{image.sprite.rect.height}" : "none")} type {image.type} rect {Rect(image.transform)}");
            return new CommandResult(DevOutput.Reply("planner", lines));
        }
        return CommandResult.Ok($"planner uitree {name}: none active");
    }

    // planner hintspot: what the hint card's placement sees of the game's tooltip, and every tooltip panel on screen.
    private static CommandResult HintSpot() =>
        CommandResult.Ok($"planner hintspot: shown {EpochAssist.Ui.GameTooltips.Shown()?.ToString() ?? "none"} panels {EpochAssist.Ui.GameTooltips.Panels?.ToString() ?? "none"} "
            + $"top {EpochAssist.Ui.GameTooltips.Top?.ToString() ?? "none"}; menu hover: {Planner.MenuHoverText}; {TooltipCanvases()}");

    // Every active tooltip canvas and its Content panels with their rects.
    private static string TooltipCanvases()
    {
        var parts = new List<string>();
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>(), false))
        {
            var canvas = obj.TryCast<Canvas>();
            if (!Game.Alive(canvas) || !canvas!.name.Contains("Tooltip", StringComparison.Ordinal)) continue;
            for (int i = 0; i < canvas.transform.childCount; i++)
            {
                var child = canvas.transform.GetChild(i);
                if (child.name.EndsWith("Content", StringComparison.Ordinal) && child.gameObject.activeInHierarchy)
                    parts.Add($"{canvas.name}/{child.name} {Rect(child)}");
            }
        }
        return string.Join("; ", parts);
    }

    // planner nodeart <id>: every image under the open tree's node with that id (sprite, size, active, rect).
    private static CommandResult NodeArt(CommandCall call)
    {
        var (tree, source) = OpenTree();
        if (tree == null || !byte.TryParse(call.Lower(2), out byte id)) return CommandResult.Ok("planner nodeart: no open tree or no id");
        var nodes = tree.nodeList;
        for (int i = 0; nodes != null && i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (!Game.Alive(node) || node!.id != id) continue;
            var lines = new List<string> { $"ok planner nodeart {id} in {source}: {node.name} rect {Rect(node.transform)}" };
            foreach (var image in node.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                lines.Add($"  {image.transform.parent?.name}/{image.name} active {image.gameObject.activeInHierarchy} enabled {image.enabled} sprite {(Game.Alive(image.sprite) ? $"{image.sprite!.name} {image.sprite.rect.width}x{image.sprite.rect.height}" : "none")} type {image.type} colour #{ColorUtility.ToHtmlStringRGBA(image.color)} rect {Rect(image.transform)}");
            return new CommandResult(DevOutput.Reply("planner", lines));
        }
        return CommandResult.Ok($"planner nodeart {id}: not in {source}");
    }

    private static string Children(Transform t)
    {
        var names = new List<string>();
        for (int i = 0; i < t.childCount && i < 8; i++) names.Add(t.GetChild(i).name);
        return string.Join(",", names);
    }

    // planner carried: the item the game holds on the cursor, and where it was picked up from.
    private static CommandResult Carried()
    {
        var manager = ItemContainersManager.Instance;
        if (!Game.Alive(manager) || manager!.cursor == null) return CommandResult.Ok("planner carried: no item containers");
        var lines = new List<string> { $"ok planner carried: from {manager.CursorPickedFromContainer} at {manager.CursorPickedFromPos.x},{manager.CursorPickedFromPos.y}, locked {manager.CursorLocked}" };
        AddEntries(lines, "cursor", manager.cursor.TryCast<IItemContainer>());
        var tip = UITooltipItem.instance;
        lines.Add($"  item tooltip: {(Game.Alive(tip) && tip!.tooltipActive ? tip.cachedItem?.getAsUnpacked()?.FullName ?? "open, no item" : "closed")}");
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // planner tooltipcheck <n>: opens and closes the stage's planned items n times in all, and compares the character's
    // items, health and the game's tooltip state before and after. Reading only.
    private static CommandResult TooltipCheck(CommandCall call)
    {
        var tooltips = Panel<TooltipItemManager>();
        if (!Game.Alive(tooltips)) return CommandResult.Ok("planner tooltipcheck: no tooltip manager");
        if (PlannedGear() is not { } gear) return CommandResult.Ok("planner tooltipcheck: no active build");
        int count = int.TryParse(call.Lower(2), out int n) ? Math.Clamp(n, 1, 1000) : 100;
        var planned = gear.Slots.Values.Concat(gear.Idols).Concat(gear.Blessings.Values).Concat(gear.WeaverItems.Values).Where(p => !p.Unmakeable).ToList();
        if (planned.Count == 0) return CommandResult.Ok("planner tooltipcheck: nothing planned to show");
        string before = CharacterState(), tipBefore = TooltipState(tooltips!);
        int opened = 0;
        for (int i = 0; i < count; i++)
        {
            var p = planned[i % planned.Count];
            if (Planner.Made(p) is not { } item) continue;
            tooltips!.OpenItemTooltip(item, p.Place == PlannedPlace.Blessing ? TooltipItemManager.SlotType.BLESSING : TooltipItemManager.SlotType.INVENTORY,
                Vector2.zero, new Vector3(Screen.width / 2f, Screen.height / 2f, 0f), null, Vector2.zero);
            tooltips.CloseTooltip();
            opened++;
        }
        string after = CharacterState(), tipAfter = TooltipState(tooltips!);
        bool same = before == after;
        return CommandResult.Ok($"planner tooltipcheck: opened and closed {opened} of {count} ({planned.Count} items); character {(same ? "unchanged" : "CHANGED")} "
            + $"({before.Length} chars, hash {before.GetHashCode():X8} → {after.GetHashCode():X8}); tooltip {tipBefore} → {tipAfter}");
    }

    // Every item the character holds, as id bytes by container and place, and its health.
    private static string CharacterState()
    {
        var manager = ItemContainersManager.Instance;
        var sb = new System.Text.StringBuilder();
        void Box(string label, IItemContainer? box)
        {
            var content = box?.GetContent();
            sb.Append(label).Append(':');
            for (int i = 0; content != null && i < content.Count; i++)
            {
                var entry = content[i];
                if (entry?.data?.id is not { } id) continue;
                sb.Append(entry.Position.x).Append(',').Append(entry.Position.y).Append('=').Append(Convert.ToHexString(id.ToArray())).Append(';');
            }
            sb.Append('|');
        }
        if (Game.Alive(manager))
        {
            foreach (var (slot, _, _) in Planner.GearSlots) Box(slot, Planner.SlotContainer(slot));
            Box("idols", manager!.idols?.TryCast<IItemContainer>());
            Box("inventory", manager.inventory?.TryCast<IItemContainer>());
            Box("cursor", manager.cursor?.TryCast<IItemContainer>());
            var boxes = manager.blessings?.Containers;
            for (int i = 0; boxes != null && i < boxes.Count; i++) Box($"blessing {i}", boxes[i]?.TryCast<IItemContainer>());
        }
        var health = Player.Health;
        sb.Append("health ").Append(Game.Alive(health) ? health!.maxHealth : -1);
        return sb.ToString();
    }

    private static string TooltipState(TooltipItemManager tooltips) =>
        $"active {tooltips.active} item {(tooltips.ActiveItem != null ? "set" : "none")} game tooltip {(UITooltipItem.instance?.tooltipActive == true ? "open" : "closed")}";

    // The equipped item's inventory icon for a paper doll slot while the inventory shows it, else the slot's own element.
    private static GameObject? EquippedOpener(string slot)
    {
        var box = Planner.SlotContainer(slot);
        var content = box?.GetContent();
        if (content != null && content.Count > 0 && content[0] is { } entry && EntryUi(entry) is { } ui) return ui;
        return box != null ? ContainerUi(box) : null;
    }

    private static GameObject? IdolOpener(int x, int y)
    {
        var content = ItemContainersManager.Instance?.idols?.TryCast<IItemContainer>()?.GetContent();
        for (int i = 0; content != null && i < content.Count; i++)
            if (content[i] is { } entry && entry.Position.x == x && entry.Position.y == y) return EntryUi(entry);
        return null;
    }

    // The weaver tree's element for a socket (by container id), while it shows.
    private static GameObject? SocketOpener(int socket)
    {
        if (!Planner.WeaverSockets.TryGetValue(socket, out byte node)) return null;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppLE.Factions.WeaverTreeItemContainerUI>(), false))
        {
            var ui = obj.TryCast<Il2CppLE.Factions.WeaverTreeItemContainerUI>();
            if (Game.Alive(ui) && ui!.gameObject.activeInHierarchy && ui.LinkedNodeID == node) return ui.gameObject;
        }
        return null;
    }

    private static GameObject? BlessingOpener(int timeline)
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<InventoryBlessingSlotUI>(), false))
        {
            var ui = obj.TryCast<InventoryBlessingSlotUI>();
            if (Game.Alive(ui) && ui!.gameObject.activeInHierarchy && SlotTimeline(ui) == timeline + 1) return ui.gameObject;
        }
        return null;
    }

    // The game's timeline number of a blessing slot (from 1), or -1.
    private static int SlotTimeline(InventoryBlessingSlotUI ui) =>
        ui.blessingUIContainer?.Container?.TryCast<BlessingContainer>() is { } box ? (int)box.TimelineID : -1;

    private static GameObject? EntryUi(ItemContainerEntry entry)
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<InventoryItemUI>(), false))
        {
            var ui = obj.TryCast<InventoryItemUI>();
            if (Game.Alive(ui) && ui!.gameObject.activeInHierarchy && ui.EntryRef != null && ui.EntryRef.Pointer == entry.Pointer) return ui.gameObject;
        }
        return null;
    }

    // The container's own element (shown even when empty).
    private static GameObject? ContainerUi(IItemContainer box)
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<ItemContainerUI>(), false))
        {
            var ui = obj.TryCast<ItemContainerUI>();
            if (Game.Alive(ui) && ui!.gameObject.activeInHierarchy && ui.Container != null && ui.Container.Pointer == box.Pointer) return ui.gameObject;
        }
        return null;
    }
    // One line per item in a container: position, size, name, base, unique, rarity and affixes; with the item's
    // inventory icon on screen, its rect.
    private static void AddEntries(List<string> lines, string label, IItemContainer? box)
    {
        if (box == null) { lines.Add($"  {label}: none"); return; }
        var content = box.GetContent();
        if (content == null || content.Count == 0) { lines.Add($"  {label}: empty"); return; }
        for (int i = 0; i < content.Count; i++)
        {
            var entry = content[i];
            var data = entry?.data;
            if (data == null) continue;
            var item = data.getAsUnpacked();
            var affixes = new List<string>();
            for (int a = 0; data.affixes != null && a < data.affixes.Count; a++)
            {
                var affix = data.affixes[a];
                affixes.Add($"{affix.affixId}/t{affix.affixTier}/r{affix.affixRoll}");
            }
            lines.Add($"  {label} at {entry!.Position.x},{entry.Position.y} size {entry.size.x}x{entry.size.y}: {item?.FullName ?? "?"} (type {data.itemType} sub {data.subType} unique {data.uniqueID} rarity {data.rarity}) affixes [{string.Join(" ", affixes)}] id {data.id?.Length ?? 0} implicits {data.implicitRolls?.Length ?? 0}{ItemRect(entry)}");
        }
    }

    private static string ItemRect(ItemContainerEntry entry)
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<InventoryItemUI>(), false))
        {
            var ui = obj.TryCast<InventoryItemUI>();
            if (Game.Alive(ui) && ui!.gameObject.activeInHierarchy && ui.EntryRef != null && ui.EntryRef.Pointer == entry.Pointer)
                return $" rect {Rect(ui.transform)}";
        }
        return string.Empty;
    }

    private static void AddIcons(List<string> lines, string group, Il2CppSystem.Collections.Generic.List<AbilityPanelIcon>? icons)
    {
        for (int i = 0; icons != null && i < icons.Count; i++)
        {
            var icon = icons[i];
            if (Game.Alive(icon)) lines.Add($"  {group} {AbilityText(icon!.ability)} {(icon.gameObject.activeInHierarchy ? "shown" : "hidden")} rect {Rect(icon.transform)}");
        }
    }

    private static string AbilityText(Ability? ability) => Game.Alive(ability) ? $"{ability!.playerAbilityID} {ability.abilityName}" : "none";

    // Screen rect, top-left origin: x,y wxh.
    // An element's name with its parents', up to three.
    private static string ElementPath(Transform t)
    {
        var names = new List<string>();
        for (var at = t; at != null && names.Count < 4; at = at.parent) names.Insert(0, at.name);
        return string.Join("/", names);
    }

    private static string Rect(Transform t)
    {
        var rt = t.TryCast<RectTransform>();
        if (rt == null) return "none";
        var corners = new Il2CppStructArray<Vector3>(4);
        rt.GetWorldCorners(corners);
        var canvas = t.GetComponentInParent<Canvas>();
        var root = Game.Alive(canvas) ? canvas!.rootCanvas : null;
        Camera? camera = Game.Alive(root) && root!.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
        Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]), high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return $"{F(low.x)},{F(Screen.height - high.y)} {F(high.x - low.x)}x{F(high.y - low.y)}";
    }

    // ---- watch ----

    private static CommandResult WatchSwitch(CommandCall call)
    {
        switch (call.Lower(2))
        {
            case "on":
                if (FeatureHost.Find("dev") is not { State: FeatureState.Running } dev) return CommandResult.Error("planner watch: the dev bridge isn't running");
                s_watch?.Cancel();
                s_watchLast = string.Empty;
                s_watch = Scheduler.Spread(dev, "planner watch", () => 1d, () =>
                {
                    WatchStep();
                    return false;
                });
                return CommandResult.Ok("planner watch on: panel and allocation changes go to the log");
            case "off":
                s_watch?.Cancel();
                s_watch = null;
                return CommandResult.Ok("planner watch off");
            default:
                return CommandResult.Error($"planner: usage {Usage}");
        }
    }

    // Once a frame: the open panels and the allocation signature, logged with the frame number when either changes.
    private static void WatchStep()
    {
        string now = $"open {OpenPanels()}; {Signature()}";
        if (now == s_watchLast) return;
        s_watchLast = now;
        Log.Info($"planner watch: f={Time.frameCount} t={Clock.Now.ToString("F3", s_inv)} {now}");
    }

    private static string Signature()
    {
        var tree = Game.LocalTree;
        if (tree == null) return "no tree data";
        int count = 0, sum = 0;
        var nodes = tree.passiveTree?.nodes;
        for (int i = 0; nodes != null && i < nodes.Count; i++)
        {
            if (nodes[i] == null || nodes[i].pointsAllocated == 0) continue;
            count++;
            sum += nodes[i].pointsAllocated;
        }
        var specialised = new List<string>();
        var skills = tree.specialisedSkillTrees;
        for (int i = 0; skills != null && i < skills.Count; i++)
        {
            var skill = skills[i];
            if (skill == null) continue;
            int points = 0;
            for (int j = 0; skill.nodes != null && j < skill.nodes.Count; j++) points += skill.nodes[j]?.pointsAllocated ?? 0;
            specialised.Add($"{skill.slot}:{skill.treeID}={points}");
        }
        var (open, _) = OpenTree();
        int shown = 0;
        var list = Game.Alive(open) ? open!.nodeList : null;
        for (int i = 0; list != null && i < list.Count; i++) shown += Game.Alive(list[i]) ? list[i].pointsAllocated : 0;
        return $"passive {count} nodes {sum} points, mastery {tree.chosenMastery}, specialised [{string.Join(" ", specialised)}], shown tree {(Game.Alive(open) ? $"{open!.treeID} {shown}" : "none")}";
    }

    // ---- fixtures ----

    private static string FixturePath(string name) => Path.IsPathRooted(name) ? name : Path.Combine(FixtureDir, name);

    // A fixture by its folder (maxroll/ or let/), then by its content.
    private static Parsed ParseFixture(string path, string text)
    {
        var now = DateTime.Now;
        string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty).ToLowerInvariant();
        var root = MaxrollFormat.Parse(text) as JsonObject;
        bool let = folder == "let" || (folder != "maxroll" && (root?["data"] is JsonObject || root?.ContainsKey("code") == true));
        if (let) return LetFormat.Parse(text, null, null, now);
        if (root?["data"] is JsonValue || root?.ContainsKey("error") == true) return MaxrollFormat.ParseLink(text, null, now);
        return MaxrollFormat.ParseExport(text, now);
    }

    private static CommandResult Import(CommandCall call)
    {
        string name = call.Rest(2).Trim();
        if (name.Length == 0) return CommandResult.Error($"planner: usage {Usage}");
        string path = FixturePath(name);
        if (!File.Exists(path)) return CommandResult.Error($"planner import: no file {path}");
        var parsed = ParseFixture(path, File.ReadAllText(path));
        if (parsed.Build is not { } build) return CommandResult.Ok($"planner import {name}: {parsed.Error}");
        Planner.ForgetResolution(build.Id);
        s_build = build;
        var lines = new List<string> { $"ok planner import {name}: {build.Id} \"{build.Name}\" by {build.Author ?? "-"}, game {build.GameVersion ?? "-"}, {build.Stages.Count} stages, first {build.FirstStage + 1}" };
        lines.Add("library: " + Planner.Add(build));
        // Resolved first, so Maxroll's skill names show with the ids they matched.
        var resolution = Planner.Resolve(build);
        lines.AddRange(StageLines(build));
        lines.AddRange(ResolutionLines(resolution));
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    private static IEnumerable<string> StageLines(Build build)
    {
        foreach (var stage in build.Stages)
        {
            yield return $"stage \"{stage.Name}\": class {stage.ClassId} mastery {stage.Mastery}, passive {stage.Passive.TreeId} {stage.Passive.Points.Count} nodes {stage.Passive.Total} points, "
                + $"order {(stage.Passive.Order is { } o ? $"{o.Count} steps" : "none")}, bar [{string.Join(", ", stage.Bar.Select(b => b ?? "-"))}]{(stage.SlotsInferred ? ", slots inferred" : string.Empty)}";
            foreach (var skill in stage.Skills)
                yield return $"  slot {skill.Slot} {(skill.AbilityId.Length > 0 ? skill.AbilityId : "?")} {skill.Name}: {skill.Tree.Points.Count} nodes {skill.Tree.Total} points, order {(skill.Tree.Order is { } so ? $"{so.Count} steps" : "none")}";
            if (stage.OtherTrees.Count > 0) yield return $"  other trees: {string.Join(", ", stage.OtherTrees.Select(t => $"{t.TreeId} {t.Total}"))}";
        }
    }

    private static IEnumerable<string> ResolutionLines(BuildResolution r)
    {
        if (r.Unread)
        {
            yield return "resolve: game data unread";
            yield break;
        }
        yield return $"resolve: {r.UnknownNodes.Count} unknown nodes, {r.UnknownSkills.Count} unknown skills, {r.CappedNodes.Count} capped";
        if (r.UnknownNodes.Count > 0) yield return "  unknown nodes: " + string.Join(" ", r.UnknownNodes.Select(n => $"{n.TreeId}/{n.Node}"));
        if (r.UnknownSkills.Count > 0) yield return "  unknown skills: " + string.Join(", ", r.UnknownSkills);
        if (r.CappedNodes.Count > 0) yield return "  capped: " + string.Join(" ", r.CappedNodes.Select(n => $"{n.TreeId}/{n.Node} {n.Planned}>{n.Max}"));
    }

    private static CommandResult Check()
    {
        var lines = new List<string>();
        int failed = 0;
        foreach (var (id, digits) in s_pairs)
        {
            var (tag, got) = LzString.DecodeLetId(id);
            if (got == digits) continue;
            failed++;
            lines.Add($"decoder {id}: got {tag}{got ?? "null"}, want {digits}");
        }
        int files = 0;
        if (Directory.Exists(FixtureDir))
        {
            foreach (string path in Directory.GetFiles(FixtureDir, "*.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                files++;
                string rel = Path.GetRelativePath(FixtureDir, path);
                string text = File.ReadAllText(path);
                var parsed = ParseFixture(path, text);
                if (parsed.Build is not { } build)
                {
                    // The saved not-found bodies are meant to fail.
                    if (parsed.Error == Parsed.NotFound) lines.Add($"{rel}: not found (as saved)");
                    else
                    {
                        failed++;
                        lines.Add($"{rel}: {parsed.Error}");
                    }
                    continue;
                }
                string stored = build.ToJson().ToJsonString();
                var back = Build.FromJson(build.Id, (JsonObject)JsonNode.Parse(stored)!);
                if (back == null || back.ToJson().ToJsonString() != stored)
                {
                    failed++;
                    lines.Add($"{rel}: the stored form doesn't read back the same");
                }
                var (ids, bad) = LetIds(build.Source == LetFormat.Source ? MaxrollFormat.Parse(text) : null);
                if (bad.Count > 0)
                {
                    failed++;
                    lines.Add($"{rel}: {bad.Count} of {ids} ids don't decode: {string.Join(" ", bad.Take(5))}");
                }
                lines.Add($"{rel}: {build.Stages.Count} stages, {build.Stages.Sum(s => s.Passive.Total)} passive points, {build.Stages.Sum(s => s.Skills.Count)} skills{(ids > 0 ? $", {ids} ids decoded" : string.Empty)}");
            }
        }
        lines.Insert(0, $"{(failed == 0 ? "ok" : "error")} planner check: {s_pairs.Length} decoder pairs, {files} fixtures in {FixtureDir}, {failed} failed");
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // Every item, affix and set id in a Last Epoch Tools build: each must decode to digits.
    private static (int Count, List<string> Bad) LetIds(JsonNode? root)
    {
        int count = 0;
        var bad = new List<string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key == "id" && Build.Str(value) is string id && id.Length > 1 && char.IsUpper(id[0]))
                        {
                            count++;
                            if (LzString.DecodeLetId(id).Digits == null) bad.Add(id);
                        }
                        else Walk(value);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a) Walk(item);
                    break;
            }
        }
        Walk(root?["data"]);
        return (count, bad);
    }

    private static CommandResult ResolveCommand()
    {
        if (s_build is not { } build) return CommandResult.Ok("planner resolve: no build; planner import <file> or planner paste <link> first");
        Planner.ForgetResolution(build.Id);
        var lines = new List<string> { $"ok planner resolve {build.Id}: game {build.GameVersion ?? "-"}" };
        lines.AddRange(ResolutionLines(Planner.Resolve(build)));
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    // planner show [stage]: one stage of the last build with the game's node and skill names, for comparing with the
    // planner page by eye. Passive nodes are grouped by the mastery they need (0 is the base class).
    private static CommandResult Show(CommandCall call)
    {
        if (s_build is not { } build) return CommandResult.Ok("planner show: no build; planner import <file> first");
        int number = int.TryParse(call.Lower(2), out int n) ? n : build.FirstStage + 1;
        if (number < 1 || number > build.Stages.Count) return CommandResult.Error($"planner show: stage 1 to {build.Stages.Count}");
        Planner.Resolve(build);
        var stage = build.Stages[number - 1];
        var trees = GlobalTreeData.get();
        var lines = new List<string> { $"ok planner show {build.Id} stage {number} \"{stage.Name}\": class {stage.ClassId} mastery {stage.Mastery} level {(stage.Level > 0 ? stage.Level.ToString(s_inv) : "-")}",
            "levels: " + string.Join(", ", build.Stages.Select((st, i) => $"{i + 1} {(st.Level > 0 ? st.Level.ToString(s_inv) : "-")}")) };

        var passive = Game.Alive(trees) && stage.Passive.TreeId.Length > 0 ? trees!.getTreeData(stage.Passive.TreeId) : null;
        foreach (var group in stage.Passive.Points.GroupBy(p => passive?.getNode(p.Key)?.requiredMastery ?? 255).OrderBy(g => g.Key))
        {
            lines.Add($"passive, {(group.Key == 0 ? "base class" : group.Key == 255 ? "unknown" : $"mastery {group.Key}")}: {group.Sum(p => p.Value)} points");
            lines.AddRange(group.Select(p => "  " + NodeText(passive, p.Key, p.Value)));
        }
        if (stage.Passive.Order is { Count: > 0 } order)
            lines.Add("passive order, first 10: " + string.Join(", ", order.Take(10).Select(s => $"{NodeName(passive, s.Node)} +{s.Add}")));

        foreach (var skill in stage.Skills)
        {
            var data = Game.Alive(trees) && skill.AbilityId.Length > 0 ? trees!.getTreeData(skill.AbilityId) : null;
            lines.Add($"skill slot {skill.Slot + 1}: {SkillName(skill.AbilityId, skill.Name)}, {skill.Tree.Total} points");
            lines.AddRange(skill.Tree.Points.Select(p => "  " + NodeText(data, p.Key, p.Value)));
        }
        lines.Add("bar: " + string.Join(", ", stage.Bar.Select((id, i) => $"{i + 1} {(id == null ? "-" : SkillName(id, id))}")));
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    private static string NodeName(GlobalTreeData.TreeData? tree, byte node) => tree?.getNode(node)?.name is { Length: > 0 } name ? name : $"node {node}";

    private static string NodeText(GlobalTreeData.TreeData? tree, byte node, byte points)
    {
        var data = tree?.getNode(node);
        return $"{NodeName(tree, node)} {points}/{(data != null ? data.maxPoints.ToString(s_inv) : "?")}";
    }

    private static string SkillName(string id, string fallback)
    {
        var ability = id.Length > 0 ? AbilityManager.getPlayerAbility(id) : null;
        return Game.Alive(ability) && ability!.playerAbilityID == id ? $"{ability.abilityName} ({id})" : $"{fallback} (unknown)";
    }

    // ---- paste and host ----

    // The same handler as the tab's button. For a link: the frames of the 10 s before, then the request, then the
    // status, time, size and the longest frame while it ran.
    private static CommandResult PasteCommand(CommandCall call)
    {
        string text = call.Rest(2).Trim();
        if (text.Length == 0) return CommandResult.Error($"planner: usage {Usage}");
        if (!PasteKind.Recognise(text).IsLink)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = Planner.Paste(text);
            double ms = FrameTimes.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - start);
            if (result.Build != null) s_build = result.Build;
            return CommandResult.Ok($"planner paste: {result.Message} ({F(ms)} ms on the main thread)");
        }
        if (Planner.Fetch.Busy) return CommandResult.Ok($"planner paste: {Planner.Words.Busy}");

        float baselineEnd = Clock.Now + BaselineSeconds, before = 0f, during = 0f;
        bool started = false;
        string startWords = string.Empty;
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                float frame = Time.unscaledDeltaTime * 1000f;
                if (!started)
                {
                    before = Math.Max(before, frame);
                    if (Clock.Now < baselineEnd) return null;
                    var result = Planner.Paste(text);
                    startWords = result.Message;
                    if (!result.Started) return $"ok planner paste: {result.Message}";
                    started = true;
                    return null;
                }
                during = Math.Max(during, frame);
                // The feature's poll may take it first; then it's the last fetch, already in the library.
                var attempt = Planner.PollFetch() ?? (Planner.Fetch.Busy ? null : Planner.Fetch.Last);
                if (attempt == null) return null;
                if (attempt.Build != null) s_build = attempt.Build;
                return $"ok planner paste: {startWords} → {attempt.State}, status {attempt.Status}, {F(attempt.Ms)} ms, {attempt.Bytes} bytes, "
                    + $"longest frame {F(during)} ms (10 s before: {F(before)} ms); {Planner.ImportOutcome}";
            },
        };
    }

    private static CommandResult Host(CommandCall call)
    {
        string value = call.Rest(2).Trim();
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            Planner.Fetch.TestHost(null);
            return CommandResult.Ok("planner host off");
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return CommandResult.Error($"planner: usage {Usage}");
        Planner.Fetch.TestHost(value);
        return CommandResult.Ok($"planner host {value}: the next link paste's request goes there instead");
    }

    private static string F(double v) => v.ToString("F1", s_inv);

    private static string V(Vector3 v) => $"{v.x.ToString("0.###", s_inv)},{v.y.ToString("0.###", s_inv)},{v.z.ToString("0.###", s_inv)}";
}
