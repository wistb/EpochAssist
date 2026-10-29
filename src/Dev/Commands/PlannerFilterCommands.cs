using EpochAssist.Core;
using EpochAssist.Features;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;
using Il2CppItemFiltering;

namespace EpochAssist.Dev;

// planner filter: the game's loot filters as the Planner reads and makes them. facts and dump only read; saveas-test
// writes a throwaway filter of its own and deletes it, and puts the player's active filter back.
internal static class PlannerFilterCommands
{
    public const string Usage = "planner filter facts | planner filter dump <name> | planner filter saveas-test | planner filter use <name> | planner filter looks | planner filter affixes [ids] | planner filter uniques | planner filter shards [ids] | planner filter subtypetest | planner filter mapicons | planner filter plan [stage|all] [strictness] | planner filter make [stage|all] [strictness] [force] | planner filter judge [name] | planner filter records | planner filter liveedit <rule #> addaffix|removeaffix <id> | planner filter scrolltrace on|show | planner filter interval [seconds] | planner filter live | planner filter checkcost [runs] | planner filter stages-test | planner filter view | planner filter builds | planner filter reset [stage|all] | planner filter base [restore]";

    private const string TestName = "EA saveas test";

    public static CommandResult Run(CommandCall call) => call.Lower(2) switch
    {
        "facts" => Facts(),
        "dump" => Dump(call.Rest(3)),
        "saveas-test" => SaveAsTest(),
        "use" => CommandResult.Ok($"planner filter use: {(Planner.LootFilterGame.Use(call.Rest(3)) ? "active" : "not loaded")} \"{Planner.LootFilterGame.ActiveName}\""),
        "looks" => Looks(),
        "affixes" => AffixGroups(call),
        "uniques" => UniqueFacts(),
        "shards" => Shards(call),
        "subtypetest" => SubTypeTest(call),
        "mapicons" => MapIcons(),
        "plan" => PlanCommand(call),
        "make" => MakeCommand(call),
        "judge" => JudgeCommand(call),
        "records" => Records(),
        "liveedit" => LiveEdit(call),
        "scrolltrace" => ScrollTrace(call),
        "interval" => call.Args.Length >= 3 && float.TryParse(call.Args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds)
            ? CommandResult.Ok($"planner filter interval: {Planner.FilterSaveSeconds = Math.Clamp(seconds, 0f, 10f)} s")
            : CommandResult.Ok($"planner filter interval: {Planner.FilterSaveSeconds} s"),
        "live" => Planner.LootFilterGame.Manager?.Filter is { } live
            ? new CommandResult(DevOutput.Reply("planner-filter-live", new[] { $"ok planner filter live \"{live.name}\": {live.rules?.Count ?? 0} rules; window {WindowEntries()}" }.Concat(Planner.LootFilterGame.Describe(live).Select(l => "  " + l)).ToList()))
            : CommandResult.Ok("planner filter live: no filter in use"),
        "checkcost" => CommandResult.Ok("planner filter checkcost:\n" + string.Join("\n", Planner.MeasureSaveCheck(call.Args.Length >= 3 && int.TryParse(call.Args[2], out int n) ? Math.Clamp(n, 5, 500) : 50))),
        "stages-test" => StagesTest(),
        "view" => ViewCommand(),
        "builds" => BuildsCommand(),
        "reset" => ResetCommand(call),
        "base" => CommandResult.Ok($"planner filter base: {Planner.LootFilterGame.EnsureEaBase(call.Lower(3) == "restore") ?? "not made"}"),
        _ => CommandResult.Error($"planner filter: usage {Usage}"),
    };

