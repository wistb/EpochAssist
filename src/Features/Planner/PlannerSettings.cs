using EpochAssist.Core;

namespace EpochAssist.Features;

/// <summary>Which side of its node the next point's label sits on.</summary>
internal enum LabelSide { Top, Bottom, Left, Right }

/// <summary>What the next point's label sits on: the game's own point-counter box, a dark plate, or nothing (an outline
/// round the text).</summary>
internal enum LabelBack { Game, Plate, None }

internal static class PlannerSettings
{
    // Stored ids are a contract. Renaming or retyping one raises the version and adds a migration for it. The library
    // and the character bindings are files, not settings, so profiles and share codes never carry them.
    public static readonly SettingsGroup Group = new("EpochAssist_Planner", "Planner", version: 1) { ActionKey = "planner", Module = "planner" };

    public static readonly Section Main = Group.Section("main", "Overlay");
    // On by default: it only draws, and only for a character using a build. The quick label offers it on the HUD panel
    // and the menu's footer.
    public static readonly Toggle Enabled = new(Main, "Enabled", "Planner overlay", true)
    {
        QuickLabel = "Overlay", Help = "Marks the active build on the passive tree, skill trees and skill screens.",
    };

    public static readonly Section Colours = Group.Section("colours", "Colours");
    public static readonly ColorSetting MatchColour = new(Colours, "MatchColour", "Matches", "#3FBF5F");
    public static readonly ColorSetting PartialColour = new(Colours, "PartialColour", "Partial", "#F2992E")
    {
        Help = "Fewer or more points than planned.",
    };
    public static readonly ColorSetting ToPlaceColour = new(Colours, "ToPlaceColour", "To place", "#D94FD9");
    public static readonly ColorSetting OffPlanColour = new(Colours, "OffPlanColour", "Not in build", "#E5484D");

    public static readonly Section Overlay = Group.Section("overlay", "Overlay");
    public static readonly Number OutlineOpacity = new(Overlay, "OutlineOpacity", "Outline opacity", 1f, 0.1f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true,
    };
    public static readonly Toggle ShowMatches = new(Overlay, "ShowMatches", "Show matches", true);
    public static readonly Toggle ShowPartial = new(Overlay, "ShowPartial", "Show partial", true);
    public static readonly Toggle ShowToPlace = new(Overlay, "ShowToPlace", "Show to place", true);
    public static readonly Toggle ShowOffPlan = new(Overlay, "ShowOffPlan", "Show not in build", true);
    public static readonly Toggle HoverHints = new(Overlay, "HoverHints", "Hover hints", true)
    {
        Help = "Hold the pointer on a marked node, slot or skill and the mini HUD says what the build wants there.",
    };
    public static readonly Number HoverDelay = new(Overlay, "HoverDelay", "Hover delay", 0.8f, 0.2f, 3f)
    {
        Step = 0.1f, Slider = true, Unit = " s",
    };
    public static readonly Choice<string> HintPosition = new(Overlay, "HintPosition", "Hint position", "cursor", ("cursor", "At cursor"), ("hud", "Mini HUD"))
    {
        Help = "At cursor sits beside the game's own tooltip when one shows, so neither covers the other.",
    };

