using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;
using S = EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

// A checklist section of the build view as one fold row: its name, done out of planned and a tally per state; open, its
// open items as bullets in two columns under each state's heading, in the state's colour.
internal static partial class Planner
{
    private const string CheckBlock = "planner.check";
    private const float CheckHeaderHeight = 26f, CheckHeadingHeight = 22f, CheckLineHeight = 20f, CheckIndent = 22f, CheckColumnsFrom = 520f;

    /// <summary>One state's open items: its name, its colour (read when drawn, so a changed colour shows at once) and
    /// the items by name.</summary>
    private sealed record CheckGroup(string Name, Func<Color> Colour, List<string> Items);

    private sealed record CheckSection(string Key, string Label, string Count, StatusState State, List<CheckGroup> Groups);

    // Sections the player opened, for the session; closed at first.
    private static readonly HashSet<string> s_openChecks = new();
    private static GUIStyle? s_checkHeading, s_checkTally;

    private static void AddCheckSection(List<MenuRow> rows, string key, string label, ChecklistSection list, Func<int, string> name, int offPlan = 0)
    {
        if (!MenuBlocks.Has(CheckBlock)) MenuBlocks.Register(CheckBlock, DrawCheck, d => CheckBridge((CheckSection)d!), (d, w) => CheckHeight((CheckSection)d!, w));
        var groups = new List<CheckGroup>();
        void Group(ColorSetting setting, List<int> ids)
        {
            if (ids.Count > 0) groups.Add(new CheckGroup(setting.Label, () => S.Colour(setting), ids.Select(name).ToList()));
        }
        Group(S.ToPlaceColour, list.Missing);
        Group(S.PartialColour, list.Partial);
        Group(S.OffPlanColour, list.OffPlan);
        if (list.Unknown > 0) groups.Add(new CheckGroup("Not in this game version", () => UnknownColour, new List<string> { list.Unknown == 1 ? "1 id" : $"{list.Unknown} ids" }));
        var state = list.Done == list.Total && list.OffPlan.Count + offPlan == 0 ? StatusState.Active : StatusState.Waiting;
        rows.Add(new BlockRow(CheckBlock, new CheckSection(key, label, $"{list.Done} / {list.Total}", state, groups)) { Key = "check." + key });
    }

    /// <summary>A state colour as the build view draws it: lifted to readable contrast on the menu's background, the
    /// same way everywhere in the view (the game's own panels keep the colour as set).</summary>
    private static Color MenuColour(Color colour) => Widgets.Themed ? Readable(colour) : colour;

    private static string MenuColoured(Color colour, string text) => $"<color=#{ColorUtility.ToHtmlStringRGB(MenuColour(colour))}>{text}</color>";

    private static bool CheckOpen(CheckSection section) => section.Groups.Count > 0 && s_openChecks.Contains(section.Key);

    private static int CheckColumns(float width) => width >= CheckColumnsFrom ? 2 : 1;

    private static float CheckHeight(CheckSection section, float width)
    {
        if (!CheckOpen(section)) return CheckHeaderHeight;
        int columns = CheckColumns(width - CheckIndent);
        float height = CheckHeaderHeight;
        foreach (var group in section.Groups) height += CheckHeadingHeight + Mathf.Ceil(group.Items.Count / (float)columns) * CheckLineHeight;
        return height + 6f;
    }

    private static IEnumerable<string> CheckBridge(CheckSection section)
    {
        bool open = CheckOpen(section);
        yield return $"{(section.Groups.Count == 0 ? " " : open ? "▾" : "▸")} {section.Label} {section.Count}{(section.Groups.Count > 0 ? " · " + Tally(section, coloured: false) : string.Empty)}";
        if (!open) yield break;
        foreach (var group in section.Groups)
        {
            yield return "  " + group.Name.ToUpperInvariant();
            foreach (var item in group.Items) yield return "  • " + item;
        }
    }

    // "13 to place · 1 partial", each in its state's colour.
    private static string Tally(CheckSection section, bool coloured) => string.Join(" · ", section.Groups.Select(g =>
    {
        string words = g.Items.Count == 1 && g.Name.StartsWith("Not in this", StringComparison.Ordinal) ? g.Items[0] : $"{g.Items.Count} {g.Name.ToLowerInvariant()}";
        return coloured && Widgets.Themed ? Widgets.Colored(words, MenuColour(g.Colour())) : words;
    }));

