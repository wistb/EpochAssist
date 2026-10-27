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
    public const string Usage = "planner | planner trees | planner nodes [id] | planner skills | planner selector open|close | planner open passive|skills|tree <slot>|picker <slot>|inventory|mastery|close | planner tab <0-3> | planner mastery | planner gear | planner idols | planner blessings | planner weaver | planner watch on|off | planner import <file> | planner check | planner resolve | planner show [stage] | planner paste <text> | planner host <url>|off";

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
        "selector" => Selector(call),
        "screen" => ScreenCommand(call),
        "open" => OpenCommand(call),
        "tab" => Tab(call),
        "mastery" => Mastery(),
        "gear" => Gear(),
        "idols" => Idols(),
        "blessings" => Blessings(),
        "weaver" => Weaver(),
        "watch" => WatchSwitch(call),
        "import" => Import(call),
        "check" => Check(),
        "resolve" => ResolveCommand(),
        "show" => Show(call),
        "paste" => PasteCommand(call),
        "host" => Host(call),
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
                ui!.openInventory(false, false, false, 0, false);
                return CommandResult.Ok($"planner open inventory: UIBase.openInventory; open {ui.IsInventoryOpen()}");
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
                return CommandResult.Error("planner open: usage planner open passive|skills|tree <slot>|picker <slot>|inventory|mastery|close");
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
        var e = manager.equipment;
        var lines = new List<string> { $"ok planner gear: inventory {(UIBase.instance?.IsInventoryOpen() == true ? "open" : "closed")}" };
        var slots = new (string Name, Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase? Box)[]
        {
            ("helmet", e.helmet), ("armor", e.armor), ("weapon", e.weapon), ("offhand", e.offhand), ("amulet", e.amulet),
            ("leftRing", e.leftRing), ("rightRing", e.rightRing), ("belt", e.belt), ("gloves", e.gloves), ("boots", e.boots), ("relic", e.relic),
        };
        foreach (var (name, box) in slots) AddEntries(lines, name, box?.TryCast<IItemContainer>());
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    private static CommandResult Idols()
    {
        var manager = ItemContainersManager.Instance;
        if (!Game.Alive(manager) || manager!.idols == null) return CommandResult.Ok("planner idols: no item containers");
        var idols = manager.idols;
        var lines = new List<string> { $"ok planner idols: grid {idols.size.x}x{idols.size.y}, unlock {idols.unlockIdx}, inventory {(UIBase.instance?.IsInventoryOpen() == true ? "open" : "closed")}" };
        AddEntries(lines, "idol", idols.TryCast<IItemContainer>());
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    private static CommandResult Blessings()
    {
        var manager = ItemContainersManager.Instance;
        if (!Game.Alive(manager) || manager!.blessings == null) return CommandResult.Ok("planner blessings: no item containers");
        var boxes = manager.blessings.Containers;
        var lines = new List<string> { $"ok planner blessings: {boxes?.Count ?? 0} slots" };
        for (int i = 0; boxes != null && i < boxes.Count; i++)
            AddEntries(lines, $"slot {i}", boxes[i]?.TryCast<IItemContainer>());
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<InventoryBlessingSlotUI>(), false))
        {
            var ui = obj.TryCast<InventoryBlessingSlotUI>();
            if (!Game.Alive(ui) || !ui!.gameObject.activeInHierarchy) continue;
            var item = ui.itemData;
            lines.Add($"  shown {(item != null ? item.FullName : "empty")} grand {ui.isGrand} roll {ui.powerRoll.ToString("0.##", s_inv)} rect {Rect(ui.transform)}");
        }
        return new CommandResult(DevOutput.Reply("planner", lines));
    }

    private static CommandResult Weaver()
    {
        var tree = Game.LocalTree?.weaverTree;
        var lines = new List<string> { $"ok planner weaver: tree {tree?.treeID ?? "none"}: {Nodes(tree?.nodes)}" };
        var panel = Panel<Il2CppLE.Factions.FactionRankUIWeaver>();
        lines.Add($"weaver panel: {(Open(panel) ? $"open, tree {(panel!.IsWeaverTreeOpened ? "shown" : "hidden")}" : "closed")}");
        return new CommandResult(DevOutput.Reply("planner", lines));
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
            lines.Add($"  {label} at {entry!.Position.x},{entry.Position.y} size {entry.size.x}x{entry.size.y}: {item?.FullName ?? "?"} (type {data.itemType} sub {data.subType} unique {data.uniqueID} rarity {data.rarity}) affixes [{string.Join(" ", affixes)}]{ItemRect(entry)}");
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
        var lines = new List<string> { $"ok planner show {build.Id} stage {number} \"{stage.Name}\": class {stage.ClassId} mastery {stage.Mastery}" };

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