    // The look every interface uses: its marks' opacity, outline size and fill, and on the trees the next point. Each
    // interface has its own page under it with its marks switch and, when "Own look" is on, its own copy of the look
    // (bolder marks on small icons than on tree nodes, say). Only the trees have a next point.
    public static readonly PanelLook Interfaces = new(Group.Section("interfaces", "Interfaces"), "Interfaces", next: true);
    public static readonly PanelLook PassiveLook = new(Group.Section("passive", "Passive tree"), "Passive",
        ("PassiveMarks", "Marks planned nodes on the class and mastery trees."), next: true);
    public static readonly PanelLook SkillTreeLook = new(Group.Section("skilltree", "Skill trees"), "SkillTree",
        ("SkillTreeMarks", "Marks planned nodes on each specialised skill's tree."), next: true);
    public static readonly Toggle SkillOffPlanNodes = new(SkillTreeLook.Section, "SkillOffPlanNodes", "Mark nodes of skills not in the build", false)
    {
        Help = "Marks every allocated node of a specialised skill the build doesn't use; otherwise its tree shows only the note.",
    };
    public static readonly PanelLook SkillChoiceLook = new(Group.Section("skillchoice", "Skill selection"), "SkillChoice",
        ("SkillChoiceMarks", "Marks the bar's slots and the planned skill while the skill selection opened from a bar slot is open."));
    public static readonly PanelLook SkillsPageLook = new(Group.Section("skillspage", "Skills & Specializations"), "SkillsPage",
        ("SkillsPageMarks", "Marks each skill and specialisation slot on the Skills & Specializations page."));

    // The inventory's equipment slots and idols, and the blessings panel: icons in the state colours, the planned item on
    // hover. Pages under Interfaces with no look of their own.
    public static readonly Section Inventory = Group.Section("inventory", "Inventory");
    public static readonly Toggle InventoryMarks = new(Inventory, "InventoryMarks", "Show icons", true)
    {
        Help = "Shows an icon at the corner of each equipment slot and idol the build plans; hover it to see the planned item.",
    };
    public static readonly Toggle InventoryMatches = new(Inventory, "InventoryMatches", "Icons on matching slots", true)
    {
        Help = "Off shows icons only where the item isn't the planned one.",
    };
    public static readonly Choice<string> InventoryIconGlyph = new(Inventory, "InventoryIconGlyph", "Icon", "diamond",
        ("diamond", "Diamond"), ("gem", "Gem"), ("star", "Star"))
    {
        Help = "The game's own art, in the state colour.",
        Preview = Planner.GlyphPreview,
    };
    public static readonly Toggle InventoryIconBackground = new(Inventory, "InventoryIconBackground", "Stone background", true)
    {
        Help = "Sets the icon on the game's small round stone button.",
    };
    public static readonly Number InventoryIconOpacity = new(Inventory, "InventoryIconOpacity", "Icon opacity", 1f, 0.2f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true,
    };
    public static readonly Choice<string> InventoryIconCorner = new(Inventory, "InventoryIconCorner", "Icon corner", "topleft",
        ("topright", "Top right"), ("topleft", "Top left"), ("bottomright", "Bottom right"), ("bottomleft", "Bottom left"));
    public static readonly Number InventoryIconSize = new(Inventory, "InventoryIconSize", "Icon size", 24f, 12f, 40f) { Step = 1f, Unit = " px" };
    public static readonly Number InventoryIconOffsetX = new(Inventory, "InventoryIconOffsetX", "Icon offset X", -18f, -20f, 20f) { Step = 1f, Slider = true, Unit = " px" };
    public static readonly Number InventoryIconOffsetY = new(Inventory, "InventoryIconOffsetY", "Icon offset Y", -16f, -20f, 20f) { Step = 1f, Slider = true, Unit = " px" };
    public static readonly Toggle InventoryIdols = new(Inventory, "InventoryIdols", "Icons on idols", true);
    public static readonly Number InventoryIdolIconSize = new(Inventory, "InventoryIdolIconSize", "Idol icon size", 16f, 10f, 40f)
    {
        Step = 1f, Unit = " px", Help = "Idol cells are small: no bigger than a cell, whatever is set.",
    };
    public static readonly Number InventoryIdolOffsetX = new(Inventory, "InventoryIdolOffsetX", "Idol icon offset X", -4f, -20f, 20f) { Step = 1f, Slider = true, Unit = " px" };
    public static readonly Number InventoryIdolOffsetY = new(Inventory, "InventoryIdolOffsetY", "Idol icon offset Y", -4f, -20f, 20f) { Step = 1f, Slider = true, Unit = " px" };
    public static readonly Toggle InventoryAltar = new(Inventory, "InventoryAltar", "Colour the idol altar", true)
    {
        Help = "Colours the ring round the idol altar by the planned altar's state; hover it to see the planned one.",
    };
    public static readonly Number InventoryTooltipDelay = new(Inventory, "InventoryTooltipDelay", "Tooltip delay", 0.15f, 0f, 1f)
    {
        Step = 0.05f, Slider = true, Unit = " s", Help = "How long the pointer rests on an icon before the planned item shows.",
    };
    public static readonly Toggle InventoryHidePrompts = new(Inventory, "InventoryHidePrompts", "Hide prompts on planned items", true)
    {
        Help = "Hides the game's prompts (equip, drop, compare) under a planned item's tooltip; they act on the real item under the pointer.",
    };
    public static readonly Hotkey CompareHotkey = new(Inventory, "CompareHotkey", "Compare with build", string.Empty, HotkeyKind.Press, HotkeyScope.Gameplay)
    {
        Clearable = true, Action = "planner.compare",
        Help = "With an item's tooltip open, shows the build's planned item for that item's slot in its place, against the equipped item.",
    };
    public static readonly Toggle InventoryHints = new(Inventory, "InventoryHints", "Hint card", true)
    {
        Help = "Hovering an icon also shows a card saying what the build wants there.",
    };
    public static readonly Choice<string> InventoryHintPosition = new(Inventory, "InventoryHintPosition", "Card position", "tooltip",
        ("tooltip", "Beside the tooltip"), ("hud", "Mini HUD"));
    public static readonly Toggle InventoryHintMissing = new(Inventory, "InventoryHintMissing", "List affixes", true)
    {
        Help = "Lists the planned item's affixes on the card: each one's tier on the worn item, or missing.",
    };
    public static readonly Toggle InventoryHintWorn = new(Inventory, "InventoryHintWorn", "Name the worn item", true)
    {
        Help = "A card for an item not in the build says what's worn there.",
    };

