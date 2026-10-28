using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;
using S = EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

/// <summary>Which kind of element a mark is on, for its hover hint.</summary>
internal enum HintKind { None, Node, BarSlot, Pick, SpecSlot, PageIcon, EquipSlot, IdolCell, BlessingSlot }

/// <summary>What a marked element is to the plan: a tree node and its guide, a bar or specialisation slot (0-based), the
/// selection pick planned for a slot, or a Skills page skill; or a gear icon: a paper doll slot (its name in AbilityId),
/// an idol cell (y × 5 + x in Slot) or a blessing timeline (Slot), whose hover opens the game's tooltip instead. The words
/// are made only when the hint shows.</summary>
internal readonly record struct MarkHint(HintKind Kind, NodeGuide Guide = default, int Slot = -1, string? AbilityId = null);

internal static partial class Planner
{
    // The marked element under the pointer, since when, and whether its hint is up (with the guidance it was made from).
    private static PlannerMark? s_hovered;
    private static float s_hoverSince;
    private static Guidance? s_hintGuidance;

    /// <summary>
    /// Every tick: while a guided panel is open, the marked element under the pointer; once it has stayed there for the
    /// hover delay, the mini HUD says what the build wants there, until the pointer leaves it. A new comparison (a point
    /// spent) rewrites the words.
    /// </summary>
    private static void Hover(Run run)
    {
        PlannerMark? mark = null;
        // The weaver tree isn't one of the guided panels; its marks show while it's open.
        if (S.Enabled.Value && S.HoverHints.Value && (run.Open != GuidedPanel.None || run.WeaverMarks.Count > 0) && run.Guidance != null)
        {
            // Not through the mod's own menu or HUD, which the game's raycast doesn't see.
            var point = InputState.Pointer;
            var topLeft = new UnityEngine.Vector2(point.x, UnityEngine.Screen.height - point.y);
            // The menu's area counts only while it is open: it keeps its rect when closed.
            bool overMenu = InputGate.Menu && InputGate.MenuHitTest?.Invoke(topLeft) == true;
            if (!overMenu && InputGate.HudHitTest?.Invoke(topLeft) != true) mark = Marks.Under(point);
        }
        if (mark != s_hovered)
        {
            s_hovered = mark;
            s_hoverSince = Clock.Now;
            ClearHint(run);
        }
        if (mark == null || run.Guidance == s_hintGuidance || Clock.Now - s_hoverSince < S.HoverDelay.Value) return;
        s_hintGuidance = run.Guidance;
        if (HintWords(run, mark) is { } words) HudHint.Show(run.Feature, words.Title, words.Text, StateColour(words.State), S.HintPosition.Value == "cursor");
        else HudHint.Clear(run.Feature);
    }

    private static void ClearHint(Run run)
    {
        if (s_hintGuidance == null) return;
        s_hintGuidance = null;
        HudHint.Clear(run.Feature);
    }

    // A hint's words and the state its card is coloured by. Each opens with the state's own name, as the colour settings
    // call it, in its colour; the details follow.
    private static (string Title, string Text, GuidanceState State)? HintWords(Run run, PlannerMark mark)
    {
        if (run.Build is not { } build || run.Guidance is not { } guidance) return null;
        var stage = build.Stages[run.Stage];
        var have = run.Allocation.Bar;
        var hint = mark.Hint;
        string Name(string? id) => string.IsNullOrEmpty(id) ? "nothing" : AbilityName(run.Feature, id!, id!);
        switch (hint.Kind)
        {
            case HintKind.Node:
            {
                var node = Game.Alive(mark.Node) ? mark.Node.GetComponent<SkillTreeNode>() : null;
                string title = Game.Alive(node) && !string.IsNullOrWhiteSpace(node!.nodeName) ? node.nodeName : "Node";
                return (title, Said(hint.Guide.State, NodeWords(hint.Guide)), hint.Guide.State);
            }
            case HintKind.BarSlot when hint.Slot >= 0 && hint.Slot < Stage.BarSlots && guidance.Bar[hint.Slot] is { } state:
            {
                // What belongs here first; the skill in the slot only when it isn't that.
                string? planned = stage.Bar[hint.Slot], now = have[hint.Slot];
                string goesHere = planned != null ? $" {Name(planned)} goes here." : string.Empty;
                string text = state switch
                {
                    GuidanceState.Matches or GuidanceState.ToPlace => $"{Name(planned)}.",
                    GuidanceState.Partial => $"{(planned != null ? Name(planned) : "Nothing")} here; {Name(now)} goes in slot {Array.IndexOf(stage.Bar, now) + 1}.",
                    _ => $"{Name(now)}.{goesHere}",
                };
                return ($"Bar slot {hint.Slot + 1}", Said(state, text), state);
            }
            case HintKind.Pick:
                return (Name(hint.AbilityId), Said(GuidanceState.Matches, $"The build puts this skill in bar slot {hint.Slot + 1}."), GuidanceState.Matches);
            case HintKind.SpecSlot:
            {
                string title = $"Specialisation slot {hint.Slot + 1}";
                if (string.IsNullOrEmpty(hint.AbilityId))
                {
                    var missing = guidance.Skills.Where(s => s.State != GuidanceState.NotInBuild && s.Slot < 0).Select(s => SkillName(run.Feature, s)).ToList();
                    return (title, Said(GuidanceState.ToPlace, string.Join(", ", missing) + "."), GuidanceState.ToPlace);
                }
                if (SkillOf(guidance, hint.AbilityId) is not { } skill) return null;
                string name = SkillName(run.Feature, skill);
                if (skill.State == GuidanceState.NotInBuild) return (title, Said(GuidanceState.NotInBuild, $"{name}."), GuidanceState.NotInBuild);
                string tree = TreeWords(run, stage, skill.AbilityId);
                return skill.PlannedSlot == skill.Slot
                    ? (title, Said(GuidanceState.Matches, $"{name}.{tree}"), GuidanceState.Matches)
                    : (title, Said(GuidanceState.Partial, $"{name} goes in slot {skill.PlannedSlot + 1}.{tree}"), GuidanceState.Partial);
            }
            case HintKind.PageIcon when !string.IsNullOrEmpty(hint.AbilityId):
                return PageWords(run, hint.AbilityId!, guidance, stage, have) is { } page ? (Name(hint.AbilityId), Said(page.State, page.Text), page.State) : null;
            default:
                return null;
        }
    }

