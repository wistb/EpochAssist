using UnityEngine;

namespace EpochAssist.Ui;

// The menu's styles and textures (top bar, tree, compact rows, popups), built with the skin from the same values.
internal static partial class Theme
{
    public static Color Secondary => s_values.Secondary;
    public static Color Faint => s_values.Faint;
    public static Color DividerColor => s_values.Divider;
    public static Color Button => s_values.Button;
    public static Color Selection => s_values.Selection;
    public static Color Error => s_values.Error;
    /// <summary>The secondary accent: where you are (selection, list highlight, search matches, chips on).</summary>
    public static Color Highlight => s_values.Highlight;
    public static Color HighlightText => s_values.HighlightText;
    public static Color Window => s_values.Window;
    public static Color SidebarColor => s_values.Sidebar;
    public static int ControlRadius => s_values.RadiusControl;
    public static int CardRadius => s_values.RadiusCard;

    /// <summary>Counts skin builds, so caches of measured text know when sizes may have changed.</summary>
    public static int Builds { get; private set; }

    public static GUIStyle Brand = null!, Version = null!, Tab = null!, TabOn = null!, Crumb = null!, Tag = null!;
    public static GUIStyle TreeRow = null!, TreeNode = null!, TreeNumber = null!, TreeFilter = null!;
    public static GUIStyle Label = null!, FieldText = null!, FieldDim = null!, FieldHint = null!, Heading = null!, BannerText = null!, KeyText = null!;
    public static GUIStyle ButtonText = null!, PrimaryText = null!, OptionText = null!, OptionHeading = null!, Tooltip = null!;
    public static GUIStyle FooterLabel = null!, FooterHint = null!, LiveValue = null!, Glyph = null!;
    // Fills drawn by the menu: flat, so they meet the window's own edge cleanly.
    public static Texture2D Flat = null!, FieldBg = null!, FieldOpen = null!, InputBox = null!, InputFocus = null!, PopupBg = null!;
    public static Texture2D ButtonBg = null!, ButtonHover = null!, PrimaryBg = null!, PrimaryHover = null!, HighlightBg = null!, SelectionBg = null!, HoverBg = null!;
    public static Texture2D SmallOff = null!, SmallOffHover = null!, SmallOn = null!, SmallOnHover = null!;
    public static Texture2D CheckOff = null!, CheckOn = null!, CheckMixed = null!, TickMark = null!, TreeChevron = null!, TreeChevronRight = null!, TreeCollapseAll = null!, TreeExpandAll = null!, StepUp = null!, StepDown = null!, Reset = null!, Cross = null!, CloseCross = null!;
    /// <summary>The mark after a button that opens a page outside the game: a box with an arrow out of its corner.</summary>
    public static Texture2D ExternalLink = null!;
    public static Texture2D TagBg = null!, SidebarFill = null!;