    /// <summary>The inventory page's headings: each starts its group, before the setting it names.</summary>
    public static readonly Dictionary<Setting, string> InventoryHeadings = new()
    {
        [InventoryMarks] = "Icons", [InventoryIdols] = "Idols", [InventoryTooltipDelay] = "Tooltip", [InventoryHints] = "Hint card",
    };

    public static readonly Section Blessings = Group.Section("blessings", "Blessings");
    public static readonly Toggle BlessingMarks = new(Blessings, "BlessingMarks", "Show marks", true)
    {
        Help = "Colours the ring round each timeline's blessing slot by the planned blessing's state; hover it to see the planned one.",
    };

    public static readonly PanelLook WeaverLook = new(Group.Section("weaver", "Weaver tree"), "Weaver",
        ("WeaverMarks", "Marks planned nodes on the weaver tree."), next: true);

    // Reading, not marking: it follows its own switch, not the overlay's.
    public static readonly Section Guide = Group.Section("guide", "Guide");
    public static readonly Toggle GuideEnabled = new(Guide, "GuideEnabled", "Build guide", true)
    {
        Help = "Shows the planner's own guide for a build: its notes, strengths and weaknesses, with game icons for skills, passives and items.",
    };

    // The defaults of a made filter; each build's own choices on its Loot filter page are build data, not settings.
    public static readonly Section LootFilterSection = Group.Section("lootfilter", "Loot filter");
    public static readonly Toggle LootFilter = new(LootFilterSection, "LootFilter", "Loot filter page", true)
    {
        Help = "Shows the Loot filter page beside a build's checklist and guide, to make a game loot filter from the build's stage.",
    };
    public static readonly Choice<string> LootFilterStrictness = new(LootFilterSection, "LootFilterStrictness", "Default strictness", "regular",
        ("levelling", "Levelling"), ("regular", "Regular"), ("strict", "Strict"), ("verystrict", "Very strict"))
    {
        Help = "Which rules a new filter turns on; each build's page can move it.",
    };
    public static readonly Number LootFilterMinAffixes = new(LootFilterSection, "LootFilterMinAffixes", "Planner affixes per item", 2f, 1f, 4f)
    {
        Step = 1f, Help = "How many of a slot's planner affixes an item needs for the slot's main rule.",
    };
    public static readonly Number LootFilterTierCap = new(LootFilterSection, "LootFilterTierCap", "Tier bar cap", 5f, 1f, 7f)
    {
        Step = 1f, Help = "The highest tier a slot rule asks for; a lower planner tier lowers the slot's bar.",
    };
    public static readonly Number LootFilterLowShards = new(LootFilterSection, "LootFilterLowShards", "Highly needed: fewer shards than", 20f, 0f, 100f)
    {
        Step = 5f, Help = "Items with a planner affix you hold fewer shards of show to shatter, at any tier; 0 turns the rule off.",
    };
    public static readonly Toggle LootFilterHideRest = new(LootFilterSection, "LootFilterHideRest", "Hide other equipment", true)
    {
        Help = "Ends the filter with a rule hiding equipment and idols no rule above shows.",
    };
    public static readonly Toggle LootFilterEmphasizeUniques = new(LootFilterSection, "LootFilterEmphasizeUniques", "Emphasize planner uniques", true)
    {
        Help = "Planner uniques and sets with good potential get the brightest looks and a beam.",
    };
    public static readonly Toggle LootFilterHideOtherClasses = new(LootFilterSection, "LootFilterHideOtherClasses", "Hide items for other classes", true)
    {
        Help = "Adds a rule hiding items only other classes can use.",
    };
    public static readonly Toggle LootFilterHideClassesFirst = new(LootFilterSection, "LootFilterHideClassesFirst", "Hide other classes above uniques", true)
    {
        Help = "The other-classes rule goes first, so even a rare unique for another class is hidden.",
    };
    public static readonly Toggle LootFilterHideAll = new(LootFilterSection, "LootFilterHideAll", "Hide every item kind", true)
    {
        Help = "The last rule hides everything no rule above shows, from level 10 unless Hide from level says otherwise; runes, glyphs, keys, woven echoes, shards and resonances get their own rules.",
    };
    // UTILITY: rule groups: off turns the group's rules off in new filters; each build's page can change it.
    public static readonly Toggle LootFilterUtilityAscend = new(LootFilterSection, "LootFilterUtilityAscend", "Item types to ascend", true)
    {
        Help = "Circle of Fortune items of each type the planner has a unique or set item for.",
    };
    public static readonly Toggle LootFilterUtilityHavoc = new(LootFilterSection, "LootFilterUtilityHavoc", "T7s for a Rune of Havoc", true)
    {
        Help = "Uncorrupted items with a T7 and a build affix, to turn with a Rune of Havoc.",
    };
    public static readonly Toggle LootFilterUtilityClass = new(LootFilterSection, "LootFilterUtilityClass", "Class affixes to shatter", true)
    {
        Help = "Items with a planner class affix at any tier (T4+ at Very strict), for shards.",
    };
    public static readonly Toggle LootFilterUtilityLow = new(LootFilterSection, "LootFilterUtilityLow", "Highly needed affixes", true)
    {
        Help = "Items with a planner affix you're short of shards of (below the count under it), at any tier, loud.",
    };
    public static readonly Number LootFilterShatterTier = new(LootFilterSection, "LootFilterShatterTier", "Build affixes to shatter, from tier", 5f, 0f, 7f)
    {
        Step = 1f, Help = "Items with any planner affix at this tier or higher, for shards, quiet; 0 turns the rule off.",
    };

