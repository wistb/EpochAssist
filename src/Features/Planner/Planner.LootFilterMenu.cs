using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using EpochAssist.Ui;
using Il2Cpp;
using UnityEngine;
using S = EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

// The build view's Loot filter page: the filter the mod would make for the chosen stage, its options and rules, what
// was left out, and Make. Rows are made again only when a choice, the stage, the library or a default changes.
internal static partial class Planner
{
    private const string FilterRuleBlock = "planner.filter.rule";
    private const string FilterFooterBlock = "planner.filter.footer";
    private const string FilterUniqueBlock = "planner.filter.unique";
    private const float FilterRuleHeight = 24f;

    // The page's state for the session: the stage key per build, the open rule folds, the last result and its prompt.
    private static readonly Dictionary<string, string> s_filterStage = new();
    private static readonly HashSet<string> s_filterOpen = new();
    private static int s_filterVersion;
    private static string? s_filterResult;
    // An edited made file waiting for the player's yes, and the plan it would be replaced with.
    private static FilterPlan? s_filterAsk;
    private static string? s_filterAskText;
    // EA · Base's restore waiting for the player's yes.
    private static bool s_restoreAsk;
    // The base choice's names, read from the game's folder when the rows are made.
    private static List<string> s_baseNames = new();

    private static (string? Build, string Stage, int Version, int Generation, int Defaults) s_filterRowsFor;
    private static List<MenuRow> s_filterRows = new();
    private static FilterPlan? s_filterPlan;

    /// <summary>The page is offered: its switch on, the Planner running and the game's filter classes there.</summary>
    private static bool LootFilterOffered => S.LootFilter.Value && s_run is { } run && run.Feature.Has(LootFilterGroup);

    /// <summary>Why the page can't make a filter (the game lacks a member of its group), or null.</summary>
    private static string? LootFilterUnavailable()
    {
        if (s_run is not { } run || run.Feature.Has(LootFilterGroup)) return null;
        var missing = Compat.Missing(run.Feature).Where(m => m.Group == LootFilterGroup).Select(m => m.Text).FirstOrDefault();
        return $"Loot filter: unavailable ({missing ?? "game update"})";
    }

    // The stage key the page shows for a build: the player's pick this session, else the bound stage.
    private static string FilterStageKey(Build build)
    {
        if (s_filterStage.TryGetValue(build.Id, out var key) && (key == "all" ? build.Stages.Count > 1 : int.TryParse(key, out int i) && i < build.Stages.Count))
            return key;
        return GuideStage(build).ToString();
    }

    // The UTILITY: group switches' tips; label and hint are the default setting's.
    private static readonly (UtilityGroup Group, string Tip)[] s_utilityRows =
    {
        (UtilityGroup.Ascend, "One rule over every item type the planner has a unique or set item for: normal to exalted, uncorrupted, with forging potential left, needing Circle of Fortune rank. A Rune of Ascendance keeps the item type, not the base or the rarity. Any item can take the rune; on these, the Circle's Upgraded Rune of Ascendance has a chance to keep it."),
        (UtilityGroup.Havoc, "Items of the planner slots' types with any T7 affix and a planner affix, uncorrupted, with forging potential left."),
        (UtilityGroup.ClassShatter, "Class affixes roll on few items, so their shards are worth a rune at any tier. Magic, rare and exalted items, loud."),
        (UtilityGroup.LowShards, "Magic, rare and exalted items with a planner affix you hold fewer shards of than the count below, loud."),
    };

    private static int FilterDefaultsStamp() => HashCode.Combine(S.LootFilterStrictness.Value, S.LootFilterMinAffixes.Value, S.LootFilterTierCap.Value,
        S.LootFilterHideRest.Value, S.LootFilterEmphasizeUniques.Value, S.LootFilterHideOtherClasses.Value,
        HashCode.Combine(S.LootFilterHideClassesFirst.Value, S.LootFilterHideAll.Value, S.LootFilterLowShards.Value, ShardCounts.Generation,
            string.Concat(s_utilityRows.Select(r => S.UtilityDefault(r.Group).Value ? '1' : '0')) + S.LootFilterShatterTier.Value, string.Concat(S.LootFilterBaseGroups.Select(g => g.Switch.Value ? '1' : '0'))));

    private static void FilterChanged(Build build)
    {
        LootFilterStore.Changed(build.Id);
        s_filterVersion++;
        s_filterResult = null;
        s_filterAsk = null;
    }

    /// <summary>The plan the page shows for a build, made again only when something it's made from changed.</summary>
    private static FilterPlan FilterPlanOf(Build build)
    {
        FilterRows(build, new List<MenuRow>());
        return s_filterPlan!;
    }

    /// <summary>The checklist's row that opens the page for the stage; saves nothing. Without the game's filter classes it
    /// says why instead.</summary>
    private static MenuRow FilterChecklistRow(Build build) =>
        new TextButtonRow("Loot filter", () => LootFilterUnavailable() ?? $"Make loot filter for {FilterStageLabel(build, GuideStage(build).ToString())}",
            new ActionButton("Open", "planner.filter.open") { OnClick = () => SetFilterView(), Tip = "Opens the Loot filter page for this stage", EnabledWhen = () => LootFilterOffered })
        {
            Key = "filter.open",
            VisibleWhen = () => S.LootFilter.Value && s_run != null && !ShowingFilter(build),
        };

    // ---- Rows ----