    // A specialised skill's tree against the plan, and the skill level that completes it (a level gives one point).
    private static string TreeWords(Run run, Stage stage, string abilityId)
    {
        var plan = stage.Skills.Find(s => s.AbilityId == abilityId);
        if (plan == null || plan.Tree.Total == 0) return string.Empty;
        int spent = run.Allocation.Skills.TryGetValue(abilityId, out var have) ? have.Points.Values.Sum(p => p) : 0;
        return $" Tree: {spent} of {plan.Tree.Total} planned points; complete at level {plan.Tree.Total}.";
    }

    private static string Points(int n) => n == 1 ? "1 point" : $"{n} points";

    // A tree node: its points against the plan, then where it is in the order.
    private static string NodeWords(NodeGuide guide)
    {
        int a = guide.Allocated, p = guide.Planned;
        string state = guide.State switch
        {
            GuidanceState.Matches => $"{a}/{p}, as planned.",
            GuidanceState.Partial when a > p => $"{a} points, {a - p} over the plan's {p}.",
            GuidanceState.Partial => $"{a} of {p} planned points.",
            GuidanceState.ToPlace => $"{Points(p)} planned, none spent yet.",
            _ => a > 0 ? $"{Points(a)} here." : "Not used by the build.",
        };
        if (guide.NextStep == 1) return $"{state} The next point goes here ({a + 1}/{p} after it).";
        return guide.NextStep > 1 ? $"{state} Step {guide.NextStep} of the next steps." : state;
    }

    // A Skills page skill, as its mark reads: by its specialisation slot when the build specialises it, else by its bar
    // slot; where it goes, then where it is when that differs.
    private static (string Text, GuidanceState State)? PageWords(Run run, string id, Guidance guidance, Stage stage, string?[] have)
    {
        int onBar = Array.IndexOf(have, id);
        if (SkillOf(guidance, id) is { } skill)
        {
            if (skill.State == GuidanceState.NotInBuild) return ($"Specialised in slot {skill.Slot + 1}.", GuidanceState.NotInBuild);
            string tree = TreeWords(run, stage, id);
            if (skill.Slot < 0) return ($"Specialisation slot {skill.PlannedSlot + 1}.{tree}", GuidanceState.ToPlace);
            return skill.Slot == skill.PlannedSlot
                ? ($"Specialisation slot {skill.Slot + 1}.{tree}", GuidanceState.Matches)
                : ($"Specialisation slot {skill.PlannedSlot + 1} (now in {skill.Slot + 1}).{tree}", GuidanceState.Partial);
        }
        int planned = Array.IndexOf(stage.Bar, id);
        if (planned >= 0)
            return onBar == planned ? ($"Bar slot {planned + 1}.", GuidanceState.Matches)
                : onBar >= 0 ? ($"Bar slot {planned + 1} (now in {onBar + 1}).", GuidanceState.Partial)
                : ($"Bar slot {planned + 1}.", GuidanceState.ToPlace);
        return onBar >= 0 ? ($"On bar slot {onBar + 1}.", GuidanceState.NotInBuild) : null;
    }

    // The state's own name (its colour setting's label) in its colour, then the details.
    private static string Said(GuidanceState state, string details) =>
        $"<color=#{UnityEngine.ColorUtility.ToHtmlStringRGB(StateColour(state))}>{StateName(state)}:</color> {details}";

    private static string StateName(GuidanceState state) => state switch
    {
        GuidanceState.Matches => S.MatchColour.Label,
        GuidanceState.Partial => S.PartialColour.Label,
        GuidanceState.ToPlace => S.ToPlaceColour.Label,
        _ => S.OffPlanColour.Label,
    };
}