    /// <summary>The setting giving each switched UTILITY: group's default; the shatter rule has a tier instead.</summary>
    public static Toggle UtilityDefault(UtilityGroup group) => group switch
    {
        UtilityGroup.Ascend => LootFilterUtilityAscend,
        UtilityGroup.Havoc => LootFilterUtilityHavoc,
        UtilityGroup.ClassShatter => LootFilterUtilityClass,
        UtilityGroup.LowShards => LootFilterUtilityLow,
        _ => throw new ArgumentOutOfRangeException(nameof(group), "the shatter rule is set by tier"),
    };

    // EA · Base's rule groups: off turns the group's rules off in filters made on EA · Base.
    public static readonly Toggle LootFilterEveryLegendary = new(LootFilterSection, "LootFilterEveryLegendary", "Every legendary", true)
    {
        Help = "EA · Base's rule showing every legendary item; planner legendaries show either way.",
    };
    public static readonly Toggle LootFilterRarestUniques = new(LootFilterSection, "LootFilterRarestUniques", "Rarest uniques", true)
    {
        Help = "Uniques a drop rerolls into another 98% of the time or more.",
    };
    public static readonly Toggle LootFilterVeryRareUniques = new(LootFilterSection, "LootFilterVeryRareUniques", "Very rare uniques", true)
    {
        Help = "Uniques a drop rerolls into another 80% of the time or more.",
    };
    public static readonly Toggle LootFilterRareRollUniques = new(LootFilterSection, "LootFilterRareRollUniques", "Uniques with rare LP or WW rolls", true)
    {
        Help = "2–3 LP and 18–21 WW on uniques whose LP level makes that roll rare.",
    };
    public static readonly Toggle LootFilterSetSourceUniques = new(LootFilterSection, "LootFilterSetSourceUniques", "Boss and other set-source uniques", true)
    {
        Help = "Uniques that never drop at random: from bosses, dungeons and other set sources.",
    };
    public static readonly Toggle LootFilterPrimordialUniques = new(LootFilterSection, "LootFilterPrimordialUniques", "Primordial uniques", true);
    public static readonly Toggle LootFilterCocoonedUniques = new(LootFilterSection, "LootFilterCocoonedUniques", "Cocooned uniques", true);