    private static IReadOnlyList<MenuRow> FilterRows(Build build, List<MenuRow> head)
    {
        string key = FilterStageKey(build);
        var stamp = (build.Id, key, s_filterVersion, Library.Generation, FilterDefaultsStamp());
        if (s_filterRowsFor == stamp && s_filterPlan != null && s_filterRows.Count > 0 && ReferenceEquals(s_filterRowsHead, head)) return s_filterRows;
        s_filterRowsFor = stamp;
        s_filterRowsHead = head;
        RegisterFilterBlocks();

        var choices = LootFilterStore.ChoicesFor(build.Id, key);
        if (choices.Base != null && choices.Base != NoBase && LootFilterGame.PathOf(choices.Base) is { } basePath && !File.Exists(basePath))
        {
            choices.Base = null;
            LootFilterStore.Changed(build.Id);
        }
        var plan = PlanFor(build, key, choices, LootFilterGame.Manager != null ? LootFilterGame.Limit : 200);
        s_filterPlan = plan;
        DropStaleChoices(build, plan);
        s_baseNames = BaseChoices();

        var rows = new List<MenuRow>(head);
        if (LootFilterUnavailable() is { } why)
        {
            rows.Add(new HintRow(() => why) { Key = "filter.unavailable" });
            s_filterRows = rows;
            return rows;
        }

        // Header: stage, base, strictness, name, band and the whole-filter switches.
        if (build.Stages.Count > 1)
        {
            var stages = Enumerable.Range(0, build.Stages.Count).Select(i => FilterStageLabel(build, i.ToString())).Append("All stages").ToList();
            rows.Add(new ValueChoice("Filter for", () => key == "all" ? stages.Count - 1 : int.Parse(key), i =>
            {
                s_filterStage[build.Id] = i >= build.Stages.Count ? "all" : i.ToString();
                s_filterVersion++;
                s_filterResult = null;
            }, () => stages) { Key = "filter.stage" });
        }
        rows.Add(new ValueChoice("Base", () => Math.Max(0, s_baseNames.IndexOf(plan.Base == NoBase ? NoBaseLabel : plan.Base)), i =>
        {
            choices.Base = i <= 0 ? null : s_baseNames[i] == NoBaseLabel ? NoBase : s_baseNames[i];
            FilterChanged(build);
        }, () => s_baseNames) { Key = "filter.base" });
        rows.Add(new ValueChoice("Strictness", () => plan.Strictness, i =>
        {
            choices.Strictness = i == S.DefaultStrictness ? null : i;
            FilterChanged(build);
        }, () => StrictnessNames) { Key = "filter.strictness" });
        rows.Add(new ValueTextField("Name", () => choices.Name ?? plan.Name, text =>
        {
            string clean = CleanFilterName(text);
            choices.Name = clean.Length == 0 || clean == DefaultFilterName(build, plan.StageKey) ? null : clean;
            FilterChanged(build);
        }, 50) { Key = "filter.name" });
        rows.Add(new HeadingRow("Utility Rules"));
        foreach (var (group, tip) in s_utilityRows)
        {
            var g = group;
            var setting = S.UtilityDefault(g);
            string hint = setting.Help ?? string.Empty;
            var groupRules = plan.Rules.Where(r => UtilityGroupOf(r.Key) == g).ToList();
            int count = groupRules.Count;
            // The switch shows whether the group shows anything in this filter, and its change always lands.
            rows.Add(new ValueToggle(setting.Label, () => count > 0 ? groupRules.Any(r => r.Enabled) : UtilityOn(g, choices), v =>
            {
                if (v == setting.Value) choices.Utility.Remove(g);
                else choices.Utility[g] = v;
                // A rule's own check box gives way to the switch.
                foreach (var rule in groupRules)
                    if (choices.Rules.TryGetValue(rule.Key, out var change))
                    {
                        change.Enabled = null;
                        if (change.Hide == null && change.Look == null && change.Conditions == null) choices.Rules.Remove(rule.Key);
                    }
                // On where the strictness leaves the whole group off: its rules are turned on by hand.
                if (v && !groupRules.Any(r => StepDefault(r, plan, choices)))
                    foreach (var rule in groupRules)
                    {
                        if (!choices.Rules.TryGetValue(rule.Key, out var change)) choices.Rules[rule.Key] = change = new RuleChoice();
                        change.Enabled = true;
                    }
                FilterChanged(build);
            })
            {
                Key = "filter.utility." + g.ToString().ToLowerInvariant(),
                Hint = count > 0 ? hint : hint + " · none in this filter",
                Tip = tip,
            });
            if (g != UtilityGroup.LowShards) continue;
            rows.Add(new ValueNumber("Fewer shards than", () => plan.LowShards, v =>
            {
                int below = (int)v;
                choices.LowShards = below == (int)S.LootFilterLowShards.Value ? null : below;
                FilterChanged(build);
            }, 0f, 100f, 5f)
            {
                Key = "filter.lowshards",
                VisibleWhen = () => UtilityOn(UtilityGroup.LowShards, choices),
                Hint = "Planner affixes you hold fewer shards of count as highly needed. 0 leaves the rule out.",
                Tip = "Your shard counts are read when the filter is made, the crafting slot's included. While this page is open "
                    + "they're read again every two seconds; an affix crossing the count shows the filter as changed, and Update "
                    + "makes it again. Class affixes are left to their own rule.",
            });
        }
        rows.Add(new ValueNumber(S.LootFilterShatterTier.Label, () => plan.ShatterTier, v =>
        {
            int tier = (int)v;
            choices.ShatterTier = tier == (int)S.LootFilterShatterTier.Value ? null : tier;
            FilterChanged(build);
        }, 0f, 7f)
        {
            Key = "filter.shattertier",
            Hint = plan.Rules.Any(r => UtilityGroupOf(r.Key) == UtilityGroup.Shatter) ? S.LootFilterShatterTier.Help ?? string.Empty
                : (S.LootFilterShatterTier.Help ?? string.Empty) + " · none in this filter",
            Tip = "Magic, rare and exalted items with any planner affix at this tier or higher. At the setting's tier the rule follows "
                + "strictness (off at Very strict); a tier set here turns it on at every strictness. 0 keeps the rule in the filter, off.",
        });
        rows.Add(new ValueToggle("Hide other equipment", () => plan.HideRest, v =>
        {
            choices.HideRest = v == S.LootFilterHideRest.Value ? null : v;
            FilterChanged(build);
        })
        {
            Key = "filter.hiderest",
            Hint = "Ends the filter with a rule hiding equipment and idols no rule above shows.",
            Tip = "The last rule, #" + plan.Rules.Count + " Other equipment: it hides weapons, armour, jewellery and idols that no rule "
                + "above it shows. Runes, glyphs, keys, woven echoes and materials are never hidden by it. Off shows everything the rules "
                + "above don't hide.",
        });
        rows.Add(new ValueToggle("Hide every item kind", () => plan.HideAll, v =>
        {
            choices.HideAll = v == S.LootFilterHideAll.Value ? null : v;
            FilterChanged(build);
        })
        {
            Key = "filter.hideall",
            VisibleWhen = () => plan.HideRest,
            Hint = "The last rule hides anything no rule shows; runes, glyphs, keys and other resources get their own rules above it.",
            Tip = "The last rule hides every item kind, not only equipment, from the Hide from level (10 unless changed). Rules for runes, glyphs, keys and currency, woven echoes, shards and resonances sit just above it, so they "
                + "still show; turn one off to hide that kind.",
        });
        rows.Add(new ValueNumber("Hide from level", () => plan.Band?.Min ?? 0, v =>
        {
            int from = (int)v;
            choices.BandSet = true;
            choices.Band = from <= 0 ? null : new LevelBand(from, 100);
            FilterChanged(build);
        }, 0f, 100f)
        {
            Key = "filter.band",
            VisibleWhen = () => plan.HideRest,
            Hint = "Below this level the hide rule lets everything show, for levelling. 0 hides at every level.",
            Tip = "The level the hide rule starts at. It starts at the previous stage's planner level, so a stage's filter keeps "
                + "showing equipment until the character reaches that stage; the first or only stage hides at every level (0).",
        });
        rows.Add(new ValueToggle("Hide items for other classes", () => plan.HideOtherClasses, v =>
        {
            choices.HideOtherClasses = v == S.LootFilterHideOtherClasses.Value ? null : v;
            FilterChanged(build);
        })
        {
            Key = "filter.hideclasses",
            Hint = "Hides items only other classes can use.",
            Tip = "Adds the rule Items for other classes, hiding items whose class requirement is another class. Items any class can "
                + "use are kept. With Above uniques too off, it sits just below the base's uniques with good LP or WW, so a rare "
                + "unique for another class still shows.",
        });
        rows.Add(new ValueToggle("Above uniques too", () => plan.HideClassesFirst, v =>
        {
            choices.HideClassesFirst = v == S.LootFilterHideClassesFirst.Value ? null : v;
            FilterChanged(build);
        })
        {
            Key = "filter.hideclassesfirst",
            VisibleWhen = () => plan.HideOtherClasses,
            Hint = "The rule goes first: even a rare unique for another class is hidden.",
            Tip = "Items for other classes becomes rule #1, above every unique rule, so nothing for another "
                + "class shows. Off keeps it below the valuable uniques.",
        });
        if (plan.Note != null) rows.Add(new HintRow(() => plan.Note ?? string.Empty) { Key = "filter.note" });

        // The planned uniques: drop one, or the potential rules for all of them.
        var uniques = PlannedUniques(build, key);
        if (uniques.Count > 0)
        {
            rows.Add(new HeadingRow("Planner uniques"));
            rows.Add(new ValueToggle("Potential rules", () => choices.Potential ?? true, v =>
            {
                choices.Potential = v ? null : false;
                FilterChanged(build);
            })
            {
                Key = "filter.uniques.potential",
                Hint = "Brighter rules for the uniques below with good LP or WW; strictness picks which are on.",
                Tip = "Three rules for the planner uniques and sets, by legendary potential (LP) and weaver's will (WW): 3 LP or 20 WW (brightest, "
                    + "loud, largest beam), 2 LP or 17 WW (bright, beam) and 1 LP or 14 WW (emphasized). Strictness turns each on or off. Off writes all three "
                    + "disabled: the uniques still show through the plainer planner unique rules under them. Set items never carry LP, so for "
                    + "them nothing changes.",
            });
            foreach (var (id, name, item) in uniques)
                rows.Add(new BlockRow(FilterUniqueBlock, new FilterUniqueLine(build, id, name, item, choices)) { Key = "filter.unique." + id });
        }

        // The rules in the order the game judges them, each with its fold.
        rows.Add(new HeadingRow("Rules"));
        rows.Add(new HintRow(() => $"{plan.Rules.Count} of {plan.Limit} rules · {plan.EnabledCount} on") { Key = "filter.count" });
        foreach (var rule in plan.Rules)
        {
            rows.Add(new BlockRow(FilterRuleBlock, new FilterRuleLine(build, rule, choices, plan.Rules.IndexOf(rule) + 1)) { Key = "filter.rule." + rule.Key });
            AddRuleFold(rows, build, rule, choices);
        }

        if (plan.LeftOut.Count > 0)
        {
            rows.Add(new HeadingRow("Left out"));
            foreach (var left in plan.LeftOut.Take(40))
                rows.Add(new HintRow(() => $"{left.What}: {left.Why}") { Key = "filter.leftout." + rows.Count });
            if (plan.LeftOut.Count > 40) rows.Add(new HintRow(() => $"and {plan.LeftOut.Count - 40} more") { Key = "filter.leftout.more" });
        }

        // Saving gets its own section, apart from the lists above.
        rows.Add(new HeadingRow("Game filter"));
        rows.Add(new BannerRow(() => s_filterAskText, new ActionButton("Replace", "planner.filter.replace") { OnClick = () => ReplaceEdited(build) },
            new ActionButton("Keep", "planner.filter.keep") { OnClick = () => { s_filterAsk = null; s_filterAskText = null; s_filterResult = "Not saved: the edited file was kept"; } })
        { Key = "filter.ask" });
        rows.Add(new BannerRow(() => s_restoreAsk ? "Restore EA · Base? Your edits to it will be replaced." : null,
            new ActionButton("Restore", "planner.filter.restore.yes") { OnClick = RestoreEaBase },
            new ActionButton("Keep", "planner.filter.restore.no") { OnClick = () => s_restoreAsk = false })
        { Key = "filter.restore.ask" });
        rows.Add(new BlockRow(FilterFooterBlock, build) { Key = "filter.footer" });
        rows.Add(new HintRow(() => s_filterResult ?? string.Empty) { Key = "filter.result", VisibleWhen = () => s_filterResult != null });
        UpdateSaveState(build, plan);
        s_filterRows = rows;
        return rows;
    }