    // planner filter facts
    private static CommandResult Facts()
    {
        if (Planner.LootFilterGame.Manager is not { } manager) return CommandResult.Ok("planner filter facts: no filter manager");
        var active = manager.Filter;
        var lines = new List<string>
        {
            $"ok planner filter facts: folder {Planner.LootFilterGame.FilterFolder ?? "none"}",
            $"  limit {Planner.LootFilterGame.Limit}, format {ItemFilter.CURRENT_LF_VERSION}",
            $"  active {(active != null ? $"\"{active.name}\" ({active.rules?.Count ?? 0} rules, format {active.lootFilterVersion}, made in {active.lastModifiedInVersion})" : "none")}, off by player {manager.ManualFilterDisable}",
            $"  bindings {Bindings(manager)}",
        };
        foreach (var (name, a, b) in Planner.LootFilterGame.ListFilters()) lines.Add($"  file \"{name}\" {a} {b}");
        return new CommandResult(DevOutput.Reply("planner-filter-facts", lines));
    }

    private static string Bindings(ItemFilterManager manager)
    {
        var bindings = manager.GetBindings();
        if (bindings?.keys == null || bindings.values == null) return "none";
        var pairs = new List<string>();
        for (int i = 0; i < bindings.keys.Count && i < bindings.values.Count; i++) pairs.Add($"{bindings.keys[i]}={bindings.values[i]}");
        return pairs.Count == 0 ? "none" : string.Join(", ", pairs);
    }

    // planner filter dump <name>
    private static CommandResult Dump(string name)
    {
        if (name.Length == 0) return CommandResult.Error("planner filter dump: name a filter");
        string? before = Planner.LootFilterGame.ActiveName;
        string? path = Planner.LootFilterGame.PathOf(name);
        string? hashBefore = path != null ? Planner.LootFilterGame.HashFile(path) : null;
        var filter = Planner.LootFilterGame.LoadByName(name);
        if (filter == null) return CommandResult.Ok($"planner filter dump: \"{name}\" couldn't be read");
        string? hashAfter = path != null ? Planner.LootFilterGame.HashFile(path) : null;
        var lines = new List<string>
        {
            $"ok planner filter dump \"{filter.name}\": {filter.rules?.Count ?? 0} rules, format {filter.lootFilterVersion}, made in {filter.lastModifiedInVersion}, icon {filter.filterIcon}/{filter.filterIconColor}",
            $"  description \"{filter.description}\"",
            $"  active before \"{before}\", after \"{Planner.LootFilterGame.ActiveName}\"; file {(hashBefore == hashAfter ? "unchanged" : "CHANGED")}",
        };
        lines.AddRange(Planner.LootFilterGame.Describe(filter).Select(l => "  " + l));
        return new CommandResult(DevOutput.Reply("planner-filter-dump", lines));
    }

    // planner filter saveas-test: the same throwaway filter saved by the mod's serializer path and by the game's own
    // SaveFilter while it's briefly the active filter; the bytes are compared and both files deleted.
    private static CommandResult SaveAsTest()
    {
        if (Planner.LootFilterGame.Manager is not { } manager || Planner.LootFilterGame.PathOf(TestName) is not { } path)
            return CommandResult.Ok("planner filter saveas-test: no filter manager");
        if (File.Exists(path)) return CommandResult.Ok($"planner filter saveas-test: {path} is there already; left as it is");
        var active = manager.Filter;
        string bindingsBefore = Bindings(manager);
        var lines = new List<string> { $"ok planner filter saveas-test: active \"{active?.name}\", bindings {bindingsBefore}" };
        try
        {
            var filter = TestFilter();
            string? hash = Planner.LootFilterGame.SerializeTo(filter, path);
            if (hash == null) return CommandResult.Ok("planner filter saveas-test: the serializer path saved nothing");
            byte[] ours = File.ReadAllBytes(path);
            File.Delete(path);

            manager.Filter = filter;
            bool saved;
            try
            {
                saved = manager.SaveFilter(TestName, false);
            }
            finally
            {
                manager.Filter = active;
            }
            byte[] theirs = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            lines.Add($"  ours {ours.Length} bytes {hash[..12]}; SaveFilter {(saved ? "true" : "false")} {theirs.Length} bytes {(theirs.Length > 0 ? Planner.LootFilterGame.Hash(theirs)[..12] : "-")}");
            lines.Add(ours.AsSpan().SequenceEqual(theirs) ? "  bytes equal" : $"  bytes differ at {FirstDifference(ours, theirs)}");
            if (!ours.AsSpan().SequenceEqual(theirs))
            {
                File.WriteAllBytes(Path.Combine(DevBridge.Dir, "saveas-ours.xml"), ours);
                File.WriteAllBytes(Path.Combine(DevBridge.Dir, "saveas-theirs.xml"), theirs);
                lines.Add("  both kept in the dev folder as saveas-ours.xml and saveas-theirs.xml");
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string bindingsAfter = Bindings(manager);
            if (bindingsAfter != bindingsBefore && active != null) manager.UpdateBindingForActiveCharacter(active.name);
            lines.Add($"  after: active \"{manager.Filter?.name}\", bindings {(Bindings(manager) == bindingsBefore ? "as before" : Bindings(manager))}{(bindingsAfter != bindingsBefore ? " (put back)" : string.Empty)}");
        }
        return new CommandResult(string.Join("\n", lines));
    }

    private static ItemFilter TestFilter()
    {
        var filter = new ItemFilter(TestName) { description = "Epoch Assist test filter" };
        var rule = new Rule(Rule.RuleOutcome.SHOW, 0, false) { nameOverride = "Rings" };
        var sub = new SubTypeCondition();
        sub.SetTypes(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(new[] { (int)EquipmentType.RING }));
        rule.conditions.Add(sub);
        filter.AddRule(rule);
        return filter;
    }

    // planner filter looks: the game's rule colour and beam colour presets, and its loot filter sounds.
    private static CommandResult Looks()
    {
        var lines = new List<string> { $"ok planner filter looks: default sound {Rule.DefaultSoundId}, no sound {Rule.NoneSoundId}, default beam {Rule.DefaultBeamId}, no beam {Rule.NoneBeamId}" };
        var colours = EpochColorManager.instance?.colorList;
        void Presets(string label, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<EpochColor.EcolorPair>? presets)
        {
            if (presets == null)
            {
                lines.Add($"  {label}: none");
                return;
            }
            for (int i = 0; i < presets.Length; i++)
            {
                var pair = presets[i];
                lines.Add($"  {label} {i}: {pair?.primary?.id} #{pair?.primary?.hexCode} / #{pair?.secondary?.hexCode}");
            }
        }
        Presets("colour", colours?.lootFilterColorPresets);
        Presets("beam colour", colours?.lootFilterBeamColorPresets);
        var sounds = Il2CppLE.Audio.PresetSoundList.instance?.LootFilterSounds;
        if (sounds != null)
            for (int i = 0; i < sounds.Count; i++)
                lines.Add($"  sound {i}: {sounds[i].soundEnum} \"{sounds[i].displayName}\"{(sounds[i].showInLootFilterAsBeamOption ? " (beam option)" : string.Empty)}");
        for (int id = 0; id <= 20; id++)
        {
            try
            {
                Rule.GetRuleSound(id, out bool isDefault, out var preset);
                lines.Add($"  sound id {id}: {preset}{(isDefault ? " (default)" : string.Empty)}");
            }
            catch (Exception)
            {
                lines.Add($"  sound id {id}: none past the list");
                break;
            }
        }
        return new CommandResult(DevOutput.Reply("planner-filter-looks", lines));
    }

    // planner filter affixes: the game's affix ids by special type, with their class specificity.
    // planner filter uniques: the game's unique list with what a filter could group uniques by, one line each (tab
    // separated): id, name, set, random drop, reroll chance, LP level, level requirement, drop level, primordial,
    // cocooned, legendary type, hidden, display name, lore.
    // The shard store as the highly-needed rule reads it: which tracker answers, and counts for the given affix ids.
    private static CommandResult Shards(CommandCall call)
    {
        var lines = new List<string>();
        string Probe(string what, Func<Il2Cpp.GlobalDataTracker?> get)
        {
            try
            {
                var t = get();
                return $"{what}: {(t == null ? "null" : $"alive {Game.Alive(t)}, loaded {t.dataLoaded}, {t.GetShardCount()} shards held")}";
            }
            catch (Exception e)
            {
                return $"{what}: {e.GetType().Name} {e.Message}";
            }
        }
        lines.Add(Probe("getGlobalDataTracker", () => Il2Cpp.PlayerFinder.getGlobalDataTracker()));
        lines.Add(Probe("localGlobalDataTracker", () => Il2Cpp.PlayerFinder.localGlobalDataTracker));
        var ids = call.Args.Skip(2).Select(a => int.TryParse(a, out int id) ? id : -1).Where(id => id >= 0).ToList();
        foreach (int id in ids) lines.Add($"  {id}: {ShardCounts.Of(id)?.ToString() ?? "unread"}");
        return CommandResult.Ok("planner filter shards:\n" + string.Join("\n", lines));
    }

    // Whether a subtype condition over several types reads each subtype id with the item's own type: the condition names
    // helmets and boots with one boots subtype, then helmets and boots of that subtype id and of another are matched.
    private static CommandResult SubTypeTest(CommandCall call)
    {
        var (build, key) = Target(call, 99);
        if (build == null) return CommandResult.Ok("planner filter subtypetest: no build");
        var gear = Planner.GearOf(build, int.TryParse(key, out int stage) ? stage : build.Stages.Count - 1);
        if (!gear.Slots.TryGetValue("helmet", out var helmet) || !gear.Slots.TryGetValue("boots", out var boots))
            return CommandResult.Ok("planner filter subtypetest: the build plans no helmet or boots");
        int sub = boots.SubType;
        var lines = new List<string> { $"ok planner filter subtypetest: subtype {sub} (the boots' {boots.Name})" };
        Rule rule = null!;
        void Condition(params int[] types)
        {
            rule = new Rule(Rule.RuleOutcome.SHOW, 0, false) { isEnabled = true };
            var condition = new SubTypeCondition();
            condition.SetTypes(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(types));
            condition.SetSubTypes(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(new[] { sub }));
            rule.conditions.Add(condition);
            lines.Add($" types {string.Join(",", condition.GetTypes())}, subtypes {string.Join(",", condition.GetSubTypes())}:");
        }
        void Try(string label, PlannedItem from, int type, int subType)
        {
            var item = Planner.Make(Cut(from, Enumerable.Empty<PlannedAffix>()), out string? refresh);
            item.itemType = (byte)type;
            item.subType = (byte)subType;
            item.RefreshIDAndValues();
            lines.Add($"  {label} (type {item.itemType}, subtype {item.subType}): {(rule.Match(item, 100) ? "matches" : "no match")}{(refresh != null ? " · " + refresh : string.Empty)}");
        }
        Condition(boots.ItemType);
        Try("boots, that subtype", boots, boots.ItemType, sub);
        Try("boots, another subtype", boots, boots.ItemType, sub == 0 ? 1 : 0);
        Condition(helmet.ItemType, boots.ItemType);
        Try("boots, that subtype", boots, boots.ItemType, sub);
        Try("boots, another subtype", boots, boots.ItemType, sub == 0 ? 1 : 0);
        Try("helmet, the same subtype id", helmet, helmet.ItemType, sub);
        Try("helmet, another subtype", helmet, helmet.ItemType, sub == 0 ? 1 : 0);
        return CommandResult.Ok(string.Join("\n", lines));
    }

    private static CommandResult UniqueFacts()
    {
        var list = UniqueList.get()?.uniques;
        if (list == null) return CommandResult.Ok("planner filter uniques: no unique list");
        var lines = new List<string> { $"ok planner filter uniques: {list.Count}", "id\tname\tset\trandom\treroll\tlpl\tlevel\tdrop\tprimordial\tcocooned\tlegendary\thidden\tdisplay\tlore" };
        foreach (var e in list)
        {
            if (e == null) continue;
            lines.Add(string.Join("\t", e.uniqueID, e.name, e.isSetItem ? 1 : 0, e.canDropRandomly ? 1 : 0, e.rerollChance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                e.getEffectiveLevelForLegendaryPotential(e.subTypes is { Count: > 0 } subs ? subs[0] : (byte)0), e.getDefaultLevelRequirement(), e.useCustomDropLevel ? e.customDropLevel : -1,
                e.isPrimordialItem ? 1 : 0, e.isCocoonedItem ? 1 : 0, e.legendaryType, e.hideFromPlayers ? 1 : 0,
                e.getDisplayName(), (e.loreText ?? string.Empty).Replace('\n', ' ')));
        }
        return new CommandResult(DevOutput.Reply("planner-filter-uniques", lines));
    }

    private static CommandResult AffixGroups(CommandCall call)
    {
        var all = AffixList.get()?.GetAllAffixes();
        if (all == null) return CommandResult.Ok("planner filter affixes: no affix list");
        // With ids: each one's name, kind and class.
        if (call.Args.Length >= 3)
        {
            var named = new List<string> { "ok planner filter affixes:" };
            for (int i = 3; i <= call.Args.Length; i++)
                if (int.TryParse(call.Lower(i), out int id))
                    named.Add(all.FirstOrDefault(a => a?.affixId == id) is { } found
                        ? $"  {id} {found.affixName}: {found.specialAffixType}, rolls on {found.rollsOn}, class {found.classSpecificity}"
                        : $"  {id}: not in the game's list");
            return new CommandResult(DevOutput.Reply("planner-filter-affixes", named));
        }
        var lines = new List<string> { $"ok planner filter affixes: {all.Length} affixes" };
        foreach (var group in all.Where(a => a != null).GroupBy(a => (a.specialAffixType, a.classSpecificity)).OrderBy(g => g.Key.specialAffixType))
        {
            var ids = group.Select(a => a.affixId).OrderBy(i => i).ToList();
            lines.Add($"  {group.Key.specialAffixType} / {group.Key.classSpecificity}: {ids.Count}{(ids.Count <= 70 ? ": " + string.Join(",", ids) : string.Empty)}");
        }
        var groups = Planner.LootFilterBase.Groups;
        foreach (var (name, ids) in new[] { ("health/phys res", groups.Health), ("resistances", groups.Resistances), ("movement speed", groups.Movespeed) })
            lines.Add($"  group {name}: {ids.Count}: " + string.Join(", ", ids.Take(40).Select(id => $"{id} {AffixList.get()?.GetAffixName(id)}")));
        foreach (int id in new[] { 766, 779, 698, 758, 673, 714, 826, 891 })
            if (all.FirstOrDefault(a => a?.affixId == id) is { } a) lines.Add($"  {id} {a.affixName}: {a.specialAffixType}, rolls on {a.rollsOn}, class {a.classSpecificity}");
        return new CommandResult(DevOutput.Reply("planner-filter-affixes", lines));
    }

    // planner filter mapicons: the game's rule editor's map icon choices, with each one's sprite, read from its dropdown
    // (the editor's prefab, loaded or not shown).
    private static CommandResult MapIcons()
    {
        var lines = new List<string> { "ok planner filter mapicons:" };
        foreach (var obj in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<RuleUI>()))
        {
            var ui = obj.TryCast<RuleUI>();
            if (!Game.Alive(ui)) continue;
            var dropdown = ui!.mapIconDropdown;
            lines.Add($"  RuleUI {ui.gameObject.name} (scene {ui.gameObject.scene.name}, active {ui.gameObject.activeInHierarchy}): {dropdown?.options?.Count ?? -1} options");
            if (dropdown?.options == null) continue;
            for (int i = 0; i < dropdown.options.Count; i++)
            {
                var option = dropdown.options[i];
                var sprite = option.image;
                lines.Add($"    {i}: \"{option.text}\" sprite {(Game.Alive(sprite) ? $"{sprite!.name} {sprite.rect.width}x{sprite.rect.height} tex {sprite.texture?.name}" : "none")}");
            }
            break;
        }
        foreach (var obj in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MapIconDropdownIconHelper>()))
        {
            var helper = obj.TryCast<MapIconDropdownIconHelper>();
            if (!Game.Alive(helper)) continue;
            var sprite = helper!.img?.sprite;
            lines.Add($"  helper {helper.gameObject.name} label {helper.isLabel}: sprite {(Game.Alive(sprite) ? $"{sprite!.name} {sprite.rect} tex {sprite.texture?.name}" : "none")}, img colour {helper.img?.color}, "
                + $"rare {helper._rareColor} unique {helper._uniqueColor} set {helper._setColor} legendary {helper._legendaryColor} key {helper._keyColor} exalted {helper._exaltedColor} golden {helper._goldenColor} obsidian {helper._obsidianColor}");
        }
        return new CommandResult(DevOutput.Reply("planner-filter-mapicons", lines));
    }

    // ---- plans, making and judging ----

    // The bound build and a stage key from argument i ("all", a stage number from 1, or the character's stage).
    private static (Build? Build, string Key) Target(CommandCall call, int i)
    {
        Planner.Library.EnsureLoaded();
        // A build id anywhere in the arguments picks that build.
        var build = call.Args.Select(a => Planner.Library.Find(a)).FirstOrDefault(b => b != null) ?? Planner.ActiveBuild ?? Planner.Library.Builds.FirstOrDefault();
        if (build == null) return (null, string.Empty);
        string arg = call.Lower(i);
        string key = arg == "all" ? "all" : int.TryParse(arg, out int n) && n >= 1 ? (n - 1).ToString()
            : build == Planner.ActiveBuild ? Planner.ActiveStage.ToString() : build.FirstStage.ToString();
        return (build, key);
    }

    private static int? StrictnessArg(string word) => word switch
    {
        "levelling" or "l" => 0,
        "regular" or "r" => 1,
        "strict" or "s" => 2,
        "verystrict" or "v" => 3,
        _ => null,
    };

    private static FilterPlan PlanOf(Build build, string key, int? strictness)
    {
        var choices = Planner.LootFilterStore.ChoicesFor(build.Id, key);
        int? kept = choices.Strictness;
        if (strictness != null) choices.Strictness = strictness;
        try
        {
            return Planner.PlanFor(build, key, choices, Planner.LootFilterGame.Limit);
        }
        finally
        {
            choices.Strictness = kept;
        }
    }

    // planner filter plan [stage|all] [strictness]
    private static CommandResult PlanCommand(CommandCall call)
    {
        var (build, key) = Target(call, 3);
        if (build == null) return CommandResult.Ok("planner filter plan: no build");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = PlanOf(build, key, StrictnessArg(call.Lower(4)));
        double ms = sw.Elapsed.TotalMilliseconds;
        var lines = new List<string>
        {
            $"ok planner filter plan \"{plan.Name}\" ({build.Id} stage {plan.StageKey}): {plan.Rules.Count} of {plan.Limit} rules, {plan.EnabledCount} on, "
            + $"{Planner.StrictnessNames[plan.Strictness]}, base {plan.Base}, band {(plan.Band is { } b ? $"{b.Min}-{b.Max}" : "every level")}, made in {ms:0.00} ms",
            $"  description \"{plan.Description}\"",
        };
        if (plan.Note != null) lines.Add($"  note: {plan.Note}");
        for (int i = 0; i < plan.Rules.Count; i++) lines.Add($"  {i + 1,3} {Describe(plan.Rules[i])}");
        foreach (var left in plan.LeftOut) lines.Add($"  left out: {left.What}: {left.Why}");
        return new CommandResult(DevOutput.Reply("planner-filter-plan", lines));
    }

    private static string Describe(PlanRule rule)
    {
        var c = rule.Conditions;
        var parts = new List<string>();
        if (c.Types is { Count: > 0 } types) parts.Add($"Sub[{(types.Count > 6 ? $"{types.Count} types" : string.Join(",", types.Select(t => ((EquipmentType)t).ToString())))}:{(c.SubTypes is { Count: > 0 } subs ? string.Join(",", subs) : "*")}]");
        foreach (var a in c.Affixes) parts.Add($"Affix[{a.Ids.Count} ids{(a.Ids.Count <= 6 ? ": " + string.Join(",", a.Ids) : string.Empty)}; T{a.TierBar}+; min {a.Min}{(a.Sum is int sum ? $"; sum {sum}" : string.Empty)}]");
        if (c.Uniques is { Count: > 0 } u) parts.Add($"Uniques[{string.Join(",", u)}]");
        if (c.Rarity != 0) parts.Add($"Rar[{(RarityCondition.Rarity)c.Rarity}]");
        if (c.Potential is { } p) parts.Add($"Pot[LP{p.Lp} WW{p.Ww}]");
        if (c.Forging is int fp) parts.Add($"FP{fp}+");
        if (c.Resource is { } res) parts.Add($"All {res}");
        if (c.Corruption != 0) parts.Add(c.Corruption == 1 ? "Corr[only]" : "Corr[none]");
        if (c.Levels is { } l) parts.Add($"Lvl{l.Min}-{l.Max}");
        if (c.Classes != 0) parts.Add($"Class[{(ItemList.ClassRequirement)c.Classes}]");
        if (c.Factions is { Count: > 0 } f) parts.Add($"Faction[{string.Join(",", f)}]");
        if (rule.BaseIndex >= 0) parts.Add($"base rule {rule.BaseIndex}");
        var look = rule.Look;
        string looks = $"col {(look.Colour?.ToString() ?? "-")} {(look.Emphasized ? "E" : "-")} snd{look.Sound} map{look.MapIcon} beam{(look.Beam is { } bm ? $"{bm.Size}/{bm.Colour}" : "-")}";
        return $"{(rule.Enabled ? "on " : "OFF")} {(rule.Hide ? "HIDE" : "SHOW")} {Planner.StepLetters(rule.Steps),-4} {rule.Key,-22} {looks} | {rule.Name} | {string.Join(" & ", parts)}";
    }

    // planner filter make [stage|all] [strictness] [force]
    private static CommandResult MakeCommand(CommandCall call)
    {
        var (build, key) = Target(call, 3);
        if (build == null) return CommandResult.Ok("planner filter make: no build");
        bool force = call.Args.Skip(2).Any(a => a.Equals("force", StringComparison.OrdinalIgnoreCase));
        var plan = PlanOf(build, key, StrictnessArg(call.Lower(4)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Planner.LootFilterGame.Make(plan, replaceEdited: force);
        return CommandResult.Ok($"planner filter make: {result.Message}{(result.Ask ? " (answered no; add force to replace)" : string.Empty)} in {sw.Elapsed.TotalMilliseconds:0} ms, {plan.Rules.Count} rules, active \"{Planner.LootFilterGame.ActiveName}\"");
    }

    // planner filter view: the bound build's view on its Loot filter page.
    private static CommandResult ViewCommand()
    {
        var build = Planner.ActiveBuild;
        if (build == null) return CommandResult.Ok("planner filter view: no bound build");
        Planner.OpenBuildView(build.Id);
        Planner.SetFilterView();
        return CommandResult.Ok($"planner filter view: {build.Id}");
    }

    // planner filter builds: the library's builds, for the build id argument.
    private static CommandResult BuildsCommand()
    {
        Planner.Library.EnsureLoaded();
        return CommandResult.Ok("planner filter builds: " + string.Join(", ", Planner.Library.Builds.Select(b => $"{b.Id} \"{b.Name}\" ({b.Stages.Count})")));
    }

    // planner filter reset [stage|all]: the stage's choices back to the defaults, as the page's Reset.
    private static CommandResult ResetCommand(CommandCall call)
    {
        var (build, key) = Target(call, 3);
        if (build == null) return CommandResult.Ok("planner filter reset: no build");
        Planner.LootFilterStore.Reset(build.Id, key);
        Planner.LootFilterStore.Flush(now: true);
        return CommandResult.Ok($"planner filter reset: {build.Id} stage {key}");
    }

    // planner filter stages-test: the stage hooks on two throwaway build ids, their files deleted after.
    private static CommandResult StagesTest()
    {
        const string a = "test-stagesa", b = "test-stagesb";
        string Keys(string id) => string.Join(",", Planner.LootFilterStore.StageKeys(id).OrderBy(k => k).Select(k => $"{k}={Planner.LootFilterStore.ChoicesFor(id, k).Name}"));
        Planner.LootFilterStore.ChoicesFor(a, "0").Name = "zero";
        Planner.LootFilterStore.ChoicesFor(a, "1").Name = "one";
        Planner.LootFilterStore.ChoicesFor(a, "2").Name = "two";
        Planner.LootFilterStore.ChoicesFor(a, "all").Name = "all";
        Planner.LootFilterStore.ChoicesFor(b, "0").Name = "b-zero";
        Planner.LootFilterStore.ChoicesFor(b, "all").Name = "b-all";
        var lines = new List<string> { $"ok planner filter stages-test: a {Keys(a)}" };
        Planner.LootFilterStore.StagesChanged(a, new Dictionary<int, int?> { [0] = 1, [1] = 0, [2] = null });
        lines.Add($"  after 0→1, 1→0, 2 removed: a {Keys(a)}");
        Planner.LootFilterStore.MoveChoices(a, b, 1);
        lines.Add($"  after merging a into b at +1: b {Keys(b)}; a's file {(File.Exists(Path.Combine(Planner.LootFilterGame.DataDir, a + ".json")) ? "kept" : "deleted")}");
        Planner.LootFilterStore.DeleteChoices(a);
        Planner.LootFilterStore.DeleteChoices(b);
        return CommandResult.Ok(string.Join("\n", lines).Substring(3));
    }

    // The game's open loot filter window: how many rule entries it draws, and the filter it was built for.
    private static string WindowEntries()
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<LootFilterUI>()))
            if (obj.TryCast<LootFilterUI>() is { } ui && Game.Alive(ui) && ui.gameObject.activeInHierarchy)
                return $"{ui.entries?.Count ?? -1} entries, built for \"{ui._lastActiveFilterWithUiBuilt?.name}\", building {ui._isBuildingUi}, scrolled {ui.autoScrollScrollRect?.content?.anchoredPosition.y:0} px";
        return "not open";
    }

    // planner filter scrolltrace on|show: records the window hold's passes (the list's offset each pass before it was put back).
    private static CommandResult ScrollTrace(CommandCall call)
    {
        if (call.Lower(3) == "on")
        {
            Planner.LootFilterGame.ScrollTrace = new List<string>();
            return CommandResult.Ok("planner filter scrolltrace: on");
        }
        var lines = new List<string> { $"ok planner filter scrolltrace: {Planner.LootFilterGame.ScrollTrace?.Count ?? 0} passes" };
        if (Planner.LootFilterGame.ScrollTrace is { } trace) lines.AddRange(trace.Select(l => "  " + l));
        Planner.LootFilterGame.ScrollTrace = null;
        return new CommandResult(DevOutput.Reply("planner-filter-scrolltrace", lines));
    }

    // planner filter liveedit <rule #> addaffix|removeaffix <id>: changes a rule of the filter in use in memory, as the game's
    // editor does before it saves (for testing what the page takes in).
    private static CommandResult LiveEdit(CommandCall call)
    {
        if (Planner.LootFilterGame.Manager?.Filter is not { rules: { } rules } || !int.TryParse(call.Lower(3), out int number) || number < 1 || number > rules.Count
            || !int.TryParse(call.Lower(5), out int id))
            return CommandResult.Error("planner filter liveedit: usage <rule #> addaffix|removeaffix <id>");
        var rule = rules[rules.Count - number];
        for (int i = 0; i < rule.conditions.Count; i++)
        {
            if (rule.conditions[i]?.TryCast<AffixCondition>() is not { } affix) continue;
            bool done = call.Lower(4) == "addaffix" ? affix.affixes.Add(id) : affix.affixes.Remove(id);
            return CommandResult.Ok($"planner filter liveedit: #{number} {rule.nameOverride}: {call.Lower(4)} {id} {(done ? "done" : "no change")}, {affix.affixes.Count} ids");
        }
        return CommandResult.Ok($"planner filter liveedit: #{number} has no affix condition");
    }

    // planner filter records
    private static CommandResult Records()
    {
        var lines = new List<string> { $"ok planner filter records: {Planner.LootFilterStore.Records.Count}" };
        foreach (var r in Planner.LootFilterStore.Records)
            lines.Add($"  \"{r.File}\" build {(r.BuildId.Length > 0 ? r.BuildId : "-")} stage {r.StageKey} {r.Hash[..12]} {r.Made:u}: {Planner.LootFilterStore.StateOf(r)}");
        return new CommandResult(string.Join("\n", lines));
    }

    // planner filter judge [name]: the stage's planned pieces, each cut below its bar, and test items, judged by a made
    // filter read back from its file.
    private static CommandResult JudgeCommand(CommandCall call)
    {
        var (build, key) = Target(call, 99);
        if (build == null) return CommandResult.Ok("planner filter judge: no build");
        string name = call.Rest(3);
        if (name.Length == 0 && Planner.LootFilterStore.RecordFor(build.Id, key) is { } record) name = Path.GetFileNameWithoutExtension(record.File);
        if (name.Length == 0 || Planner.LootFilterGame.LoadByName(name) is not { } filter) return CommandResult.Ok($"planner filter judge: no made filter \"{name}\"");
        int level = Game.LocalTree?.characterLevel ?? 0;
        var lines = new List<string> { $"ok planner filter judge \"{name}\" at level {level}: item → rule (Match, then first enabled rule by Rule.Match)" };
        var gear = Planner.GearOf(build, int.TryParse(key, out int stage) ? stage : build.Stages.Count - 1);
        void Judge(string label, PlannedItem planned, Action<ItemDataUnpacked>? change = null)
        {
            if (planned.Unmakeable) return;
            var item = Planner.Make(planned, out string? refresh);
            change?.Invoke(item);
            lines.Add($"  {label}: {(refresh != null ? "refused; " : string.Empty)}{JudgeText(filter, item, level)}");
        }
        foreach (var (slot, item) in gear.Slots)
        {
            Judge($"{slot} {item.Name}", item);
            if (item.Unique != 0)
            {
                Judge($"{slot} {item.Name} at 4 LP", item, i => i.legendaryPotential = 4);
                Judge($"{slot} {item.Name} at 1 LP", item, i => i.legendaryPotential = 1);
                continue;
            }
            if (item.Affixes.Count == 0) continue;
            Judge($"{slot} one planned affix", Cut(item, item.Affixes.Take(1)));
            Judge($"{slot} planned affixes two tiers lower", Cut(item, item.Affixes.Select(a => a with { Tier = (byte)Math.Max(0, a.Tier - 2) })));
            var otherBase = Cut(item, item.Affixes);
            otherBase.SubType = (ushort)(item.SubType == 0 ? 1 : 0);
            Planner.Check(otherBase);
            Judge($"{slot} same affixes, other base", otherBase);
        }
        foreach (var idol in gear.Idols.Take(4))
        {
            Judge($"idol {idol.Name}", idol);
            if (idol.Affixes.Count > 1) Judge($"idol {idol.Name} one affix", Cut(idol, idol.Affixes.Take(1)));
        }
        var rare = new PlannedItem { ItemType = 21, SubType = 0, Rarity = 2 };
        rare.Affixes.Add(new PlannedAffix(1, 6, 128, SealedAffixType.None));
        rare.Affixes.Add(new PlannedAffix(3, 6, 128, SealedAffixType.None));
        Planner.Check(rare);
        Judge("unplanned rare ring, two T7", rare);
        var rare2 = new PlannedItem { ItemType = 21, SubType = 0, Rarity = 2 };
        rare2.Affixes.Add(new PlannedAffix(1, 3, 128, SealedAffixType.None));
        Planner.Check(rare2);
        Judge("unplanned magic ring, one T4", rare2);
        return new CommandResult(DevOutput.Reply("planner-filter-judge", lines));
    }

    private static PlannedItem Cut(PlannedItem item, IEnumerable<PlannedAffix> affixes)
    {
        var copy = new PlannedItem { Place = item.Place, Slot = item.Slot, ItemType = item.ItemType, SubType = item.SubType, Rarity = item.Rarity, Implicits = item.Implicits };
        copy.Affixes.AddRange(affixes);
        copy.Rarity = (byte)Math.Min(4, copy.Affixes.Count);
        Planner.Check(copy);
        return copy;
    }

    private static string JudgeText(ItemFilter filter, ItemDataUnpacked item, int level)
    {
        string viaMatch;
        try
        {
            var outcome = filter.Match(item, out var colour, out var emphasize, out int number, out int sound, out int map, out bool beam, out var size, out var beamColour);
            viaMatch = $"Match {outcome} rule {number}";
        }
        catch (Exception e)
        {
            viaMatch = $"Match threw {e.GetType().Name}";
        }
        string first = "none";
        for (int i = filter.rules.Count - 1; i >= 0; i--)
        {
            var rule = filter.rules[i];
            if (!rule.isEnabled || !rule.Match(item, level)) continue;
            first = $"\"{rule.nameOverride}\" (order {rule.Order})";
            break;
        }
        return $"{viaMatch}; first {first}";
    }

    private static string FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) return $"byte {i}";
        return $"byte {n} (lengths {a.Length} and {b.Length})";
    }
}