    /// <summary>EA · Base rule ids each group switch turns off.</summary>
    public static readonly (Toggle Switch, string[] Ids)[] LootFilterBaseGroups =
    {
        (LootFilterEveryLegendary, new[] { "legendary" }),
        (LootFilterRarestUniques, new[] { "rarest" }),
        (LootFilterVeryRareUniques, new[] { "veryrare", "veryrare.lp1" }),
        (LootFilterRareRollUniques, new[] { "lp3.58", "lp2.100", "ww21", "ww20", "ww19", "ww18", "lp2.70", "lp3.27", "lp2.48" }),
        (LootFilterSetSourceUniques, new[] { "setsource", "setsource.lp1" }),
        (LootFilterPrimordialUniques, new[] { "primordial" }),
        (LootFilterCocoonedUniques, new[] { "cocooned" }),
    };

    /// <summary>The default strictness as a step: Levelling 0, Regular 1, Strict 2, Very strict 3.</summary>
    public static int DefaultStrictness => LootFilterStrictness.Value switch
    {
        "levelling" => 0,
        "strict" => 2,
        "verystrict" => 3,
        _ => 1,
    };

    public static readonly PanelLook[] Panels = { PassiveLook, SkillTreeLook, SkillChoiceLook, SkillsPageLook, WeaverLook };

    /// <summary>The interface pages with no look of their own: the inventory and the blessings panel.</summary>
    public static readonly Section[] GearPages = { Inventory, Blessings };

    /// <summary>The look a panel draws with: its own while "Own look" is on, else Interfaces'.</summary>
    public static PanelLook InUse(PanelLook panel) => panel.Own?.Value == true ? panel : Interfaces;

    internal sealed class PanelLook
    {
        public readonly Section Section;
        /// <summary>The panel's marks switch and its "Own look" switch; null on Interfaces.</summary>
        public readonly Toggle? Marks, Own;
        public readonly Number Opacity, Size, FillOpacity;
        public readonly Toggle Fill;
        /// <summary>The next point's settings; null where the interface has none.</summary>
        public readonly NextPointLook? Next;