    private static List<MenuRow>? s_filterRowsHead;

    // The names the base choice offers: EA · Base, then the game's filters, leaving out the mod's own made ones.
    private static List<string> BaseChoices()
    {
        var names = new List<string> { EaBaseName, NoBaseLabel };
        foreach (var (name, _, _) in LootFilterGame.ListFilters())
        {
            var record = LootFilterStore.RecordOfFile(name + ".xml");
            if (name == EaBaseName || record != null) continue;
            names.Add(name);
        }
        return names;
    }

    private static List<(ushort Id, string Name, PlannedItem Item)> PlannedUniques(Build build, string key)
    {
        var list = new List<(ushort, string, PlannedItem)>();
        var stages = key == "all" ? Enumerable.Range(0, build.Stages.Count) : new[] { int.Parse(key) };
        foreach (int s in stages)
        {
            var gear = GearOf(build, s);
            foreach (var item in gear.Slots.Values.Concat(gear.Idols).Concat(gear.WeaverItems.Values))
                if (item.Unique != 0 && !item.Unknown.Contains("unique") && !list.Exists(u => u.Item1 == item.Unique))
                    list.Add((item.Unique, item.Name.Length > 0 ? item.Name : $"Unique {item.Unique}", item));
        }
        return list;
    }

    // A re-import that drops a slot or a unique drops the choices about it.
    private static void DropStaleChoices(Build build, FilterPlan plan)
    {
        var slots = plan.Rules.Where(r => r.Key.StartsWith("slot.", StringComparison.Ordinal) && r.Key.EndsWith(".main", StringComparison.Ordinal))
            .Select(r => r.Key["slot.".Length..^".main".Length]);
        var uniques = PlannedUniques(build, plan.StageKey).Select(u => u.Id);
        LootFilterStore.DropStale(build.Id, plan, slots, uniques);
    }

    // ---- Rule folds ----

    private static readonly string[] s_soundNames = { "Game default", "None", "Shing", "Shaker", "Zap", "Drum", "Begin", "Fight", "Discovery", "Inspiration", "Anvil", "Wild", "Comet", "Smash", "Smith", "Precious", "Metal", "Unlock" };
    private static readonly string[] s_beamSizes = { "Game's own", "Largest", "Very large", "Large", "Medium", "Small", "Very small", "Smallest" };
    // The game's rule colour presets (game-facts.md), the beam colour each pairs with, and their swatches.
    private static readonly string[] s_ruleColours = { "FFFFFF", "B2B2B2", "BEE600", "FFEA00", "FFAA00", "FF8000", "FF3D00", "FF002F", "FF8CEF", "F500FF", "6C1AFF", "A14CFF", "006EFF", "4C9BFF", "33FDFF", "00E69D", "2CFF00", "1DA600" };
    private static readonly int[] s_beamOfColour = { 0, 1, 3, 4, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 18, 19, 20, 21 };
    private static readonly List<string> s_colourChoices = new[] { "No recolour" }.Concat(s_ruleColours.Select((hex, i) => $"{i} · #{hex}")).ToList();
    private static readonly List<Color?> s_colourSwatches = new Color?[] { null }.Concat(s_ruleColours.Select((_, i) => (Color?)RuleSwatch(i))).ToList();
    private static readonly List<string> s_beamChoices = s_colourChoices.Skip(1).ToList();
    private static readonly List<Color?> s_beamSwatches = s_colourSwatches.Skip(1).ToList();
    // The game's map icon ids as its rule editor lists them: 0 its own icon for the item, 1 none, then one icon in each
    // rarity's colour (read from the editor's dropdown, game-facts.md).
    private static readonly List<string> s_mapIcons = new() { "Game default", "None", "Rare", "Unique", "Set", "Legendary", "Resource", "Exalted", "Golden", "Obsidian", "Tutorial" };
    private static readonly Color?[] s_mapIconTints =
    {
        null, null, new Color32(250, 221, 84, 255), new Color32(229, 151, 51, 255), new Color32(68, 154, 60, 255), new Color32(188, 0, 0, 255),
        new Color32(171, 36, 36, 255), new Color32(158, 84, 250, 255), new Color32(255, 215, 0, 255), new Color32(157, 64, 217, 255), Color.white,
    };
    private const string MapIconSprite = "treasure-minimap-icon-generic";
    private static Sprite? s_mapIconSprite;
    private static float s_mapIconLook = -1f;

    // The game's map icon art, found once by name; looked for again at most every few seconds while it isn't loaded.
    private static Sprite? MapIconArt()
    {
        if (Game.Alive(s_mapIconSprite)) return s_mapIconSprite;
        if (Clock.Now < s_mapIconLook) return null;
        s_mapIconLook = Clock.Now + 5f;
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
            if (obj.name == MapIconSprite && obj.TryCast<Sprite>() is { } sprite) return s_mapIconSprite = sprite;
        return null;
    }

    // An option's map icon in its colour: the game's own art, or a dot of the colour until that art is loaded.
    private static void DrawMapIcon(int option, Rect r)
    {
        if (option < 0 || option >= s_mapIconTints.Length || s_mapIconTints[option] is not Color tint) return;
        float size = Mathf.Min(r.width, r.height);
        var square = new Rect(r.x, Mathf.Round(r.center.y - size / 2f), size, size);
        var was = GUI.color;
        GUI.color = tint;
        if (MapIconArt() is { } sprite && Game.Alive(sprite.texture))
        {
            var t = sprite.texture;
            var area = sprite.textureRect;
            GUI.DrawTextureWithTexCoords(square, t, new Rect(area.x / t.width, area.y / t.height, area.width / t.width, area.height / t.height));
        }
        else
        {
            GUI.DrawTexture(new Rect(square.center.x - 5f, square.center.y - 5f, 10f, 10f), Theme.Swatch);
        }
        GUI.color = was;
    }

    private static Color RuleSwatch(int colour) =>
        colour >= 0 && colour < s_ruleColours.Length && ColorUtility.TryParseHtmlString("#" + s_ruleColours[colour], out var c) ? c : Color.white;