    private static void BuildMenu(GUISkin skin, ThemeValues v)
    {
        Builds++;
        int rControl = v.RadiusControl, rCard = v.RadiusCard;
        Flat = Paint(1, 1, (_, _) => Color.white);
        // The whole window in the bars' colour with its edge; the page draws its own fill over it.
        SidebarFill = Rounded(v.RadiusWindow, v.RadiusWindow, v.Sidebar, v.WindowEdge, 1f);

        Brand = new GUIStyle(skin.label) { fontSize = v.FontBody + 1, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, padding = Offset(0) };
        Version = new GUIStyle(skin.label) { fontSize = v.FontCaption, fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleLeft, padding = Offset(0) };
        TextColor(Version, v.Dim);
        Tab = new GUIStyle(skin.label) { fontSize = v.FontNav, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = Offset(0) };
        TextColor(Tab, v.Secondary);
        Tab.hover.textColor = v.Text;
        TabOn = new GUIStyle(Tab);
        TextColor(TabOn, v.Text);
        Crumb = new GUIStyle(skin.label) { fontSize = v.FontCaption, fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleLeft, padding = Offset(0) };
        TextColor(Crumb, v.Dim);
        Tag = new GUIStyle(skin.label) { fontSize = v.FontCaption, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(18, 7, 0, 1) };
        TagBg = Rounded(Math.Min(rControl, 3), Math.Min(rControl, 3), Color.white, Color.white, 0f);

        TreeRow = new GUIStyle(skin.label) { fontSize = v.FontBody, alignment = TextAnchor.MiddleLeft, padding = Offset(0), clipping = TextClipping.Clip };
        TextColor(TreeRow, v.Secondary);
        TreeNode = new GUIStyle(TreeRow) { fontStyle = FontStyle.Bold };
        TextColor(TreeNode, v.Text);
        TreeNumber = new GUIStyle(TreeRow) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

        Label = new GUIStyle(skin.label) { fontSize = v.FontBody, alignment = TextAnchor.MiddleLeft, padding = Offset(0), wordWrap = false };
        TextColor(Label, v.Text);
        FieldText = new GUIStyle(Label) { fontStyle = FontStyle.Normal, padding = new RectOffset(7, 7, 0, 1), clipping = TextClipping.Clip };
        FieldDim = new GUIStyle(FieldText);
        TextColor(FieldDim, v.Dim);
        // A field's placeholder: readable, in the secondary text colour.
        FieldHint = new GUIStyle(FieldText);
        TextColor(FieldHint, v.Secondary);
        TreeFilter = new GUIStyle(skin.textField)
        {
            fontSize = v.FontSmall, fixedHeight = 0f, padding = new RectOffset(7, 22, 0, 1), margin = Offset(0), alignment = TextAnchor.MiddleLeft,
        };
        Background(TreeFilter, Rounded(rControl, rControl, v.InputBg, v.Divider, 1f));
        TreeFilter.focused.background = Rounded(rControl, rControl, v.InputBg, v.Accent, 1f);
        TreeFilter.onFocused.background = TreeFilter.focused.background;
        Heading = new GUIStyle(skin.label) { fontSize = Math.Max(8, v.FontCaption - 1), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, padding = Offset(0) };
        TextColor(Heading, v.Dim);
        BannerText = new GUIStyle(skin.label) { fontSize = v.FontBody, fontStyle = FontStyle.Normal, wordWrap = true, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 10, 6, 7) };
        TextColor(BannerText, v.Secondary);
        KeyText = new GUIStyle(Label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(8, 8, 0, 1) };
        TextColor(KeyText, v.AccentText);
        ButtonText = new GUIStyle(Label) { fontSize = v.FontSmall, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(10, 10, 0, 1) };
        // Button labels in the main text colour: grey on a grey button was hard to read.
        TextColor(ButtonText, v.Text);
        ButtonText.hover.textColor = v.Text;
        PrimaryText = new GUIStyle(ButtonText);
        TextColor(PrimaryText, v.OnAccent);
        OptionText = new GUIStyle(FieldText) { padding = new RectOffset(8, 22, 0, 1) };
        OptionHeading = new GUIStyle(Heading) { padding = new RectOffset(6, 6, 0, 0) };
        // Headings in a list are quieter than its options but must stay readable on the popup (faint is for lines).
        TextColor(OptionHeading, v.Dim);
        Tooltip = new GUIStyle(skin.label) { fontSize = v.FontSmall, fontStyle = FontStyle.Normal, wordWrap = true, padding = new RectOffset(8, 8, 5, 6) };
        TextColor(Tooltip, v.Secondary);
        FooterLabel = new GUIStyle(Label) { fontSize = v.FontSmall, fontStyle = FontStyle.Normal };
        TextColor(FooterLabel, v.Secondary);
        FooterHint = new GUIStyle(Label) { fontSize = v.FontCaption, fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleRight };
        TextColor(FooterHint, v.Dim);
        LiveValue = new GUIStyle(Label) { alignment = TextAnchor.MiddleRight };
        Glyph = new GUIStyle(skin.label) { fontSize = v.FontBody + 1, fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleCenter, padding = Offset(0) };
        TextColor(Glyph, v.Secondary);
        Glyph.hover.textColor = v.Text;

        FieldBg = Rounded(rControl, rControl, v.Control, v.CardEdge, 1f);
        FieldOpen = Rounded(rControl, rControl, v.Control, v.Accent, 1f);
        InputBox = Rounded(rControl, rControl, v.InputBg, v.InputEdge, 1f);
        InputFocus = Rounded(rControl, rControl, v.InputBg, v.Accent, 1f);
        PopupBg = Rounded(rCard, rCard, v.Control, v.CardEdge, 1f);
        ButtonBg = Rounded(rControl, rControl, v.Button, v.Button, 0f);
        Color buttonHover = Color.Lerp(v.Button, v.Text, 0.08f);
        ButtonHover = Rounded(rControl, rControl, buttonHover, buttonHover, 0f);
        PrimaryBg = Rounded(rControl, rControl, v.Accent, v.Accent, 0f);
        PrimaryHover = Rounded(rControl, rControl, v.AccentHover, v.AccentHover, 0f);
        // A list's hovered or focused item: the muted selection colour, as the tree's selected row, not the bright highlight.
        HighlightBg = Rounded(rControl, rControl, v.Selection, v.Selection, 0f);
        SelectionBg = Rounded(rCard, rCard, v.Selection, v.Selection, 0f);
        HoverBg = Rounded(rCard, rCard, v.NavHover, v.NavHover, 0f);