        public PanelLook(Section section, string id, (string Id, string Help)? marks = null, bool next = false)
        {
            Section = section;
            if (marks is { } m)
            {
                Marks = new(section, m.Id, "Show marks", true) { Help = m.Help };
                Own = new(section, id + "OwnLook", "Own look", false) { Help = "Uses the settings below instead of Interfaces'." };
            }
            Opacity = new(section, id + "Opacity", "Opacity", 1f, 0.1f, 1f) { Step = 0.05f, Slider = true, Percent = true };
            Size = new(section, id + "OutlineSize", "Outline size", 1f, 0.8f, 1.4f) { Step = 0.05f, Slider = true, Percent = true };
            Fill = new(section, id + "FillTint", "Fill tint", false) { Help = "Also tints the inside faintly in its state colour." };
            FillOpacity = new(section, id + "FillOpacity", "Fill opacity", 0.2f, 0.05f, 0.6f) { Step = 0.05f, Slider = true, Percent = true };
            // The shared ones keep the ids they had on their own page.
            if (next) Next = new NextPointLook(section, marks == null ? string.Empty : id);
        }
    }

    internal sealed class NextPointLook
    {
        public readonly Toggle Steps, Label, OwnColour;
        public readonly Number LabelSize, LabelX, LabelY;
        public readonly Choice<string> LabelSide, LabelBackground, LabelFormat;
        public readonly ColorSetting Colour;

        public NextPointLook(Section section, string id)
        {
            Steps = new(section, id + "NextSteps", "Next steps", true) { Help = "Pulses the node the next point goes into." };
            Label = new(section, id + "NextLabel", "Next point label", true);
            LabelSize = new(section, id + "NextLabelSize", "Label size", 16f, 10f, 28f) { Step = 1f };
            LabelFormat = new(section, id + "NextLabelFormat", "Label format", "slash", ("compare", "1 < 5"), ("slash", "1/5"))
            {
                Help = "1 < 5: points now, then after the next point. 1/5: points now, out of the plan.",
            };
            LabelSide = new(section, id + "NextLabelPosition", "Label position", "bottom",
                ("top", "Top"), ("bottom", "Bottom"), ("left", "Left"), ("right", "Right"));
            LabelBackground = new(section, id + "NextLabelBackground", "Label background", "game",
                ("game", "Game box"), ("plate", "Dark plate"), ("none", "None"))
            {
                Help = "Game box looks like the nodes' own point counters; None outlines the text instead.",
                Sample = Planner.LabelSample,
            };
            LabelX = new(section, id + "NextLabelX", "Label offset X", 0f, -60f, 60f) { Step = 1f, Slider = true };
            LabelY = new(section, id + "NextLabelY", "Label offset Y", 20f, -60f, 60f) { Step = 1f, Slider = true };
            OwnColour = new(section, id + "NextOwnColour", "Own colour for the next point", false);
            Colour = new(section, id + "NextColour", "Next point colour", "#FFD84A");
        }

        public LabelSide Side => LabelSide.Value switch
        {
            "bottom" => Features.LabelSide.Bottom,
            "left" => Features.LabelSide.Left,
            "right" => Features.LabelSide.Right,
            _ => Features.LabelSide.Top,
        };

        public LabelBack Back => LabelBackground.Value switch
        {
            "plate" => LabelBack.Plate,
            "none" => LabelBack.None,
            _ => LabelBack.Game,
        };

        public bool Slash => LabelFormat.Value == "slash";

        public IEnumerable<Setting> All => new Setting[] { Steps, Label, LabelSize, LabelFormat, LabelSide, LabelBackground, LabelX, LabelY, OwnColour, Colour };
    }

    /// <summary>A state's colour; an emptied colour falls back to its default.</summary>
    public static UnityEngine.Color Colour(ColorSetting setting) =>
        setting.TryGetColor(out var color) || ColorSetting.TryParse(setting.Default, out color) ? color : UnityEngine.Color.white;

    public static void Register() => Group.Register();
}