    private static void AddRuleFold(List<MenuRow> rows, Build build, PlanRule rule, StageChoices choices)
    {
        string key = rule.Key;
        bool Open() => s_filterOpen.Contains(key);
        RuleChoice Change() => choices.Rules.TryGetValue(key, out var c) ? c : choices.Rules[key] = new RuleChoice();
        void Look(Action<RuleLook> edit)
        {
            var look = rule.Look.Copy();
            edit(look);
            Change().Look = look;
            FilterChanged(build);
        }
        MenuRow Fold(MenuRow row) => new Indented(row, 1) { Key = row.Key, VisibleWhen = Open };

        // Conditions kept from the game's editor: one press goes back to the planner's (or the base's).
        if (rule.GameConditions)
        {
            rows.Add(Fold(new ActionButton("Use the planner's conditions", $"planner.filter.fold.{key}.conditions")
            {
                OnClick = () =>
                {
                    if (choices.Rules.TryGetValue(key, out var kept))
                    {
                        kept.Conditions = null;
                        if (kept.Enabled == null && kept.Hide == null && kept.Look == null) choices.Rules.Remove(key);
                    }
                    FilterChanged(build);
                },
                Tip = "This rule's conditions were edited in the game's editor and are kept on every Make; this goes back to the ones the page makes",
                Key = $"filter.fold.{key}.conditions",
            }));
        }
        if (rule.Origin != RuleOrigin.Base)
        {
            rows.Add(Fold(new ValueChoice("Outcome", () => rule.Hide ? 1 : 0, i =>
            {
                Change().Hide = i == 1;
                FilterChanged(build);
            }, () => new[] { "Show", "Hide" }) { Key = $"filter.fold.{key}.outcome" }));
            rows.Add(Fold(new ValueToggle("Capitalize labels", () => rule.Look.Emphasized, v => Look(l => l.Emphasized = v)) { Key = $"filter.fold.{key}.emphasized" }));
            rows.Add(Fold(new ValueChoice("Colour", () => (rule.Look.Colour ?? -1) + 1, i => Look(l => l.Colour = i == 0 ? null : i - 1), () => s_colourChoices)
            {
                Key = $"filter.fold.{key}.colour", Swatches = () => s_colourSwatches,
            }));
            rows.Add(Fold(new ValueChoice("Sound", () => Math.Clamp(rule.Look.Sound, 0, s_soundNames.Length - 1), i => Look(l => l.Sound = i), () => s_soundNames) { Key = $"filter.fold.{key}.sound" }));
            rows.Add(Fold(new ValueChoice("Map icon", () => Math.Clamp(rule.Look.MapIcon, 0, s_mapIcons.Count - 1), i => Look(l => l.MapIcon = i), () => s_mapIcons)
            {
                Key = $"filter.fold.{key}.map", Sample = (16f, DrawMapIcon),
            }));
            rows.Add(Fold(new ValueChoice("Beam", () => rule.Look.Beam?.Size ?? 0, i => Look(l =>
            {
                int colour = l.Beam?.Colour ?? (l.Colour is int c && c < s_beamOfColour.Length ? s_beamOfColour[c] : 0);
                l.Beam = i == 0 ? null : (i, colour);
            }), () => s_beamSizes) { Key = $"filter.fold.{key}.beam" }));
            rows.Add(Fold(new ValueChoice("Beam colour", () => Math.Max(0, Array.IndexOf(s_beamOfColour, rule.Look.Beam?.Colour ?? 0)), i => Look(l =>
            {
                if (l.Beam is { } beam) l.Beam = (beam.Size, s_beamOfColour[i]);
            }), () => s_beamChoices)
            {
                Key = $"filter.fold.{key}.beamcolour", EnabledWhen = () => rule.Look.Beam != null, Swatches = () => s_beamSwatches,
            }));
        }
        if (rule.Slot is not { } slot || !key.EndsWith(".main", StringComparison.Ordinal)) return;

        // The slot's own settings, on its main rule's fold.
        string slotKey = key["slot.".Length..^".main".Length];
        SlotChoice SlotChange() => choices.Slots.TryGetValue(slotKey, out var c) ? c : choices.Slots[slotKey] = new SlotChoice();
        var known = AffixList.get();
        foreach (int id in slot.Planned)
        {
            string name = known?.GetAffixName(id) is { Length: > 0 } n ? n : $"Affix {id}";
            rows.Add(Fold(new ValueToggle($"{name} (T{slot.Tiers[id]})", () => slot.Counted.Contains(id), v =>
            {
                var counted = slot.Counted.ToList();
                if (v && !counted.Contains(id)) counted.Add(id);
                else if (!v && counted.Count > 1) counted.Remove(id);
                SlotChange().Affixes = counted;
                FilterChanged(build);
            }) { Key = $"filter.fold.{key}.affix.{id}" }));
        }
        rows.Add(Fold(new ValueNumber("Minimum on one item", () => slot.Min, v =>
        {
            SlotChange().Min = (int)v;
            FilterChanged(build);
        }, 1f, 4f) { Key = $"filter.fold.{key}.min" }));
        rows.Add(Fold(new ValueNumber("Tier bar", () => slot.TierBar, v =>
        {
            SlotChange().TierBar = (int)v;
            FilterChanged(build);
        }, 1f, 7f) { Key = $"filter.fold.{key}.tierbar" }));
        rows.Add(Fold(new ValueChoice("Bases", () => slot.SubTypes.Count > 0 ? 1 : 0, i =>
        {
            SlotChange().SubTypes = i == 1 ? new List<int> { slot.SubType } : null;
            FilterChanged(build);
        }, () => new[] { "Every base of the type", $"Planner base only" }) { Key = $"filter.fold.{key}.bases" }));
    }

    // ---- The rule line ----

    /// <summary>A rule line: the rule, its stage's choices, and its number as the game's editor numbers it (1 at the top).</summary>
    private sealed record FilterRuleLine(Build Build, PlanRule Rule, StageChoices Choices, int Number)
    {
        public readonly string NumberText = Number.ToString();
    }

    private static GUIStyle? s_filterRuleText, s_filterRuleNumber;
    private const float FilterNumberWidth = 24f;

    private static void RegisterFilterBlocks()
    {
        if (!MenuBlocks.Has(FilterRuleBlock))
            MenuBlocks.Register(FilterRuleBlock, DrawFilterRule, d => new[] { RuleBridgeLine((FilterRuleLine)d!) }, (_, _) => FilterRuleHeight);
        if (!MenuBlocks.Has(FilterUniqueBlock))
            MenuBlocks.Register(FilterUniqueBlock, DrawFilterUnique, d => new[] { UniqueBridgeLine((FilterUniqueLine)d!) }, (_, _) => FilterUniqueHeight);
        if (!MenuBlocks.Has(FilterFooterBlock))
            MenuBlocks.Register(FilterFooterBlock, DrawFilterFooter, d => FooterBridge((Build)d!), (_, _) => FooterHeight());
    }

    private static string RuleBridgeLine(FilterRuleLine line) => RuleBridgeLine(line.Rule, line.Number);

    private static string RuleBridgeLine(PlanRule rule, int number) =>
        $"{(rule.Enabled ? "[x]" : "[ ]")} {number,3} {(rule.Hide ? "Hide" : "Show")} {rule.Name}{(rule.Look.Colour is int c ? $" · colour {c}" : string.Empty)}{(rule.GameConditions ? " · edited in the game" : string.Empty)}{(s_filterOpen.Contains(rule.Key) ? " ▾" : string.Empty)}";

    // A rule's on or off from its steps at the plan's strictness, before the player's own switch.
    private static bool StepDefault(PlanRule rule, FilterPlan plan, StageChoices choices) =>
        (rule.Steps & StepOf(plan.Strictness)) != 0 && !(choices.Potential == false && Array.IndexOf(s_potentialKeys, rule.Key) >= 0)
        && !(UtilityGroupOf(rule.Key) is { } group && !UtilityOn(group, choices));

