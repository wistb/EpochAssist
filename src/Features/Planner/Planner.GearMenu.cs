using EpochAssist.Core;
using EpochAssist.Ui;
using Il2Cpp;
using UnityEngine;
using S = EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

internal static partial class Planner
{
    /// <summary>One planned item on the build view: where it goes, its state and words, and the item for the hover.</summary>
    private sealed record GearLine(string Label, string Text, PlannedItem? Item);

    private const string GearBlock = "planner.gear";
    private const float GearLineHeight = 24f;
    private const float GearLabelGap = 12f;

    // The row the pointer rests on in the build view and when the menu last drew it so; the tooltip beside the menu
    // follows it.
    private static PlannedItem? s_menuHover;
    private static float s_menuHoverSeen = -1f;
    private static PlannedItem? s_menuShown;
    private static GUIStyle? s_gearLabel;

    // The build view's gear sections after 018's: Gear, Idols, Blessings, Weaver tree and Weaver items, each only when the
    // stage plans that kind; done out of planned, then one line per planned item in its state's words.
    private static void AddGear(List<MenuRow> rows, Run run, GearGuidance gear)
    {
        if (!MenuBlocks.Has(GearBlock)) MenuBlocks.Register(GearBlock, DrawGear, data => ((List<GearLine>)data!).Select(l => $"{l.Label}: {Plain(l.Text)}"), GearHeight);
        var slots = new List<GearLine>();
        foreach (var (slot, _, _) in GearSlots)
            if (gear.Slots.TryGetValue(slot, out var guide)) slots.Add(new GearLine(SlotName(slot), LineWords(guide), guide.Unknown ? null : guide.Planned));
        AddGearSection(rows, "gear", "Gear", gear.Gear, slots);

        var idols = gear.Idols.OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X)
            .Select(p => new GearLine($"Idol {p.Key.X + 1}, {p.Key.Y + 1}", LineWords(p.Value), p.Value.Unknown ? null : p.Value.Planned)).ToList();
        AddGearSection(rows, "idols", "Idols", gear.IdolList, idols);

        var blessings = gear.Blessings.Select(p => new GearLine($"Timeline {p.Key + 1}", LineWords(p.Value), p.Value.Unknown ? null : p.Value.Planned)).ToList();
        AddGearSection(rows, "blessings", "Blessings", gear.BlessingList, blessings);

        // The weaver tree reads as 018's trees do.
        if (gear.Weaver is { } weaver)
        {
            Func<int, string> name = node => WeaverNodeName(run.Feature, (byte)node);
            AddSection(rows, "weaver", "Weaver tree", weaver.Checklist, name);
            string site = run.Build!.Source == PlannerFormats.LetFormat.Source ? "Last Epoch Tools" : "Maxroll";
            var steps = new List<StepsSection>();
            AddSteps(steps, "weaver", "Weaver tree", weaver, site, name);
            AddStepsRow(rows, "weaver", steps);
        }