    private static void DrawCheck(Rect rect, object? data)
    {
        if (data is not CheckSection section) return;
        var header = new Rect(rect.x, rect.y, rect.width, CheckHeaderHeight);
        bool foldable = section.Groups.Count > 0;
        if (foldable)
        {
            bool clicked = GUI.Button(header, GUIContent.none, GUIStyle.none);
            if (Focus.Item(CheckBlock + "." + section.Key, header) || clicked)
            {
                if (!s_openChecks.Remove(section.Key)) s_openChecks.Add(section.Key);
                return;
            }
        }
        if (Event.current.type != EventType.Repaint) return;

        var label = Widgets.Style(() => Theme.Label, GUI.skin.label);
        bool open = CheckOpen(section);
        if (foldable && Widgets.Themed && header.Contains(Event.current.mousePosition)) Theme.DrawSliced(header, Theme.HoverBg, Theme.CardRadius);
        float x = header.x + 2f;
        if (foldable && Widgets.Themed) Theme.Tinted(new Rect(x, Mathf.Round(header.center.y - 6f), 12f, 12f), open ? Theme.Chevron : Theme.ChevronRight, Theme.Dim);
        x += 16f;
        var stateColour = Theme.StateColor(section.State);
        if (Widgets.Themed) Menu.DrawState(new Rect(x, Mathf.Round(header.center.y - 4f), 8f, 8f), section.State, stateColour);
        x += 14f;
        float countX = header.x + RowLayout.LabelWidth(rect.width) + RowLayout.Gap;
        Widgets.Paint(new Rect(x, header.y, countX - x - 8f, header.height), Controls.Fit(section.Label, label, countX - x - 8f), label);
        string count = Widgets.Themed ? Widgets.Colored(section.Count, Theme.LightText(stateColour)) : section.Count;
        Widgets.Paint(new Rect(countX, header.y, 80f, header.height), count, label);
        if (foldable)
        {
            s_checkTally ??= new GUIStyle(Widgets.Style(() => Theme.Hint, GUI.skin.label)) { wordWrap = false, richText = true, alignment = TextAnchor.MiddleLeft };
            float tallyX = countX + 80f;
            Widgets.Paint(new Rect(tallyX, header.y, header.xMax - tallyX, header.height), Tally(section, coloured: true), s_checkTally);
        }
        if (!open) return;

        s_checkHeading ??= new GUIStyle(Widgets.Style(() => Theme.Heading, GUI.skin.label)) { richText = true };
        float left = rect.x + CheckIndent, width = rect.width - CheckIndent;
        int columns = CheckColumns(width);
        float columnWidth = width / columns, y = header.yMax;
        foreach (var group in section.Groups)
        {
            var colour = group.Colour();
            Widgets.Paint(new Rect(left, y + 6f, width, 14f), Widgets.Themed ? Widgets.Colored(group.Name.ToUpperInvariant(), MenuColour(colour)) : group.Name.ToUpperInvariant(), s_checkHeading);
            y += CheckHeadingHeight;
            int rowsPerColumn = Mathf.CeilToInt(group.Items.Count / (float)columns);
            for (int i = 0; i < group.Items.Count; i++)
            {
                // Down the first column, then the second.
                int column = i / Math.Max(1, rowsPerColumn), line = i % Math.Max(1, rowsPerColumn);
                var cell = new Rect(left + column * columnWidth, y + line * CheckLineHeight, columnWidth - 8f, CheckLineHeight);
                Theme.Fill(new Rect(cell.x + 1f, Mathf.Round(cell.center.y - 2f), 4f, 4f), MenuColour(colour));
                Widgets.Paint(new Rect(cell.x + 12f, cell.y, cell.width - 12f, cell.height), Controls.Fit(group.Items[i], label, cell.width - 12f), label);
            }
            y += rowsPerColumn * CheckLineHeight;
        }
    }

    // ---- Next steps ----

    private const string StepsBlock = "planner.steps";
    private const float StepsCardWidth = 250f;

    /// <summary>A tree's next steps: in order, each node and the points it takes; or the planner's note when it gives no
    /// order.</summary>
    private sealed record StepsSection(string Label, List<(int Number, string Name, int Add)>? Steps, string? Note);