    private static void DrawFilterRule(Rect rect, object? data)
    {
        if (data is not FilterRuleLine line) return;
        var rule = line.Rule;
        var box = new Rect(rect.x, rect.y, 18f, FilterRuleHeight);
        bool clickedBox = GUI.Button(box, GUIContent.none, GUIStyle.none);
        if (Focus.Item($"{FilterRuleBlock}.{rule.Key}.on", box) || clickedBox)
        {
            var change = line.Choices.Rules.TryGetValue(rule.Key, out var c) ? c : line.Choices.Rules[rule.Key] = new RuleChoice();
            change.Enabled = !rule.Enabled;
            // Back to what strictness gives it: no override is kept, so the slider moves it again.
            if (rule.Origin != RuleOrigin.Base && s_filterPlan is { } plan && change.Enabled == StepDefault(rule, plan, line.Choices)) change.Enabled = null;
            if (change.Enabled == null && change.Hide == null && change.Look == null) line.Choices.Rules.Remove(rule.Key);
            FilterChanged(line.Build);
            return;
        }
        var nameRect = new Rect(rect.x + 22f, rect.y, rect.width - 22f, FilterRuleHeight);
        bool clickedName = GUI.Button(nameRect, GUIContent.none, GUIStyle.none);
        if (Focus.Item($"{FilterRuleBlock}.{rule.Key}.fold", nameRect) || clickedName)
        {
            if (!s_filterOpen.Remove(rule.Key)) s_filterOpen.Add(rule.Key);
            return;
        }
        if (Event.current.type != EventType.Repaint) return;

        bool hover = rect.Contains(Event.current.mousePosition);
        if (hover && Widgets.Themed) Theme.DrawSliced(rect, Theme.HoverBg, Theme.CardRadius);
        Controls.Check(box, rule.Enabled ? 1 : 0, box.Contains(Event.current.mousePosition));
        float x = nameRect.x;
        // The rule's number, as the game's editor shows it, right-aligned in its own column.
        s_filterRuleNumber ??= new GUIStyle(Widgets.Style(() => Theme.Hint, GUI.skin.label)) { alignment = TextAnchor.MiddleRight, wordWrap = false };
        Widgets.Paint(new Rect(x, rect.y, FilterNumberWidth, FilterRuleHeight), line.NumberText, s_filterRuleNumber);
        x += FilterNumberWidth + 6f;
        var swatch = new Rect(x, Mathf.Round(rect.center.y - 5f), 10f, 10f);
        if (rule.Look.Colour is int colour) Theme.Fill(swatch, RuleSwatch(colour));
        else if (Widgets.Themed) Theme.DrawSliced(swatch, Theme.CheckOff, 2);
        x += 16f;
        s_filterRuleText ??= new GUIStyle(Widgets.Style(() => Theme.Label, GUI.skin.label)) { richText = true, wordWrap = false };
        // As the game's editor shows a rule with capitalized labels: SHOW or HIDE, underlined.
        string outcome = rule.Hide ? "Hide" : "Show";
        if (rule.Look.Emphasized) outcome = outcome.ToUpperInvariant();
        string edited = rule.GameConditions ? " · edited in the game" : string.Empty;
        string text = $"{(Widgets.Themed ? Widgets.Colored(outcome, Theme.Dim) : outcome)}  {rule.Name}{edited}";
        if (!rule.Enabled && Widgets.Themed) text = Widgets.Colored($"{outcome}  {rule.Name}{edited}", Theme.Dim);
        Widgets.Paint(new Rect(x, rect.y, rect.xMax - x - 20f, FilterRuleHeight), Controls.Fit(text, s_filterRuleText, rect.xMax - x - 20f), s_filterRuleText);
        if (rule.Look.Emphasized)
        {
            var size = s_filterRuleText.CalcSize(new GUIContent(outcome));
            float under = Mathf.Round(rect.center.y + s_filterRuleText.lineHeight * 0.5f);
            Theme.Fill(new Rect(x + s_filterRuleText.padding.left, under, size.x - s_filterRuleText.padding.horizontal, 1f), Widgets.Themed ? Theme.Dim : GUI.contentColor);
        }
        if (Widgets.Themed) Theme.Tinted(new Rect(rect.xMax - 16f, Mathf.Round(rect.center.y - 6f), 12f, 12f), s_filterOpen.Contains(rule.Key) ? Theme.Chevron : Theme.ChevronRight, Theme.Dim);
    }

    // ---- Planned uniques: icon, name in the item's colour, kept or dropped; the game's tooltip on hover ----

    private sealed record FilterUniqueLine(Build Build, ushort Id, string Name, PlannedItem Item, StageChoices Choices);

    private const float FilterIconSize = 22f, FilterUniqueHeight = 28f;

    private static string UniqueBridgeLine(FilterUniqueLine line) =>
        $"{(line.Choices.DroppedUniques.Contains(line.Id) ? "[ ]" : "[x]")} {line.Name}";

    private static void DrawFilterUnique(Rect rect, object? data)
    {
        if (data is not FilterUniqueLine line) return;
        var row = new Rect(rect.x, rect.y, rect.width, FilterUniqueHeight);
        bool kept = !line.Choices.DroppedUniques.Contains(line.Id);
        // The check box keeps it in the planned unique rules, as a rule line's does for its rule.
        var box = new Rect(row.x, row.y, 18f, row.height);
        bool clicked = GUI.Button(box, GUIContent.none, GUIStyle.none);
        if (Focus.Item($"{FilterUniqueBlock}.{line.Id}", box) || clicked)
        {
            if (kept) line.Choices.DroppedUniques.Add(line.Id);
            else line.Choices.DroppedUniques.Remove(line.Id);
            FilterChanged(line.Build);
            return;
        }
        if (Event.current.type != EventType.Repaint) return;

        var nameArea = new Rect(row.x + 22f, row.y, row.width - 22f, row.height);
        // The pointer on the name opens the planned item's game tooltip beside the menu, as the gear rows do.
        if (nameArea.Contains(Event.current.mousePosition))
        {
            if (Widgets.Themed) Theme.DrawSliced(nameArea, Theme.HoverBg, Theme.CardRadius);
            s_menuHover = line.Item;
            s_menuHoverSeen = Clock.Now;
        }
        Controls.Check(box, kept ? 1 : 0, box.Contains(Event.current.mousePosition));
        var square = new Rect(nameArea.x, Mathf.Round(row.center.y - FilterIconSize / 2f), FilterIconSize, FilterIconSize);
        if (ItemIcon(line.Item) is { } icon) DrawFitted(square, icon, Clock.Now);
        float x = square.xMax + 8f;
        s_filterRuleText ??= new GUIStyle(Widgets.Style(() => Theme.Label, GUI.skin.label)) { richText = true, wordWrap = false };
        var colour = MenuColour(ColourRoles.Item(PlannedRarity(line.Item)));
        string text = Widgets.Themed ? Widgets.Colored(line.Name, kept ? colour : Theme.Dim) : line.Name;
        Widgets.Paint(new Rect(x, row.y, nameArea.xMax - x, row.height), Controls.Fit(text, s_filterRuleText, nameArea.xMax - x), s_filterRuleText);
    }

    // ---- Footer: Make, Use now, Reset, Restore EA · Base ----

    private static IEnumerable<string> FooterBridge(Build build)
    {
        var plan = s_filterPlan;
        yield return string.Join(" · ", FooterButtons(build, plan).Where(b => b.Shown).Select(b => b.Enabled ? b.Label : $"({b.Label})"));
        if (SaveStatus() is { } status) yield return status.Text;
        foreach (var change in s_filterChanges) yield return "  • " + change;
    }

    /// <summary>Where this stage's filter stands: not made yet, saved as the page shows it, changed on the page since, or
    /// edited in the game's editor since.</summary>
    private enum FilterSave { New, Saved, Changed, Edited }

    private static (FilterSave State, MadeRecord? Record, bool BaseEdited) s_filterSave;
    private static float s_filterSaveNext;
    // A look costs about 0.08 ms with nothing changed (sizes and write times, a quick signature of the filter in use): four
    // a second add about 0.02 ms a frame, only while the page shows (every frame would be about 0.16 ms).
    private const float FilterSaveDefault = 0.25f;
    /// <summary>How often the page looks at its file and the filter in use; the dev bridge can change it to measure.</summary>
    internal static float FilterSaveSeconds = FilterSaveDefault;