        var sockets = gear.WeaverItems.Select(p => new GearLine(SocketName(run.Feature, p.Key), LineWords(p.Value), p.Value.Unknown ? null : p.Value.Planned)).ToList();
        AddGearSection(rows, "weaveritems", "Weaver items", gear.WeaverItemList, sockets);
        if (run.Build is { } build && GearOf(build, run.Stage).MaxrollWeaverItemsSkipped)
            rows.Add(new HintRow(() => "Maxroll weaver items aren't read yet.") { Key = "check.weaveritems.maxroll" });
        // One note for every planned item with a roll made at its best.
        if (gear.Slots.Values.Any(g => g.Planned.RollsFilled) || gear.WeaverItems.Values.Any(g => g.Planned.RollsFilled))
            rows.Add(new HintRow(() => Words.RollsFilled) { Key = "check.gear.rolls" });
    }

    private static void AddGearSection(List<MenuRow> rows, string key, string label, ChecklistSection list, List<GearLine> lines)
    {
        if (lines.Count == 0) return;
        // The same header as the trees' sections, with no fold: the lines show under it.
        AddCheckSection(rows, key, label, new ChecklistSection { Done = list.Done, Total = list.Total }, _ => string.Empty, offPlan: list.OffPlan.Count);
        rows.Add(new BlockRow(GearBlock, lines) { Key = "check." + key + ".items" });
    }

    // A line's words: the state's name in its colour, the planned item, and what's off.
    private static string LineWords(ItemGuide guide)
    {
        if (guide.Unknown) return MenuColoured(UnknownColour, UnknownWords(guide));
        string name = PlannedName(guide.Planned);
        string extra = guide.State == GuidanceState.Partial && guide.Missing.Count > 0
            ? $" (missing {string.Join(", ", guide.Missing.Select(m => $"{AffixName(m.Id)} T{m.Tier + 1}"))})"
            : guide.State == GuidanceState.Partial && guide.Planned.Place == PlannedPlace.Blessing ? " (normal one equipped)"
            : string.Empty;
        return $"{MenuColoured(StateColour(guide.State), StateName(guide.State))}  {name}{extra}";
    }

    private static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);

    private static float GearHeight(object? data, float width) =>
        data is List<GearLine> lines ? Mathf.Ceil(lines.Count / (float)GearColumns(width)) * GearLineHeight : 0f;

    // Two columns side by side on a page wide enough for both.
    private static int GearColumns(float width) => width >= 640f ? 2 : 1;

    // The lines, label then words; the one under the pointer is remembered for the tooltip beside the menu.
    private static void DrawGear(Rect rect, object? data)
    {
        if (data is not List<GearLine> lines) return;
        s_gearLabel ??= new GUIStyle(Theme.Label) { richText = true, wordWrap = false, clipping = TextClipping.Clip };
        // The label column fits the section's longest label (weaver sockets have long names), up to 45% of the width;
        // a label still longer ends in "…", with a gap before the words.
        int columns = GearColumns(rect.width);
        int perColumn = Mathf.CeilToInt(lines.Count / (float)columns);
        float columnWidth = rect.width / columns;
        float widest = 0f;
        foreach (var line in lines) widest = Mathf.Max(widest, TextCache.Width(line.Label, s_gearLabel));
        float labelWidth = Mathf.Min(widest + GearLabelGap, columnWidth * 0.45f);
        var mouse = Event.current.mousePosition;
        for (int i = 0; i < lines.Count; i++)
        {
            // Down the first column, then the second.
            int column = i / Math.Max(1, perColumn);
            var row = new Rect(rect.x + column * columnWidth, rect.y + i % Math.Max(1, perColumn) * GearLineHeight, columnWidth - (column + 1 < columns ? 12f : 0f), GearLineHeight);
            var line = lines[i];
            if (row.Contains(mouse))
            {
                if (Event.current.type == EventType.Repaint) GUI.color = new Color(1f, 1f, 1f, 0.06f);
                GUI.DrawTexture(row, Texture2D.whiteTexture);
                GUI.color = Color.white;
                if (line.Item != null && Event.current.type == EventType.Repaint)
                {
                    s_menuHover = line.Item;
                    s_menuHoverSeen = Clock.Now;
                }
            }
            GUI.Label(new Rect(row.x, row.y, labelWidth - GearLabelGap, row.height), TextCache.Fit(line.Label, s_gearLabel, labelWidth - GearLabelGap), s_gearLabel);
            GUI.Label(new Rect(row.x + labelWidth, row.y, row.width - labelWidth, row.height), line.Text, s_gearLabel);
        }
    }

    // How long a row counts as hovered after the menu last drew it under the pointer.
    private const float MenuHoverGrace = 0.15f;
    private const float MenuTooltipGap = 12f;

    /// <summary>
    /// Every frame: with the pointer on a gear or idol row of the build view, its planned item as the game's tooltip
    /// beside the menu, on the side with room, level with the row; closed when the pointer leaves or the menu closes.
    /// </summary>
    private static void MenuHover(Run run)
    {
        var hovered = Clock.Now - s_menuHoverSeen <= MenuHoverGrace && Menu.Visible ? s_menuHover : null;
        if (hovered == s_menuShown) return;
        if (s_menuShown != null) CloseGearTip();
        s_menuShown = hovered;
        if (hovered == null)
        {
            if (Game.Alive(s_menuAnchor)) s_menuAnchor!.SetActive(false);
            return;
        }
        if (!run.Feature.Has(ItemDataGroup) || !run.Feature.Has(ItemTooltipGroup) || MenuAnchor() is not { } anchor)
        {
            s_menuShown = null;
            return;
        }
        // The game opens a tooltip beside the element it's opened from, and hides one with none: a mod-owned, invisible
        // element just outside the menu, level with the pointer's row, is that element.
        var menu = Menu.ScreenRect;
        bool right = MenuSide();
        var rect = anchor.GetComponent<RectTransform>();
        float y = Screen.height - InputState.Pointer.y;
        // The game centres the tooltip on the element: half the panel's width out from the menu. The panel keeps its
        // width (it exists, parked, while hidden), so it's read before opening.
        float half = TipWidth() / 2f;
        rect.position = new Vector3(right ? menu.xMax + MenuTooltipGap + half : menu.x - MenuTooltipGap - half, Screen.height - y, 0f);
        anchor.SetActive(true);
        if (!OpenPlanned(hovered, anchor, null, alone: true)) s_menuShown = null;
    }

    private static float TipWidth()
    {
        var tip = UITooltipItem.instance?.transform.Find("Content")?.TryCast<RectTransform>();
        if (!Game.Alive(tip)) return 0f;
        tip!.GetWorldCorners(s_tipCorners);
        return s_tipCorners[2].x - s_tipCorners[0].x;
    }

    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> s_tipCorners = new(4);

    // The menu's tooltip anchor: an invisible element on the game's tooltip canvas, made once and kept.
    private static GameObject? s_menuAnchor;
    private const float AnchorSize = 4f;

    private static GameObject? MenuAnchor()
    {
        if (Game.Alive(s_menuAnchor)) return s_menuAnchor;
        var canvas = UITooltipItem.instance?.GetComponentInParent<Canvas>()?.rootCanvas;
        if (!Game.Alive(canvas)) return null;
        var go = new GameObject(Marks.MarkName + ".MenuAnchor");
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(canvas!.transform, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(AnchorSize, AnchorSize);
        // Invisible, so the reload sweep finds it by name.
        var image = go.AddComponent<UnityEngine.UI.Image>();
        image.raycastTarget = false;
        image.color = Color.clear;
        s_menuAnchor = go;
        return go;
    }

    /// <summary>Destroys the menu's tooltip anchor (the feature stopping).</summary>
    private static void ForgetMenuAnchor()
    {
        // A tooltip the menu opened closes with it, or nothing would close it again.
        if (s_menuShown != null) CloseGearTip();
        if (Game.Alive(s_menuAnchor)) UnityEngine.Object.Destroy(s_menuAnchor);
        s_menuAnchor = null;
        s_menuShown = null;
    }


    // The side of the menu with more room: true for its right.
    private static bool MenuSide()
    {
        var menu = Menu.ScreenRect;
        return Screen.width - menu.xMax >= menu.x;
    }

    /// <summary>The build view's row hover, for the dev bridge.</summary>
    internal static string MenuHoverText => $"row {(s_menuHover != null ? PlannedName(s_menuHover) : "none")} seen {Clock.Now - s_menuHoverSeen:0.00}s ago, shown {(s_menuShown != null ? PlannedName(s_menuShown) : "none")}";
}
