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
    public static readonly SettingsGroup Group = new("EpochAssist_Planner", "Planner", version: 1) { ActionKey = "planner" };

    public static readonly Section Main = Group.Section("main", "Overlay");
    // Off until the player turns it on. The quick label offers it on the HUD panel and the menu's footer.
    public static readonly Toggle Enabled = new(Main, "Enabled", "Planner overlay", false)
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

    public static readonly PanelLook[] Panels = { PassiveLook, SkillTreeLook, SkillChoiceLook, SkillsPageLook };

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
                Help = "1 < 5: points now, then after the next point. 1/5: after the next point, out of the plan.",
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