    // Read when the rows are made, after Make, and four times a second while the page shows (an edit in the game's editor);
    // never per frame, since it reads the files.
    private static void UpdateSaveState(Build build, FilterPlan plan)
    {
        s_filterSaveNext = Clock.Now + FilterSaveSeconds;
        bool baseEdited = plan.Base == EaBaseName && LootFilterStore.RecordFor(string.Empty, "base") is { } baseRecord
            && LootFilterStore.StateOf(baseRecord) == MadeState.Edited;
        var record = LootFilterStore.RecordFor(build.Id, plan.StageKey);
        var state = record == null ? MadeState.Gone : LootFilterStore.StateOf(record);
        // Edits made in the game's editor become the page's own choices, so the page shows the filter as it is: from its file
        // once the game has saved them, and from the filter in use before that.
        if (state == MadeState.Edited && TakeInGameEdits(build, plan, record!) is { } taken)
        {
            plan = taken;
            state = MadeState.Unchanged;
            s_liveSeen = null;
        }
        else if (state == MadeState.Unchanged && TakeInLiveEdits(build, plan, record!) is { } live)
        {
            plan = live;
        }
        var now = state == MadeState.Unchanged ? CachedSnapshot(plan) : null;
        // The page differs from the file only by what the game's copy in use already has (an edit in the game's editor,
        // or a page change undone): the file is written to match at once. That changes nothing in play, so it needs no
        // Update; page changes the game doesn't have yet keep the file as it is, for the player's own Update.
        if (now != null && record!.PlanHash != now.Fingerprint && s_liveSeen is { } inUse && inUse.File == record.File
            && LootFilterGame.LiveFilterFor(record) != null && GameCopyDiffers(inUse.Seen, now).Count == 0
            && inUse.Seen.Rules.Count == now.Rules.Count && !EaBaseMissing(plan) && SameAsInUseCached(plan, record) && LootFilterGame.Make(plan).Saved != null)
        {
            s_liveSeen = null;
            s_liveQuick = default;
            record = LootFilterStore.RecordFor(build.Id, plan.StageKey) ?? record;
            state = LootFilterStore.StateOf(record);
        }
        s_filterSave = state switch
        {
            MadeState.Gone => (FilterSave.New, null, baseEdited),
            MadeState.Edited => (FilterSave.Edited, record, baseEdited),
            _ => (record!.PlanHash == now!.Fingerprint ? FilterSave.Saved : FilterSave.Changed, record, baseEdited),
        };
        // Saved, but the filter in use has edits its editor didn't save that the page doesn't share (the page changed back
        // after): Update makes the file and the game's copy the page's.
        if (s_filterSave.State == FilterSave.Saved && record != null && now != null && s_liveSeen is { } seen && seen.File == record.File
            && LootFilterGame.LiveFilterFor(record) != null && GameCopyDiffers(seen.Seen, now) is { Count: > 0 } differs)
        {
            s_filterSave = (FilterSave.Changed, record, s_filterSave.BaseEdited);
            s_filterChanges = differs;
            string file = Path.GetFileNameWithoutExtension(record.File);
            var copyStatus = new FilterStatus($"● The game's copy of {file}, in use, differs from this page · Update will make it match", StatusState.Waiting, file);
            if (copyStatus != s_status) s_status = copyStatus;
            return;
        }
        // Listed again only when the page's snapshot or the saved one changed.
        if (s_filterSave.State == FilterSave.Changed && record?.Snapshot is { } saved)
        {
            var inGame = s_liveSeen is { } copy && copy.File == record.File && LootFilterGame.LiveFilterFor(record) != null ? copy.Seen : null;
            if (!ReferenceEquals(s_changesFor.Now, now) || !ReferenceEquals(s_changesFor.Saved, saved) || !ReferenceEquals(s_changesFor.InGame, inGame))
                s_changesFor = (now, saved, inGame, PendingChanges(saved, now!, inGame));
            s_filterChanges = s_changesFor.Changes;
        }
        else
        {
            s_filterChanges = s_filterSave.State == FilterSave.Saved && record != null ? record.GameNotes : s_noChanges;
        }
        var status = MakeStatus();
        if (status != s_status) s_status = status;
    }

    // The filter in use as the page last looked at it (each rule's on or off, outcome and look), for the file it belongs to.
    // An edit is a change since that look, so a change made on the page meanwhile isn't undone by an older one in the game.
    private static (string File, PlanSnapshot Seen)? s_liveSeen;
    private static (string? File, int Quick) s_liveQuick;

    // The filter in use is this stage's made filter and its editor changed a rule since the last look: the change is taken
    // into the stage's choices. The first look (page opened, or just saved) compares with what was saved, so edits made
    // while the page was closed come in too. The plan as it now is, or null when nothing new was found.
    private static FilterPlan? TakeInLiveEdits(Build build, FilterPlan plan, MadeRecord record)
    {
        if (LootFilterGame.LiveFilterFor(record) is not { } live) return null;
        // The full comparison builds each planner rule; it runs only when a quick look shows the filter in use changed.
        int quick = HashCode.Combine(LootFilterGame.QuickSignature(live), record.PlanHash);
        if (s_liveQuick == (record.File, quick)) return null;
        s_liveQuick = (record.File, quick);
        var choices = LootFilterStore.ChoicesFor(build.Id, plan.StageKey);
        bool looked = s_liveSeen is { } last && last.File == record.File;
        var since = looked ? s_liveSeen!.Value.Seen : record.Snapshot;
        // Conditions are compared only with a copy seen before: against the saved file they can't tell an edit in the game
        // from a change on the page not saved yet.
        LootFilterGame.ImportFrom(live, plan, since, choices, looked ? LootFilterGame.ConditionCheck.ToSeen : LootFilterGame.ConditionCheck.None, null,
            out _, out var seen, out int edited);
        s_liveSeen = (record.File, seen);
        if (edited == 0) return null;
        LootFilterStore.Changed(build.Id);
        LootFilterStore.Flush(now: true);
        s_filterVersion++;
        Toasts.Show("planner", $"Loot filter: edits from the game's editor taken in ({Path.GetFileNameWithoutExtension(record.File)})", ToastKind.Info);
        return PlanFor(build, plan.StageKey, choices, LootFilterGame.Limit);
    }

    // Rules whose on or off, outcome or look in the game's copy differ from the page's, by rule number.
    private static List<string> GameCopyDiffers(PlanSnapshot game, PlanSnapshot page)
    {
        var list = new List<string>();
        var inGame = new Dictionary<string, SnapRule>();
        foreach (var rule in game.Rules) inGame.TryAdd(rule.Key, rule);
        for (int i = 0; i < page.Rules.Count; i++)
        {
            var rule = page.Rules[i];
            if (!inGame.TryGetValue(rule.Key, out var copy)) continue;
            var what = new List<string>();
            if (copy.On != rule.On) what.Add(copy.On ? "on in the game's copy" : "off in the game's copy");
            if (copy.Hide != rule.Hide) what.Add(copy.Hide ? "hides in the game's copy" : "shows in the game's copy");
            // Base rules take their look from the base's own file.
            if (copy.Look != rule.Look && !rule.Key.StartsWith("base.", StringComparison.Ordinal)) what.Add("other look in the game's copy");
            if (what.Count > 0) list.Add($"#{i + 1} {rule.Name}: {string.Join(", ", what)}");
        }
        return list;
    }

    // EA · Base is the base but its file isn't there (deleted from the game's list): Make would make it again, which is the
    // player's to ask for, so nothing is written by itself.
    private static bool EaBaseMissing(FilterPlan plan) =>
        plan.Base == EaBaseName && (LootFilterStore.RecordFor(string.Empty, "base") is not { } record || LootFilterStore.StateOf(record) == MadeState.Gone);

    // The full comparison builds the filter; kept for the plan and the filter in use it was made for.
    private static (FilterPlan? Plan, int Quick, bool Same) s_sameFor;

    private static bool SameAsInUseCached(FilterPlan plan, MadeRecord record)
    {
        if (LootFilterGame.LiveFilterFor(record) is not { } live) return false;
        int quick = LootFilterGame.QuickSignature(live);
        if (ReferenceEquals(s_sameFor.Plan, plan) && s_sameFor.Quick == quick) return s_sameFor.Same;
        bool same = LootFilterGame.SameAsInUse(plan, Path.GetFileNameWithoutExtension(record.File));
        s_sameFor = (plan, quick, same);
        return same;
    }

    private static Dictionary<string, RuleChoice> CopyRules(Dictionary<string, RuleChoice> rules) =>
        rules.ToDictionary(p => p.Key, p => new RuleChoice { Enabled = p.Value.Enabled, Hide = p.Value.Hide, Look = p.Value.Look?.Copy() });

    // Reads the edited file into the stage's choices and records the file as matching the page again; the plan as it now
    // is, or null when the file couldn't be read (the page then says it was edited, and Make asks).
    private static FilterPlan? TakeInGameEdits(Build build, FilterPlan plan, MadeRecord record)
    {
        var choices = LootFilterStore.ChoicesFor(build.Id, plan.StageKey);
        if (LootFilterGame.ImportEdits(plan, record, choices) is not { } notes) return null;
        LootFilterStore.Changed(build.Id);
        LootFilterStore.Flush(now: true);
        var now = PlanFor(build, plan.StageKey, choices, LootFilterGame.Limit);
        string? hash = LootFilterGame.PathOf(Path.GetFileNameWithoutExtension(record.File)) is { } path ? LootFilterGame.HashFile(path) : null;
        if (hash == null) return null;
        LootFilterStore.TookIn(record, hash, SnapshotOf(now), notes);
        s_liveSeen = null;
        s_liveQuick = default;
        s_filterVersion++;
        s_filterResult = null;
        Toasts.Show("planner", $"Loot filter: edits from the game's editor taken in ({Path.GetFileNameWithoutExtension(record.File)})", ToastKind.Info);
        return now;
    }

