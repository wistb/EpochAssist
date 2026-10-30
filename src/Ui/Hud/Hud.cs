using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

// The status pill shown while the menu is closed, and its quick-toggle panel. The pill has a section per feature that
// declares a status name, side by side in registration order, wrapping to more rows when it would leave the screen.
internal static class Hud
{
    /// <summary>Distance from the screen edges to the pill; toasts use it too.</summary>
    public const float Margin = 16f;
    private const float PillRow = 26f;
    private const float PillPad = 3f;
    private const float SectionGap = 14f;
    public const float PipSize = 18f;
    /// <summary>How far a slot's edge sits inside the pill's: the pill's padding, then the slot centred in its row.</summary>
    public const float PipInset = PillPad + (PillRow - PipSize) / 2f;
    private const float PanelWidth = 260f;
    private const float PanelChrome = 84f;
    private const float RowHeight = 28f;

    private static bool s_panelOpen;
    // Screen-space areas from the last repaint, for blocking game clicks under the cursor.
    private static Rect s_pillScreen;
    private static Rect s_panelScreen;
    private static Rect s_statsScreen;

    /// <summary>Pill and open panel together, in screen pixels; empty when hidden.</summary>
    public static Rect ScreenRect
    {
        get
        {
            if (s_pillScreen.width <= 0f) return Rect.zero;
            Rect all = s_pillScreen;
            foreach (var r in new[] { s_panelScreen, s_statsScreen })
                if (r.width > 0f) all = Rect.MinMaxRect(Mathf.Min(all.xMin, r.xMin), Mathf.Min(all.yMin, r.yMin), Mathf.Max(all.xMax, r.xMax), Mathf.Max(all.yMax, r.yMax));
            return all;
        }
    }

    public static bool PanelOpen => s_panelOpen;

    public static void TogglePanel() => s_panelOpen = !s_panelOpen;

    /// <summary>Whether a screen point (top-left origin) is on the pill or the open panel; InputGate blocks clicks there.</summary>
    public static bool HitTest(Vector2 screenPoint) =>
        Shown && (s_pillScreen.Contains(screenPoint) || (s_panelOpen && s_panelScreen.Contains(screenPoint)) || s_statsScreen.Contains(screenPoint));

    private static IEnumerable<Feature> StatusFeatures => FeatureHost.All.Where(f => f.StatusName != null && HudSettings.ShowsSection(f.Id));

    // A feature that isn't running keeps its section, greyed; with no section at all (or all switched off) and no session
    // figures there is no pill. It stays up while the menu is open, as a preview that takes no clicks, so a hint can show
    // beside it while settings change.
    private static bool Shown => HudSettings.HudEnabled.Value && Player.Exists && (StatusFeatures.Any() || SessionHud.Shown);

    public static void OnUpdate()
    {
        if (!Shown || Menu.Visible) s_panelOpen = false;
    }