    // The trees' steps as cards side by side, as many as fit the page's width, each numbered down.
    private static void AddStepsRow(List<MenuRow> rows, string key, List<StepsSection> sections)
    {
        if (sections.Count == 0) return;
        if (!MenuBlocks.Has(StepsBlock)) MenuBlocks.Register(StepsBlock, DrawSteps, d => ((List<StepsSection>)d!).SelectMany(StepsBridge), (d, w) => StepsHeight((List<StepsSection>)d!, w));
        rows.Add(new BlockRow(StepsBlock, sections) { Key = "steps." + key });
    }

    private static IEnumerable<string> StepsBridge(StepsSection section)
    {
        yield return section.Label;
        if (section.Steps == null) yield return "  " + section.Note;
        else foreach (var (number, name, add) in section.Steps) yield return $"  {number}. {name} +{add}";
    }

    private static int StepsColumns(float width) => Math.Max(1, (int)(width / StepsCardWidth));

    // A card's height: the tree's name, then a line per step, or the planner's note.
    private static float SectionHeight(StepsSection section, float width) =>
        CheckHeadingHeight + (section.Steps is { } steps ? steps.Count * CheckLineHeight : Controls.HintHeight(section.Note ?? string.Empty, width - CheckIndent)) + 8f;

    private static float StepsHeight(List<StepsSection> sections, float width)
    {
        int columns = StepsColumns(width);
        float columnWidth = width / columns, height = 0f;
        for (int i = 0; i < sections.Count; i += columns)
        {
            float tallest = 0f;
            for (int j = i; j < i + columns && j < sections.Count; j++) tallest = Mathf.Max(tallest, SectionHeight(sections[j], columnWidth));
            height += tallest;
        }
        return height;
    }

    private static void DrawSteps(Rect rect, object? data)
    {
        if (data is not List<StepsSection> sections || Event.current.type != EventType.Repaint) return;
        int columns = StepsColumns(rect.width);
        float columnWidth = rect.width / columns, y = rect.y;
        for (int i = 0; i < sections.Count; i += columns)
        {
            float tallest = 0f;
            for (int j = i; j < i + columns && j < sections.Count; j++)
            {
                var cell = new Rect(rect.x + (j - i) * columnWidth, y, columnWidth - (columns > 1 ? 16f : 0f), SectionHeight(sections[j], columnWidth));
                DrawSection(cell, sections[j]);
                tallest = Mathf.Max(tallest, cell.height);
            }
            y += tallest;
        }
    }

    // The tree's name, then its steps numbered down: the number dim, the node, its points in the to-place colour.
    private static void DrawSection(Rect rect, StepsSection section)
    {
        var label = Widgets.Style(() => Theme.Label, GUI.skin.label);
        Widgets.Paint(new Rect(rect.x, rect.y, rect.width, CheckHeadingHeight), section.Label, label);
        float left = rect.x + CheckIndent, y = rect.y + CheckHeadingHeight, width = rect.width - CheckIndent;
        if (section.Steps is not { } steps)
        {
            RowLayout.Hint(new Rect(left, y, width, rect.yMax - y), section.Note ?? string.Empty);
            return;
        }
        const float numberWidth = 24f, pointsWidth = 40f;
        string? dim = Widgets.Themed ? ColorUtility.ToHtmlStringRGB(Theme.Secondary) : null;
        foreach (var (number, name, add) in steps)
        {
            Widgets.Paint(new Rect(left, y, numberWidth, CheckLineHeight), dim != null ? $"<color=#{dim}>{number}.</color>" : number + ".", label);
            float nameWidth = Mathf.Min(TextCache.Width(name, label), width - numberWidth - pointsWidth);
            Widgets.Paint(new Rect(left + numberWidth, y, nameWidth, CheckLineHeight), Controls.Fit(name, label, nameWidth), label);
            string points = Widgets.Themed ? MenuColoured(S.Colour(S.ToPlaceColour), $"+{add}") : $"+{add}";
            Widgets.Paint(new Rect(left + numberWidth + nameWidth + 8f, y, pointsWidth, CheckLineHeight), points, label);
            y += CheckLineHeight;
        }
    }
}