    // The shown plan's snapshot, made again only for a new plan or when its base file changed.
    private static (FilterPlan? Plan, string BaseHash, PlanSnapshot? Snapshot) s_snapshotFor;

    // How the base choice names "no base".
    private const string NoBaseLabel = "No base (planner rules only)";

    private static PlanSnapshot CachedSnapshot(FilterPlan plan)
    {
        string? baseFile = plan.Base == NoBase ? null : plan.Base == EaBaseName ? LootFilterStore.RecordFor(string.Empty, "base")?.File : plan.Base + ".xml";
        string baseHash = baseFile != null && LootFilterGame.FilterFolder is { } folder ? LootFilterGame.HashFileCached(Path.Combine(folder, baseFile)) ?? string.Empty : string.Empty;
        if (ReferenceEquals(s_snapshotFor.Plan, plan) && s_snapshotFor.BaseHash == baseHash && s_snapshotFor.Snapshot is { } kept) return kept;
        var snapshot = SnapshotOf(plan);
        s_snapshotFor = (plan, baseHash, snapshot);
        return snapshot;
    }

    private static IEnumerable<(string Label, string Key, bool Shown, bool Enabled, string Tip, Action Click)> FooterButtons(Build build, FilterPlan? plan)
    {
        string key = FilterStageKey(build);
        var (state, record, baseEdited) = s_filterSave;
        string? blocked = plan == null ? "No plan" : BaseFillsLimit(plan) ? "The base alone fills the game's rule limit" : null;
        string file = record != null ? Path.GetFileNameWithoutExtension(record.File) : plan?.Name ?? string.Empty;
        var (label, tip) = state switch
        {
            FilterSave.Saved when record!.GameNotes.Count > 0 => ("Update", $"Updates the in-game filter {file} to this page, undoing the edits listed below"),
            FilterSave.Saved => ("Up to date", $"The in-game filter {file} already matches this page; change an option to update it"),
            FilterSave.Changed => ("Update", LootFilterGame.ActiveName == file
                ? $"Updates the in-game filter {file} to this page: its file, and the game's filter in use now"
                : $"Updates the in-game filter {file} to this page, in the game's filter list; the filter in use stays as it is"),
            FilterSave.Edited => ("Make", $"The in-game filter {file} was edited in the game's editor; Make asks before replacing it"),
            _ => ("Make", "Adds this filter to the game's filter list; the filter in use stays as it is"),
        };
        bool upToDate = state == FilterSave.Saved && record?.GameNotes.Count == 0;
        yield return (label, "make", true, blocked == null && !upToDate, blocked ?? tip, () => MakeFilter(build));
        bool usable = record != null;
        bool inUse = usable && LootFilterGame.ActiveName == file;
        yield return (inUse ? "In use" : "Use now", "use", usable, usable && !inUse,
            inUse ? $"The game filters with {file} now" : $"Makes {file} the filter the game uses, as picking it in the game's loot filter panel does", () => UseMadeFilter(build));
        yield return ("Reset", "reset", true, !LootFilterStore.ChoicesFor(build.Id, key).Empty, "Brings every option on this page back to the defaults", () =>
        {
            LootFilterStore.Reset(build.Id, key);
            s_filterOpen.Clear();
            FilterChanged(build);
        });
        yield return ("Restore EA · Base", "restore", baseEdited, baseEdited, "Makes EA · Base again from the mod's rules, replacing the edits made to it", () => s_restoreAsk = true);
    }

    // The status under the buttons: the state's mark and words in its colour.
    /// <summary>The status under the buttons: its words, its state (for the colour) and the filter's name within the words,
    /// drawn apart from them. Made when the save state is read, not per frame.</summary>
    private sealed record FilterStatus(string Text, StatusState State, string? Name);

    private static FilterStatus? s_status;
    // The status drawn as rich text, for the theme it was made in.
    private static (FilterStatus? For, int Theme, string Rich) s_statusRich;

    private static FilterStatus? SaveStatus() => s_status;

    private static FilterStatus? MakeStatus()
    {
        var (state, record, _) = s_filterSave;
        if (record == null)
            return state == FilterSave.New && s_filterPlan is { } plan
                ? new($"Not made yet · Make will add {plan.Name} to the game's filter list{(EaBaseMissing(plan) ? " and recreate EA · Base" : string.Empty)}", StatusState.Off, plan.Name)
                : null;
        string file = Path.GetFileNameWithoutExtension(record.File);
        string when = record.Made.ToLocalTime().ToString("HH:mm");
        string rules = record.Rules > 0 ? $" · {record.Rules} rules" : string.Empty;
        // In use, an Update also changes what the game filters with right now.
        bool inUse = LootFilterGame.ActiveName == file;
        string where = inUse ? "in use" : "in the game's filter list";
        int n = s_filterChanges.Count;
        string them = n == 1 ? "it" : "them";
        string update = inUse ? $"Update will save {them} and apply {them} now" : $"Update will save {them} to the game's filter list";
        string inUseMark = inUse ? " (in use)" : string.Empty;
        return state switch
        {
            FilterSave.Saved when record.GameNotes.Count > 0 =>
                new($"● In-game filter {file} holds edits from the game's editor this page can't show · Update will undo them", StatusState.Holding, file),
            FilterSave.Saved => new($"✓ In-game filter {file} up to date · updated {when}{rules} · {where}", StatusState.Active, file),
            FilterSave.Changed => new(record.Snapshot != null && n > 0
                ? $"● {n} {(n == 1 ? "change" : "changes")} not yet in the in-game filter {file}{inUseMark} · {update}"
                : $"● Changed since {when}, not yet in the in-game filter {file}{inUseMark} · {(inUse ? "Update will save it and apply it now" : "Update will save it to the game's filter list")}",
                StatusState.Waiting, file),
            FilterSave.Edited => new($"● In-game filter {file} was edited in the game's editor since {when}", StatusState.Holding, file),
            _ => null,
        };
    }

    // The words in the state's colour, the filter's name bold in the text colour.
    private static string StatusRich(FilterStatus status)
    {
        if (s_statusRich.For == status && s_statusRich.Theme == Theme.Builds) return s_statusRich.Rich;
        var colour = status.State == StatusState.Off ? Theme.Dim : Theme.StateText(status.State);
        int at = status.Name is { Length: > 0 } name ? status.Text.IndexOf(name, StringComparison.Ordinal) : -1;
        string rich = at < 0
            ? Widgets.Colored(status.Text, colour)
            : Widgets.Colored(status.Text[..at], colour) + "<b>" + Widgets.Colored(status.Name!, Theme.Text) + "</b>" + Widgets.Colored(status.Text[(at + status.Name!.Length)..], colour);
        s_statusRich = (status, Theme.Builds, rich);
        return rich;
    }

    private static GUIStyle? s_filterStatus;
    private const float FilterStatusHeight = 22f, FilterChangeHeight = 19f;
    // The changes an Update would save, listed under the status; the rest counted.
    private const int FilterChangesShown = 10;
    private static List<string> s_filterChanges = new();
    private static readonly List<string> s_noChanges = new();
    private static (PlanSnapshot? Now, PlanSnapshot? Saved, PlanSnapshot? InGame, List<string> Changes) s_changesFor = (null, null, null, new List<string>());

    private static int ChangeLines => s_filterChanges.Count == 0 ? 0 : Math.Min(s_filterChanges.Count, FilterChangesShown) + (s_filterChanges.Count > FilterChangesShown ? 1 : 0);

    private static float FooterHeight() => RowLayout.ControlHeight + RowLayout.RowGap + (SaveStatus() != null ? FilterStatusHeight : 0f) + ChangeLines * FilterChangeHeight
        + (ChangeLines > 0 ? 4f : 0f);