    public static void OnGUI()
    {
        if (!Shown)
        {
            s_pillScreen = s_panelScreen = s_statsScreen = Rect.zero;
            return;
        }

        GUISkin? skin = Theme.Skin;
        if (skin == null) return;

        GUISkin previousSkin = GUI.skin;
        Matrix4x4 previousMatrix = GUI.matrix;
        GUI.skin = skin;
        Themed = true;
        float scale = Mathf.Clamp(LookSettings.UiScale.Value, 0.5f, 3f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        try
        {
            Draw(scale);
        }
        finally
        {
            GUI.matrix = previousMatrix;
            GUI.skin = previousSkin;
        }
    }

    // One section of the pill: where it sits relative to the pill, and what it shows. The session figures' section (In the
    // pill look) has no feature.
    private readonly record struct PillSection(Feature? Feature, FeatureStatus Status, bool Running, GUIContent Label, float LabelWidth, Vector2 At);

    private static void Draw(float scale)
    {
        float screenW = Screen.width / scale, screenH = Screen.height / scale;
        ScreenSpot spot = HudSettings.HudSpot;

        bool stats = SessionHud.Shown;
        var sections = Layout(screenW - Margin * 2f, stats && SessionHud.Inline, out Vector2 size);
        bool pillShown = sections.Count > 0;
        Vector2 statsSize = stats && !SessionHud.Inline ? SessionHud.Size(screenW - Margin * 2f) : Vector2.zero;
        // The card or tiles and the pill are sections too: the first in the player's order sits at the HUD's place, the
        // other next to it, lined up with its side (or centre) and away from the screen edge.
        bool statsFirst = statsSize.x > 0f && (!pillShown || StatsFirst());
        Vector2 firstSize = statsFirst ? statsSize : size;
        var first = Offset(spot.Place(firstSize, screenW, screenH, Margin), screenW, screenH);
        Rect Next(Vector2 next) => new(
            spot.Horizontal < 0 ? first.x : spot.Horizontal > 0 ? first.xMax - next.x : first.center.x - next.x / 2f,
            spot.Vertical > 0 ? first.y - 8f - next.y : first.yMax + 8f, next.x, next.y);
        var pill = !pillShown ? Rect.zero : statsFirst ? Next(size) : first;
        Rect statsRect = statsSize.x <= 0f ? Rect.zero : statsFirst ? first : Next(statsSize);
        // The panel and hints open past the card.
        Rect anchor = statsRect.width > 0f && pillShown ? Rect.MinMaxRect(Mathf.Min(pill.xMin, statsRect.xMin), Mathf.Min(pill.yMin, statsRect.yMin),
            Mathf.Max(pill.xMax, statsRect.xMax), Mathf.Max(pill.yMax, statsRect.yMax)) : statsRect.width > 0f ? statsRect : pill;

        var reasons = sections.Where(x => !x.Running && x.Feature != null).Select(x => new GUIContent(Menu.Reason(x.Feature!))).ToList();
        float reasonsHeight = reasons.Sum(r => Theme.Hint.CalcHeight(r, PanelWidth - 24f) + 6f);
        float panelHeight = PanelChrome + reasonsHeight + HudSettings.PanelToggles.Count() * RowHeight;
        // The panel lines up with the pill's side (or centre) and opens away from the screen edge: up from the bottom.
        float panelX = spot.Horizontal < 0 ? anchor.x : spot.Horizontal > 0 ? anchor.xMax - PanelWidth : anchor.center.x - PanelWidth / 2f;
        var panel = new Rect(panelX, spot.Vertical > 0 ? anchor.y - 8f - panelHeight : anchor.yMax + 8f, PanelWidth, panelHeight);

        Event e = Event.current;
        if (s_panelOpen && e.type == EventType.MouseDown && !panel.Contains(e.mousePosition) && !pill.Contains(e.mousePosition) && !statsRect.Contains(e.mousePosition))
        {
            s_panelOpen = false;
        }

        bool hover = pill.Contains(e.mousePosition) || statsRect.Contains(e.mousePosition) || (s_panelOpen && panel.Contains(e.mousePosition));
        Color previousColor = GUI.color;
        // The preview shows the opacity setting and takes no clicks.
        bool preview = Menu.Visible;
        GUI.color = new Color(1f, 1f, 1f, hover && !preview ? 1f : Mathf.Clamp(HudSettings.HudOpacity.Value, 0.2f, 1f));
        try
        {
            if (pillShown)
            {
                DrawPill(pill, sections, anchor, hover && !preview, spot.Vertical <= 0, !s_panelOpen);
                if (!preview && GUI.Button(pill, GUIContent.none, GUIStyle.none)) Actions.Run("hud.togglePanel", "HUD pill");
            }
            // The card opens the quick panel too.
            if (statsRect.width > 0f)
            {
                // A figure's expanded view opens away from the screen edge, never over the quick panel.
                SessionHud.Draw(statsRect, anchor, hover && !preview, below: spot.Vertical <= 0, detail: !s_panelOpen);
                if (!preview && GUI.Button(statsRect, GUIContent.none, GUIStyle.none)) Actions.Run("hud.togglePanel", "HUD session figures");
            }
            if (s_panelOpen) DrawPanel(panel, reasons);
            else if (HudHint.Text is { } hint) DrawHint(anchor, spot, HudHint.Title, hint, HudHint.Accent);
        }
        finally
        {
            GUI.color = previousColor;
        }

        // An empty pill is kept as the card, so the rest of the mod still sees the HUD's area.
        s_pillScreen = Scaled(pillShown ? pill : statsRect, scale);
        s_panelScreen = s_panelOpen ? Scaled(panel, scale) : Rect.zero;
        s_statsScreen = statsRect.width > 0f ? Scaled(statsRect, scale) : Rect.zero;
    }

    // Whether the session card or tiles come before the pill's first section in the player's order.
    private static bool StatsFirst()
    {
        var order = HudSettings.SectionOrder.Sort(StatusFeatures.Select(f => f.Id).Append(HudSettings.SessionSection), id => id);
        return order.Count > 0 && order[0] == HudSettings.SessionSection;
    }

    // Sections in rows no wider than maxWidth, in the player's order; a section wider than that still gets a row of its
    // own. The session figures, when they go in the pill, are a section too.
    private static List<PillSection> Layout(float maxWidth, bool session, out Vector2 size)
    {
        var sections = new List<PillSection>();
        float x = 10f, width = 0f;
        int row = 0;
        var entries = StatusFeatures.Select(f => (Feature: (Feature?)f, Label: (GUIContent?)null)).ToList();
        if (session) entries.Add((null, SessionHud.InlineText));
        entries = HudSettings.SectionOrder.Sort(entries, x => x.Feature?.Id ?? HudSettings.SessionSection);
        foreach (var (feature, sessionLabel) in entries)
        {
            bool running = feature == null || feature.State == FeatureState.Running;
            FeatureStatus status = feature == null ? new FeatureStatus(StatusState.Active, string.Empty, null)
                : running ? feature.Status : FeatureStatus.Off(feature.Status.Detail);
            // A badge reads as part of the label, so the section's width includes it.
            var label = sessionLabel ?? new GUIContent(feature!.StatusBadge is { } badge ? $"{feature.StatusName} {badge}" : feature.StatusName);
            float labelWidth = Theme.PillLabel.CalcSize(label).x;
            float sectionWidth = 8f + 8f + labelWidth + (status.Slots is { Length: > 0 } slots ? 8f + slots.Length * PipSize + (slots.Length - 1) * 3f : 0f);
            float end = sectionWidth + (status.Slots != null ? 8f : 12f);
            if (x > 10f && x + end > maxWidth)
            {
                row++;
                x = 10f;
            }
            sections.Add(new PillSection(feature, status, running, label, labelWidth, new Vector2(x, PillPad + row * PillRow)));
            width = Mathf.Max(width, x + end);
            x += sectionWidth + SectionGap;
        }
        size = new Vector2(width, PillPad * 2f + (row + 1) * PillRow);
        return sections;
    }

    // The session section (In the pill) draws itself, a segment per figure, with its hover view.
    private static void DrawPill(Rect pill, List<PillSection> sections, Rect anchor, bool pointer, bool below, bool detail)
    {
        if (Event.current.type != EventType.Repaint) return;

        // Over the session figures only the figure under the pointer lights, not the whole pill.
        Vector2 mouse = Event.current.mousePosition;
        bool overFigures = sections.Any(x => x.Feature == null
            && new Rect(pill.x + x.At.x, pill.y + x.At.y, 8f + 8f + x.LabelWidth, PillRow).Contains(mouse));
        bool hover = pill.Contains(mouse) && !overFigures;
        Theme.DrawSliced(pill, hover ? Theme.Pill.hover.background : Theme.Pill.normal.background, Theme.PillRadius);

        foreach (var section in sections)
        {
            Color previous = GUI.color;
            if (!section.Running) GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * 0.45f);
            FeatureStatus status = section.Status;
            float x = pill.x + section.At.x, centerY = pill.y + section.At.y + PillRow / 2f;
            Menu.DrawState(new Rect(x, centerY - 4f, 8f, 8f), status.State, Theme.StateColor(status.State));
            x += 8f + 8f;
            Theme.PillLabel.normal.textColor = status.State != StatusState.Off ? Theme.Text : Theme.Dim;
            var labelAt = new Rect(x, centerY - PillRow / 2f, section.LabelWidth, PillRow);
            if (section.Feature == null) SessionHud.DrawInline(labelAt, anchor, pointer, below, detail);
            else Widgets.Paint(labelAt, section.Label, Theme.PillLabel);
            x += section.LabelWidth + 8f;

            int count = status.Slots?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                var pip = new Rect(x + i * (PipSize + 3f), centerY - PipSize / 2f, PipSize, PipSize);
                bool toggled = status.Slots![i];
                bool live = toggled && status.State == StatusState.Active;
                Theme.DrawSliced(pip, live ? Theme.PipOn : toggled ? Theme.PipPaused : Theme.PipOff, Theme.PipRadius);
                Theme.Pip.normal.textColor = live ? Theme.OnAccent : toggled ? Theme.Pause : Theme.Dim;
                Widgets.Paint(pip, (i + 1).ToString(), Theme.Pip);
            }
            GUI.color = previous;
        }
    }

    private static void DrawPanel(Rect panel, List<GUIContent> reasons)
    {
        GUILayout.BeginArea(panel, Theme.Panel);
        // Why a greyed section isn't running, before the toggles.
        foreach (var reason in reasons) GUILayout.Label(reason, Theme.Hint);
        GUILayout.Label("QUICK TOGGLES", Theme.CardTitle);
        foreach (var toggle in HudSettings.PanelToggles)
        {
            if (Switch(toggle.Label, toggle.Value) != toggle.Value) Actions.Run(toggle.ActionId, "HUD quick toggle");
        }
        GUILayout.Space(6f);
        if (GUILayout.Button($"Open full menu  ·  {KeySettings.MenuKey.Display}", Theme.SmallButton))
        {
            s_panelOpen = false;
            string? first = FeatureHost.All.FirstOrDefault(f => f.Menu != null)?.Id;
            Actions.Run(first != null ? $"menu.page.{first}" : "menu.page.settings", "HUD open full menu");
        }
        GUILayout.EndArea();
    }

    private const float HintWidth = 300f;

    // A feature's hover note, where the quick panel would open: beside the pill, away from the screen edge. An accent
    // colours its left edge and its title.
    private static void DrawHint(Rect pill, ScreenSpot spot, string? title, string text, Color? accent)
    {
        float inner = HintWidth - Theme.Panel.padding.horizontal;
        string heading = title?.ToUpperInvariant() ?? string.Empty;
        var titleContent = new GUIContent(accent is { } tint ? Colored(heading, tint) : heading);
        var textContent = new GUIContent(text);
        float height = Theme.Panel.padding.vertical + (title != null ? Theme.CardTitle.CalcHeight(titleContent, inner) : 0f)
            + Theme.Hint.CalcHeight(textContent, inner) + 4f;
        float x = spot.Horizontal < 0 ? pill.x : spot.Horizontal > 0 ? pill.xMax - HintWidth : pill.center.x - HintWidth / 2f;
        var card = new Rect(x, spot.Vertical > 0 ? pill.y - 8f - height : pill.yMax + 8f, HintWidth, height);
        if (HudHint.AtCursor) card = ByCursor(card.size);
        // The menu draws over the HUD: with it open, the card moves to its side, the one with room.
        else if (Menu.Visible && Menu.ScreenRect is { width: > 0f } screen)
        {
            float scale = GUI.matrix.m00 > 0f ? GUI.matrix.m00 : 1f;
            var menu = new Rect(screen.x / scale, screen.y / scale, screen.width / scale, screen.height / scale);
            if (card.Overlaps(menu))
                card.x = menu.xMax + 8f + HintWidth <= Screen.width / scale ? menu.xMax + 8f : Mathf.Max(0f, menu.x - 8f - HintWidth);
        }
        // Read over the game's panels, so at full opacity whatever the pill's.
        Color previous = GUI.color;
        GUI.color = Color.white;
        GUILayout.BeginArea(card, Theme.HintCard);
        if (title != null) GUILayout.Label(titleContent, Theme.CardTitle);
        GUILayout.Label(textContent, Theme.Hint);
        GUILayout.EndArea();
        // The accent: a stripe down the card's left edge, its full height, its ends rounded with the card's corners.
        if (accent is { } stripe && Event.current.type == EventType.Repaint)
        {
            GUI.color = stripe;
            Theme.HintStripe.Draw(new Rect(card.x, card.y, Theme.AccentStripeWidth, card.height), false, false, false, false);
        }
        GUI.color = previous;
    }

    private const float CursorGap = 20f, TooltipGap = 8f;

    // A card by the pointer: beside the game's own tooltip when one shows (right, left, below, then above it: the first
    // side with room), else just below and right of the pointer, flipped at the screen's edges. In GUI units.
    private static Rect ByCursor(Vector2 size)
    {
        float scale = GUI.matrix.m00 > 0f ? GUI.matrix.m00 : 1f;
        float w = Screen.width / scale, h = Screen.height / scale;
        var bounds = new Rect(0f, 0f, w, h);
        bool Fits(Rect r) => r.xMin >= 0f && r.yMin >= 0f && r.xMax <= w && r.yMax <= h;
        if (GameTooltips.Shown() is { } shown)
        {
            var tip = new Rect(shown.x / scale, shown.y / scale, shown.width / scale, shown.height / scale);
            // Beside an item and its comparison, the card is a third panel in their row: level with their tops, spaced as
            // they are, on the comparison's side first.
            float gap = TooltipGap;
            bool leftFirst = false;
            if (GameTooltips.Panels is { } panels)
            {
                gap = panels.Gap / scale;
                leftFirst = panels.CompareLeft;
            }
            // Kept on screen: a tooltip the game fits to the full height reports its top a hair above it.
            float top = Mathf.Clamp(GameTooltips.Top is { } level ? level / scale : tip.y, 0f, Mathf.Max(0f, h - size.y));
            Rect right = new(tip.xMax + gap, top, size.x, size.y), left = new(tip.x - gap - size.x, top, size.x, size.y);
            Rect[] sides =
            {
                leftFirst ? left : right,
                leftFirst ? right : left,
                new(tip.x, tip.yMax + TooltipGap, size.x, size.y),
                new(tip.x, tip.y - TooltipGap - size.y, size.x, size.y),
            };
            foreach (var side in sides)
                if (Fits(side)) return side;
        }
        var pointer = InputState.Pointer;
        float px = pointer.x / scale, py = (Screen.height - pointer.y) / scale;
        float cx = px + CursorGap + size.x <= w ? px + CursorGap : px - CursorGap - size.x;
        float cy = py + CursorGap + size.y <= h ? py + CursorGap : py - CursorGap - size.y;
        return new Rect(Mathf.Clamp(cx, bounds.x, Mathf.Max(0f, w - size.x)), Mathf.Clamp(cy, bounds.y, Mathf.Max(0f, h - size.y)), size.x, size.y);
    }

    /// <summary>A rect moved by the player's offsets, then kept on screen.</summary>
    public static Rect Offset(Rect r, float screenW, float screenH)
    {
        r.x = Mathf.Clamp(r.x + HudSettings.HudOffsetX.Value, 0f, Mathf.Max(0f, screenW - r.width));
        r.y = Mathf.Clamp(r.y + HudSettings.HudOffsetY.Value, 0f, Mathf.Max(0f, screenH - r.height));
        return r;
    }

    private static Rect Scaled(Rect r, float scale) => new(r.x * scale, r.y * scale, r.width * scale, r.height * scale);
}
