using EpochAssist.Core;

namespace EpochAssist.Features;

internal static class PlannerSettings
{
    // Stored ids are a contract. Renaming or retyping one raises the version and adds a migration for it. The library
    // and the character bindings are files, not settings, so profiles and share codes never carry them.
    public static readonly SettingsGroup Group = new("EpochAssist_Planner", "Planner", version: 1) { ActionKey = "planner" };

    public static readonly Section Main = Group.Section("main", "Overlay");
    // Off until the player turns it on. No quick label: the planner has no HUD switch.
    public static readonly Toggle Enabled = new(Main, "Enabled", "Planner overlay", false)
    {
        Help = "Marks the active build on the passive tree, skill trees and skill screens.",
    };

    public static readonly Section Marks = Group.Section("marks", "Marks");
    public static readonly Toggle PassiveMarks = new(Marks, "PassiveMarks", "Passive tree", true)
    {
        Help = "Marks planned nodes on the class and mastery trees.",
    };
    public static readonly Toggle SkillTreeMarks = new(Marks, "SkillTreeMarks", "Skill trees", true)
    {
        Help = "Marks planned nodes on each specialised skill's tree.",
    };
    public static readonly Toggle SkillChoiceMarks = new(Marks, "SkillChoiceMarks", "Skill choice", true)
    {
        Help = "Marks skills in the skill selection panel and on the Skills & Specializations page.",
    };
    public static readonly Toggle NextSteps = new(Marks, "NextSteps", "Next steps", true)
    {
        Help = "Pulses the node the next point goes into.",
    };
    public static readonly Number NextCount = new(Marks, "NextCount", "Next steps shown", 5f, 1f, 20f) { Step = 1f };

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
    public static readonly Number OutlineSize = new(Overlay, "OutlineSize", "Outline size", 1f, 0.8f, 1.4f)
    {
        Step = 0.05f, Slider = true, Percent = true,
    };
    public static readonly Toggle FillTint = new(Overlay, "FillTint", "Fill tint", false)
    {
        Help = "Also tints the node's inside faintly in its state colour.",
    };
    public static readonly Number FillOpacity = new(Overlay, "FillOpacity", "Fill opacity", 0.2f, 0.05f, 0.6f)
    {
        Step = 0.05f, Slider = true, Percent = true,
    };
    public static readonly Toggle ShowMatches = new(Overlay, "ShowMatches", "Show matches", true);
    public static readonly Toggle ShowPartial = new(Overlay, "ShowPartial", "Show partial", true);
    public static readonly Toggle ShowToPlace = new(Overlay, "ShowToPlace", "Show to place", true);
    public static readonly Toggle ShowOffPlan = new(Overlay, "ShowOffPlan", "Show not in build", true);

    public static readonly Section NextPoint = Group.Section("next", "Next point");
    public static readonly Toggle NextLabel = new(NextPoint, "NextLabel", "Next point label", true);
    public static readonly Number NextLabelSize = new(NextPoint, "NextLabelSize", "Label size", 14f, 10f, 28f) { Step = 1f };
    public static readonly Toggle HighlightNext = new(NextPoint, "HighlightNext", "Highlight next steps", false)
    {
        Help = "Gives the next steps after the pulsing node a brighter, thicker outline, up to 'Next steps shown'.",
    };
    public static readonly Toggle NextOwnColour = new(NextPoint, "NextOwnColour", "Own colour for the next point", false);
    public static readonly ColorSetting NextColour = new(NextPoint, "NextColour", "Next point colour", "#FFD84A");

    public static readonly Section Panels = Group.Section("panels", "Panels");
    public static readonly Number PassiveOpacity = new(Panels, "PassiveOpacity", "Passive tree opacity", 1f, 0.1f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true,
    };
    public static readonly Number SkillTreeOpacity = new(Panels, "SkillTreeOpacity", "Skill tree opacity", 1f, 0.1f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true,
    };
    public static readonly Number SkillChoiceOpacity = new(Panels, "SkillChoiceOpacity", "Skill choice opacity", 1f, 0.1f, 1f)
    {
        Step = 0.05f, Slider = true, Percent = true,
    };
    public static readonly Toggle SkillOffPlanNodes = new(Panels, "SkillOffPlanNodes", "Mark nodes of skills not in the build", false)
    {
        Help = "Marks every allocated node of a specialised skill the build doesn't use; otherwise its tree shows only the note.",
    };

    // A site's row exists only where its link import is offered, so a release build shows no switch for a site that
    // hasn't agreed.
    public static readonly Section? Sites = PlannerSites.Shown(PlannerSite.Maxroll) || PlannerSites.Shown(PlannerSite.LastEpochTools)
        ? Group.Section("sites", "Sites")
        : null;
    public static readonly Toggle? MaxrollLinks = PlannerSites.Shown(PlannerSite.Maxroll)
        ? new(Sites!, "MaxrollLinks", "Maxroll links", true) { Help = "Pasting a Maxroll link fetches that one build from Maxroll." }
        : null;
    public static readonly Toggle? LetLinks = PlannerSites.Shown(PlannerSite.LastEpochTools)
        ? new(Sites!, "LetLinks", "Last Epoch Tools links", true) { Help = "Pasting a Last Epoch Tools link fetches that one build from Last Epoch Tools." }
        : null;

    /// <summary>A site's links fetch: offered in this build and switched on.</summary>
    public static bool SiteOn(PlannerSite site) =>
        PlannerSites.Shown(site) && ((site == PlannerSite.Maxroll ? MaxrollLinks : LetLinks)?.Value ?? false);

    /// <summary>A state's colour; an emptied colour falls back to its default.</summary>
    public static UnityEngine.Color Colour(ColorSetting setting) =>
        setting.TryGetColor(out var color) || ColorSetting.TryParse(setting.Default, out color) ? color : UnityEngine.Color.white;

    public static void Register() => Group.Register();
}