    private static void DrawFilterFooter(Rect rect, object? data)
    {
        if (data is not Build build) return;
        float x = rect.x;
        foreach (var (label, key, shown, enabled, tip, click) in FooterButtons(build, s_filterPlan))
        {
            if (!shown) continue;
            float w = Controls.ButtonWidth(label) + 20f;
            var r = new Rect(x, rect.y, w, RowLayout.ControlHeight);
            bool was = GUI.enabled;
            GUI.enabled = was && enabled;
            if (Controls.Button(r, label, "planner.filter." + key, primary: key == "make" && enabled, tip: tip)) click();
            GUI.enabled = was;
            x += w + 6f;
        }
        if (Event.current.type != EventType.Repaint || SaveStatus() is not { } status) return;
        s_filterStatus ??= new GUIStyle(Widgets.Style(() => Theme.Label, GUI.skin.label)) { richText = true, wordWrap = false };
        var line = new Rect(rect.x, rect.y + RowLayout.ControlHeight + 4f, rect.width, FilterStatusHeight);
        string text = Widgets.Themed ? StatusRich(status) : status.Text;
        Widgets.Paint(line, text, s_filterStatus);
        if (s_filterChanges.Count == 0) return;
        // Each change an Update would save, under the status in the hint colour.
        var hint = Widgets.Style(() => Theme.Hint, GUI.skin.label);
        float y = line.yMax + 2f;
        for (int i = 0; i < ChangeLines; i++)
        {
            string change = i < FilterChangesShown && i < s_filterChanges.Count ? "• " + s_filterChanges[i] : $"and {s_filterChanges.Count - FilterChangesShown} more";
            var r = new Rect(rect.x + 14f, y, rect.width - 14f, FilterChangeHeight);
            Widgets.Paint(r, Controls.Fit(change, hint, r.width), hint);
            y += FilterChangeHeight;
        }
    }

    private static bool BaseFillsLimit(FilterPlan plan) => plan.Rules.Count(r => r.Origin != RuleOrigin.Planner) >= plan.Limit;

    // ---- Actions ----

    private static void MakeFilter(Build build)
    {
        // The page's plan holds the player's choices; a fresh one keeps the shown plan as it is.
        var plan = PlanFor(build, FilterStageKey(build), LootFilterStore.ChoicesFor(build.Id, FilterStageKey(build)), LootFilterGame.Limit);
        bool existed = s_filterSave.Record != null;
        var result = LootFilterGame.Make(plan);
        if (result.Ask)
        {
            s_filterAsk = plan;
            s_filterAskText = result.Message;
            s_filterResult = null;
            return;
        }
        Saved(build, result, existed);
    }

    // A save shows in the status line and as a notification; a failure stays on the page.
    private static void Saved(Build build, LootFilterGame.MakeResult result, bool updated = true)
    {
        // What was saved is the new starting point: edits taken in from the game's editor before are in it now.
        if (result.Saved != null)
        {
            s_liveSeen = null;
            s_liveQuick = default;
        }
        s_filterResult = result.Saved == null ? result.Message : null;
        if (result.Saved != null)
            Toasts.Show("planner", $"Loot filter {(updated ? "updated" : "added")}: {result.Saved}{(LootFilterGame.ActiveName == result.Saved ? " (in use)" : string.Empty)}", ToastKind.Info);
        s_filterVersion++;
    }

    private static void ReplaceEdited(Build build)
    {
        var plan = s_filterAsk;
        s_filterAsk = null;
        s_filterAskText = null;
        if (plan != null) Saved(build, LootFilterGame.Make(plan, replaceEdited: true));
        else s_filterVersion++;
    }

    private static void RestoreEaBase()
    {
        s_restoreAsk = false;
        s_filterResult = LootFilterGame.EnsureEaBase(restore: true) is { } name ? $"Restored {name}" : "Not restored: EA · Base couldn't be made";
        s_filterVersion++;
    }

    private static void UseMadeFilter(Build build)
    {
        if (LootFilterStore.RecordFor(build.Id, FilterStageKey(build)) is not { } record) return;
        string name = Path.GetFileNameWithoutExtension(record.File);
        s_filterResult = LootFilterGame.Use(name) ? $"In use: {name}" : $"Not loaded: {name}";
    }

    /// <summary>A build's stage numbers changed: the page's stage pick for it is dropped and its rows made again.</summary>
    private static void StagesMoved(string buildId)
    {
        s_filterStage.Remove(buildId);
        s_filterVersion++;
        s_filterAsk = null;
        s_filterAskText = null;
    }

    /// <summary>
    /// The page's file and live-filter check, timed for the dev bridge: each part and the whole, as mean and worst
    /// milliseconds and bytes allocated per check, over <paramref name="runs"/> runs on the shown page.
    /// </summary>
    internal static List<string> MeasureSaveCheck(int runs)
    {
        var lines = new List<string>();
        if (s_viewing is not { } id || Library.Find(id) is not { } build || s_filterPlan is not { } plan) return new List<string> { "no Loot filter page shown" };
        var record = LootFilterStore.RecordFor(build.Id, plan.StageKey);
        void Time(string name, Action act)
        {
            act();
            var times = new List<double>(runs);
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < runs; i++)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                act();
                times.Add((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
            bytes = (GC.GetAllocatedBytesForCurrentThread() - bytes) / runs;
            times.Sort();
            lines.Add($"{name}: mean {times.Average():0.000} ms, median {times[times.Count / 2]:0.000} ms, worst {times[^1]:0.000} ms, {bytes / 1024.0:0.0} KB allocated");
        }
        lines.Add($"page: {build.Id} stage {plan.StageKey}, {plan.Rules.Count} rules, base {plan.Base}, record {(record != null ? record.File : "none")}");
        if (record != null)
        {
            string? path = LootFilterGame.PathOf(Path.GetFileNameWithoutExtension(record.File));
            Time("file state (read and hash the made file)", () => LootFilterStore.StateOf(record));
            if (path != null) lines.Add($"  made file {new FileInfo(path).Length / 1024} KB");
        }
        Time("plan snapshot (incl. base file hash)", () => SnapshotOf(plan));
        if (record != null && LootFilterGame.LiveFilterFor(record) is { } live)
        {
            Time("filter in use: compare with saved (incl. building each planner rule)", () =>
            {
                var trial = new StageChoices();
                foreach (var (key, change) in CopyRules(LootFilterStore.ChoicesFor(build.Id, plan.StageKey).Rules)) trial.Rules[key] = change;
                LootFilterGame.ImportFrom(live, plan, record.Snapshot, trial, out _);
            });
        }
        else lines.Add("filter in use: not this page's filter (that part is skipped)");
        if (record != null && LootFilterGame.LiveFilterFor(record) is { } inUse)
            Time("filter in use: quick signature", () => LootFilterGame.QuickSignature(inUse));
        Time("whole check, steady (nothing changed)", () => UpdateSaveState(build, plan));
        Time("whole check, first look (no caches)", () =>
        {
            s_snapshotFor = default;
            s_liveQuick = default;
            LootFilterGame.Forget();
            UpdateSaveState(build, plan);
        });
        return lines;
    }

    // ---- Off ----

    /// <summary>Drops the page's plan, rows and prompts, and writes the choices (the switch off, the feature stopping).</summary>
    private static void ForgetFilterPage()
    {
        LootFilterStore.Flush(now: true);
        s_filterPlan = null;
        s_filterRows = new List<MenuRow>();
        s_filterRowsFor = default;
        s_filterRowsHead = null;
        s_filterAsk = null;
        s_filterAskText = null;
        s_restoreAsk = false;
        s_filterResult = null;
        s_liveSeen = null;
        s_liveQuick = default;
        s_snapshotFor = default;
        s_status = null;
        if (s_buildView == BuildView.Filter) s_buildView = BuildView.Checklist;
    }

    /// <summary>The page's frame work: choices written a frame after the last edit, and the page dropped once it's off.</summary>
    private static void FilterFrame()
    {
        LootFilterStore.Flush();
        LootFilterGame.RefreshFrame();
        if (s_filterPlan is { } shown && Clock.Now >= s_filterSaveNext && Menu.Visible && s_viewing is { } viewing && Library.Find(viewing) is { } build
            && ShowingFilter(build)) UpdateSaveState(build, shown);
        if (!LootFilterOffered && (s_filterPlan != null || s_buildView == BuildView.Filter)) ForgetFilterPage();
    }
}