        SmallOff = SmallSwitch(on: false, v.CardEdge, v.Dim);
        SmallOffHover = SmallSwitch(on: false, Color.Lerp(v.CardEdge, v.Text, 0.1f), v.Text);
        SmallOn = SmallSwitch(on: true, SwitchTrack(v, 0.7f), v.OnAccent);
        SmallOnHover = SmallSwitch(on: true, SwitchTrack(v, 0.82f), v.OnAccent);

        int rCheck = Math.Min(3, rControl + 1);
        CheckOff = Rounded(rCheck, rCheck, Color.clear, v.Faint, 1f);
        CheckOn = Paint(12, 12, (x, y) => Over(Shade(v.OnAccent, Tick(x, y)), Shade(v.Accent, Coverage(BoxDistance(x + 0.5f, y + 0.5f, 12f, 12f, rCheck)))));
        CheckMixed = Paint(12, 12, (x, y) => Over(Shade(v.OnAccent, Coverage(RectDistance(x + 0.5f, y + 0.5f, 6f, 6f, 3f, 0.9f))),
            Shade(v.Accent, Coverage(BoxDistance(x + 0.5f, y + 0.5f, 12f, 12f, rCheck)))));
        TickMark = Paint(12, 12, (x, y) => Shade(Color.white, Tick(x, y)));
        // The tree's expanders: a smaller chevron than the dropdowns', drawn at its own size so it stays sharp.
        TreeChevron = Paint(8, 8, (x, y) => Shade(Color.white, SmallChevron(x + 0.5f, y + 0.5f)));
        TreeChevronRight = Paint(8, 8, (x, y) => Shade(Color.white, SmallChevron(x + 0.5f, y + 0.5f, right: true)));
        // Collapse all and expand all: two chevrons pointing toward each other, or away.
        TreeCollapseAll = Paint(8, 12, (x, y) => Shade(Color.white, DoubleChevron(x + 0.5f, y + 0.5f, inward: true)));
        TreeExpandAll = Paint(8, 12, (x, y) => Shade(Color.white, DoubleChevron(x + 0.5f, y + 0.5f, inward: false)));
        // Stepper arrows, white and tinted when drawn: small triangles (the game's fonts have no arrow glyphs).
        StepUp = Paint(8, 5, (x, y) => Shade(Color.white, Mathf.Clamp01(Triangle(x + 0.5f, y + 0.5f, up: true))));
        StepDown = Paint(8, 5, (x, y) => Shade(Color.white, Mathf.Clamp01(Triangle(x + 0.5f, y + 0.5f, up: false))));
        // Reset: an open circle with a head, white and tinted; Cross: the list row's remove and the field's clear.
        Reset = Paint(12, 12, (x, y) =>
        {
            // Texture rows count from the bottom, so the gap is at the top right; the head sits at the gap's left end.
            float px = x + 0.5f - 6f, py = y + 0.5f - 6f;
            float angle = Mathf.Atan2(py, px) * Mathf.Rad2Deg;
            float ring = angle > 15f && angle < 80f ? 0f : Coverage(Mathf.Abs(Mathf.Sqrt(px * px + py * py) - 4f) - 0.7f);
            float hx = 4f * Mathf.Cos(80f * Mathf.Deg2Rad), hy = 4f * Mathf.Sin(80f * Mathf.Deg2Rad);
            float head = Coverage(CircleDistance(px, py, hx + 0.6f, hy, 1.9f));
            return Shade(Color.white, Mathf.Max(ring, head));
        });
        // The window's close: the dropdown chevron's size and stroke, so it matches the collapse chevron beside it.
        CloseCross = Paint(12, 12, (x, y) =>
        {
            float px = x + 0.5f, py = y + 0.5f;
            float a = Mathf.Abs(px - py) / 1.414f, b = Mathf.Abs(px + py - 12f) / 1.414f;
            float inside = px > 1.5f && px < 10.5f && py > 1.5f && py < 10.5f ? 1f : 0f;
            return Shade(Color.white, inside * Mathf.Clamp01(1.6f - Mathf.Min(a, b)));
        });
        ExternalLink = Paint(10, 10, (x, y) =>
        {
            // Drawn top down; texture rows count from the bottom.
            float px = x + 0.5f, py = 10f - (y + 0.5f);
            float box = Mathf.Min(Mathf.Min(Segment(px, py, 1f, 2.5f, 1f, 9f), Segment(px, py, 1f, 9f, 7.5f, 9f)),
                Mathf.Min(Segment(px, py, 1f, 2.5f, 4f, 2.5f), Segment(px, py, 7.5f, 6f, 7.5f, 9f)));
            float arrow = Mathf.Min(Segment(px, py, 4.5f, 5.5f, 9f, 1f), Mathf.Min(Segment(px, py, 5.5f, 1f, 9f, 1f), Segment(px, py, 9f, 1f, 9f, 4.5f)));
            return Shade(Color.white, Coverage(Mathf.Min(box, arrow) - 0.55f));
        });
        Cross = Paint(10, 10, (x, y) =>
        {
            float px = x + 0.5f, py = y + 0.5f;
            float a = Mathf.Abs(px - py) / 1.414f, b = Mathf.Abs(px + py - 10f) / 1.414f;
            float inside = px > 1.5f && px < 8.5f && py > 1.5f && py < 8.5f ? 1f : 0f;
            return Shade(Color.white, inside * Coverage(Mathf.Min(a, b) - 0.6f));
        });
    }

    // Distance from a point to the segment a–b.
    private static float Segment(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
        float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
        return Mathf.Sqrt(ex * ex + ey * ey);
    }

    // A tick inside a 12 px box: two strokes.
    private static float Tick(int x, int y)
    {
        static float Segment(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
            float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
            return Mathf.Sqrt(ex * ex + ey * ey);
        }
        float px = x + 0.5f, py = y + 0.5f;
        // Texture rows count from the bottom.
        float d = Mathf.Min(Segment(px, py, 3f, 6.5f, 5f, 4f), Segment(px, py, 5f, 4f, 9f, 8.5f));
        return Mathf.Clamp01(1.4f - d);
    }

    // A chevron in an 8 x 8 texture: two strokes meeting low in the middle, or at the right (rows count from the bottom).
    // Two of the tree's chevrons, stacked in an 8 × 12 box (y up): pointing toward the middle, or away from it.
    private static float DoubleChevron(float px, float py, bool inward)
    {
        static float Segment(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
            float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
            return Mathf.Sqrt(ex * ex + ey * ey);
        }
        float Chevron(float tipY, float armY) => Mathf.Min(Segment(px, py, 1.5f, armY, 4f, tipY), Segment(px, py, 4f, tipY, 6.5f, armY));
        float d = inward
            ? Mathf.Min(Chevron(7.5f, 10f), Chevron(4.5f, 2f))
            : Mathf.Min(Chevron(10f, 7.5f), Chevron(2f, 4.5f));
        return Mathf.Clamp01(1.25f - d);
    }

    private static float SmallChevron(float px, float py, bool right = false)
    {
        static float Segment(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
            float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
            return Mathf.Sqrt(ex * ex + ey * ey);
        }
        float d = right
            ? Mathf.Min(Segment(px, py, 2.5f, 6.5f, 5f, 4f), Segment(px, py, 5f, 4f, 2.5f, 1.5f))
            : Mathf.Min(Segment(px, py, 1.5f, 5.5f, 4f, 3f), Segment(px, py, 4f, 3f, 6.5f, 5.5f));
        return Mathf.Clamp01(1.25f - d);
    }

    // Coverage of a triangle filling an 8 x 5 texture, point up or down (rows count from the bottom).
    private static float Triangle(float px, float py, bool up)
    {
        float t = up ? py / 5f : 1f - py / 5f;
        float half = 4f * (1f - t);
        return Mathf.Clamp01(half - Mathf.Abs(px - 4f) + 0.5f);
    }

    private static Texture2D SmallSwitch(bool on, Color track, Color knob)
    {
        const int w = 28, h = 16;
        float knobX = on ? w - h / 2f : h / 2f;
        return Paint(w, h, (x, y) =>
        {
            float px = x + 0.5f, py = y + 0.5f;
            Color c = Shade(track, Coverage(BoxDistance(px, py, w, h, h / 2f)));
            float k = Coverage(CircleDistance(px, py, knobX, h / 2f, h / 2f - 3f));
            return Over(Shade(knob, k), c);
        });
    }

    /// <summary>A white texture drawn in a colour (fills, lines, the selected tab).</summary>
    public static void Fill(Rect r, Color color)
    {
        if (Event.current.type != EventType.Repaint) return;
        Color previous = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, color.a * previous.a);
        GUI.DrawTexture(r, Flat != null ? Flat : Texture2D.whiteTexture);
        GUI.color = previous;
    }

    /// <summary>A white glyph texture tinted with a colour.</summary>
    public static void Tinted(Rect r, Texture2D texture, Color color)
    {
        if (Event.current.type != EventType.Repaint) return;
        Color previous = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, color.a * previous.a);
        GUI.DrawTexture(r, texture);
        GUI.color = previous;
    }
}
