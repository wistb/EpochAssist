using System.Text.RegularExpressions;
using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class Planner
{
    // A block laid out for one width: its labels and icons, and the rects the pointer can rest on or click.
    private sealed class BlockLayout
    {
        public float Width;
        public int Stamp;
        public float Height;
        /// <summary>A list item's bullet text and, for a coloured mark, its colour.</summary>
        public string Bullet = string.Empty;
        public Color BulletColour;
        public readonly List<LayoutPiece> Pieces = new();
        public readonly List<(Rect Rect, Embed? Embed, string? Url)> Hot = new();
    }

    // A label (Text set) or an icon (Embed set); Style is one of the guide's styles.
    private readonly record struct LayoutPiece(Rect Rect, string? Text, GUIStyle? Style, Color Colour, Embed? Embed);

    // A word being placed: text, its style and colour, and what it belongs to; an icon glued before it when Icon is set.
    private readonly record struct Word(string Text, int Spaces, GUIStyle Style, Color Colour, Embed? Embed, string? Url, bool Icon, bool Break);

    private const float ListIndent = 18f, BulletWidth = 14f, BannerIndent = 12f, FoldIndent = 18f, BlockGap = 4f;

    private static readonly Dictionary<GuideBlock, BlockLayout> s_layouts = new();
    private static readonly Dictionary<(GUIStyle, string), float> s_wordWidths = new();
    private static readonly Dictionary<GUIStyle, (float Space, float Line)> s_styleSizes = new();
    private static readonly Regex s_words = new(@"\S+|\s+");
    private static GUIStyle? s_body, s_bold, s_italic, s_boldItalic, s_heading1, s_heading2;
    private static int s_stylesFor = -1;
    // Goes up when a guide's names are checked, so their colours and icon spaces are laid out again.
    private static int s_guideStamp;

    /// <summary>Drops every laid-out block (the feature stopping, the Guide turned off).</summary>
    private static void ForgetLayouts()
    {
        s_layouts.Clear();
        s_wordWidths.Clear();
        s_styleSizes.Clear();
    }

    private static int LayoutStampNow => Theme.Builds * 7919 + ColourRoles.Version * 31 + s_guideStamp + (s_run?.Feature.Has(GuideIconsGroup) == true ? 0 : 1 << 20);

    // Worked out once a frame (Frame), as every row's height looks it up; without a run, each time.
    private static int s_layoutStamp;
    private static int LayoutStamp => s_run != null ? s_layoutStamp : LayoutStampNow;

    // The guide's text styles, made again when the theme rebuilds its own.
    private static void Styles()
    {
        if (s_stylesFor == Theme.Builds && s_body != null) return;
        s_stylesFor = Theme.Builds;
        s_wordWidths.Clear();
        s_styleSizes.Clear();
        // White text, tinted per label with GUI.contentColor: setting a style's colour per label costs an interop wrapper
        // and several game calls each time.
        GUIStyle Make(FontStyle font, int add)
        {
            var style = new GUIStyle(Theme.Label)
            {
                fontStyle = font, fontSize = Theme.Label.fontSize + add, wordWrap = false, richText = true, clipping = TextClipping.Overflow,
                padding = new RectOffset(), margin = new RectOffset(), alignment = TextAnchor.UpperLeft,
            };
            style.normal.textColor = Color.white;
            return style;
        }
        s_body = Make(FontStyle.Normal, 0);
        s_bold = Make(FontStyle.Bold, 0);
        s_italic = Make(FontStyle.Italic, 0);
        s_boldItalic = Make(FontStyle.BoldAndItalic, 0);
        s_heading1 = Make(FontStyle.Bold, 4);
        s_heading2 = Make(FontStyle.Bold, 2);
    }

    private static (float Space, float Line) Sizes(GUIStyle style)
    {
        if (s_styleSizes.TryGetValue(style, out var sizes)) return sizes;
        float space = style.CalcSize(new GUIContent("a a")).x - style.CalcSize(new GUIContent("aa")).x;
        sizes = (Mathf.Max(2f, space), Mathf.Ceil(style.CalcSize(new GUIContent("Ag")).y));
        s_styleSizes[style] = sizes;
        return sizes;
    }

    private static float WordWidth(GUIStyle style, string word)
    {
        if (s_wordWidths.TryGetValue((style, word), out float w)) return w;
        w = style.CalcSize(new GUIContent(word)).x;
        s_wordWidths[(style, word)] = w;
        return w;
    }

    /// <summary>The block's height at this width, from its cached layout.</summary>
    private static float GuideHeight(GuideBlock block, float width)
    {
        long start = s_costOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        float height = Layout(block, width).Height;
        if (s_costOn)
        {
            long spent = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            s_costTicks += spent;
            s_costHeightTicks += spent;
        }
        return height;
    }

    // The dev bridge's cost check: time spent in the guide's layout, drawing and per-frame work while it's on.
    private static bool s_costOn;
    private static long s_costTicks;
    internal static long s_costHeightTicks;
    internal static int s_costLabels, s_costIcons, s_costBlocks;
    internal static long s_costLabelTicks, s_costIconTicks;

    /// <summary>Starts or stops timing the guide's work (the dev bridge); returns the ticks spent since the last read.</summary>
    internal static long GuideCost(bool on)
    {
        s_costOn = on;
        long spent = s_costTicks;
        s_costTicks = 0;
        return spent;
    }

    private static BlockLayout Layout(GuideBlock block, float width)
    {
        width = Mathf.Floor(width);
        int stamp = LayoutStamp;
        if (s_layouts.TryGetValue(block, out var cached) && cached.Width == width && cached.Stamp == stamp) return cached;
        Styles();
        var layout = new BlockLayout
        {
            Width = width, Stamp = stamp,
            Bullet = block.Number > 0 ? block.Number + "." : block.Level > 1 ? "◦" : "•",
            BulletColour = block.Bullet is { } colour ? GuideColourOf(colour) : Theme.Secondary,
        };
        try
        {
            Lay(block, layout);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:guide layout", $"Planner: a guide paragraph couldn't be laid out: {e.Message}", 30);
        }
        s_layouts[block] = layout;
        return layout;
    }

    private static GUIStyle StyleOf(GuideBlock block, GuideRun run) => block.Kind switch
    {
        BlockKind.Heading => block.Level == 1 ? s_heading1! : s_heading2!,
        BlockKind.FoldTitle or BlockKind.BannerTitle => run.Italic ? s_boldItalic! : s_bold!,
        _ => run.Bold && run.Italic ? s_boldItalic! : run.Bold ? s_bold! : run.Italic ? s_italic! : s_body!,
    };

    private static void Lay(GuideBlock block, BlockLayout layout)
    {
        float left = block.Kind == BlockKind.ListItem ? (block.Level - 1) * ListIndent + BulletWidth : 0f;
        if (block.Banner) left += BannerIndent;
        if (block.Kind == BlockKind.FoldTitle) left += FoldIndent;
        float top = block.Kind == BlockKind.Heading ? 6f : block.Kind == BlockKind.BannerTitle ? 4f : 0f;
        float right = layout.Width - (block.Banner ? 8f : 0f);
        float iconSize = Sizes(s_body!).Line;

        // Without the game's icon members no room is kept for icons.
        bool icons = s_run?.Feature.Has(GuideIconsGroup) == true;
        var words = new List<Word>();
        foreach (var run in block.Runs)
        {
            var style = StyleOf(block, run);
            var embed = run.Embed != null && block.Doc.Guide is { } guide && guide.Embeds.TryGetValue(run.Embed, out var e) ? e : null;
            var colour = embed != null ? EmbedColour(embed, run.Colour) : GuideColourOf(run.Colour);
            bool icon = embed is { Resolved: true, Kind: not EmbedKind.Affix } && icons && !OtherClassPassive(embed);
            if (run.Text == "\n")
            {
                words.Add(new Word(string.Empty, 0, style, colour, null, null, false, true));
                continue;
            }
            if (embed is { IconOnly: true } && icon)
            {
                words.Add(new Word(string.Empty, 0, style, colour, embed, null, true, false));
                continue;
            }
            bool first = true;
            foreach (Match m in s_words.Matches(run.Text))
            {
                if (char.IsWhiteSpace(m.Value[0]))
                {
                    if (words.Count > 0 && !words[^1].Break) words[^1] = words[^1] with { Spaces = words[^1].Spaces + m.Value.Length };
                    continue;
                }
                words.Add(new Word(m.Value, 0, style, colour, embed, run.LinkUrl, first && icon, false));
                first = false;
            }
        }

        float x = left, y = top, lineHeight = 0f;
        var line = new List<(float X, Word Word, float Width)>();
        void EndLine()
        {
            float height = Mathf.Max(lineHeight, Sizes(s_body!).Line);
            PlaceLine(layout, line, y, height, iconSize);
            line.Clear();
            y += height;
            x = left;
            lineHeight = 0f;
        }
        foreach (var word in words)
        {
            if (word.Break)
            {
                EndLine();
                continue;
            }
            var (space, lh) = Sizes(word.Style);
            float glued = word.Icon ? iconSize + 3f : 0f;
            float width = word.Text.Length > 0 ? WordWidth(word.Style, word.Text) : 0f;
            if (x > left && x + glued + width > right) EndLine();
            // A word wider than the line is broken by characters.
            if (glued + width > right - left && word.Text.Length > 1)
            {
                string rest = word.Text;
                bool icon = word.Icon;
                while (rest.Length > 0)
                {
                    int n = rest.Length;
                    while (n > 1 && (icon ? iconSize + 3f : 0f) + WordWidth(word.Style, rest[..n]) > right - x) n--;
                    var part = word with { Text = rest[..n], Icon = icon, Spaces = n == rest.Length ? word.Spaces : 0 };
                    float pw = (icon ? iconSize + 3f : 0f) + WordWidth(word.Style, part.Text);
                    line.Add((x, part, pw));
                    lineHeight = Mathf.Max(lineHeight, lh);
                    x += pw + part.Spaces * space;
                    rest = rest[n..];
                    icon = false;
                    if (rest.Length > 0) EndLine();
                }
                continue;
            }
            line.Add((x, word, glued + width));
            lineHeight = Mathf.Max(lineHeight, lh);
            x += glued + width + word.Spaces * space;
        }
        if (line.Count > 0 || y == top) EndLine();
        layout.Height = Mathf.Ceil(y + (block.Kind is BlockKind.Heading ? 2f : 0f) + (block.Banner ? 4f : 0f) + BlockGap * (block.Kind == BlockKind.ListItem ? 0.5f : 1f));
    }

    // One line's words as pieces: icons, and between them one label per font style with its colours as rich-text tags
    // (each label costs a few microseconds to draw); the names' and links' rects come from their words' places.
    private static void PlaceLine(BlockLayout layout, List<(float X, Word Word, float Width)> line, float y, float height, float iconSize)
    {
        int i = 0;
        while (i < line.Count)
        {
            var (x, word, _) = line[i];
            float wordX = x;
            if (word.Icon)
            {
                var r = new Rect(x, y + Mathf.Floor((height - iconSize) / 2f), iconSize, iconSize);
                layout.Pieces.Add(new LayoutPiece(r, null, null, Color.white, word.Embed));
                wordX += iconSize + 3f;
                if (word.Text.Length == 0)
                {
                    layout.Hot.Add((r, word.Embed, null));
                    i++;
                    continue;
                }
            }
            var text = new System.Text.StringBuilder();
            // Body text is drawn in its plain face, other faces by tags; headings in their own.
            var labelStyle = Family(word.Style) == 0 ? s_body! : word.Style;
            Color? open = null;
            float end = wordX;
            // The name or link being passed over: where its rect starts (its icon included) and what it is.
            (float Start, Embed? Embed, string? Url) hot = (0f, null, null);
            void Flush(float to)
            {
                if (hot.Embed != null || hot.Url != null) layout.Hot.Add((new Rect(hot.Start, y, to - hot.Start, height), hot.Embed, hot.Url));
                hot = (0f, null, null);
            }
            int j = i;
            for (; j < line.Count; j++)
            {
                var w = line[j].Word;
                if (j > i && (w.Icon || Family(w.Style) != Family(word.Style))) break;
                float wx = j == i ? wordX : line[j].X;
                if (j > i) text.Append(' ', line[j - 1].Word.Spaces);
                if (open != w.Colour)
                {
                    if (open != null) text.Append("</color>");
                    text.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(w.Colour)).Append('>');
                    open = w.Colour;
                }
                text.Append(Tagged(w.Style, labelStyle, w.Text));
                if (w.Embed != hot.Embed || w.Url != hot.Url)
                {
                    Flush(end);
                    hot = (j == i ? x : wx, w.Embed, w.Url);
                }
                end = wx + WordWidth(w.Style, w.Text);
            }
            Flush(end);
            if (open != null) text.Append("</color>");
            float labelTop = y + Mathf.Floor((height - Sizes(word.Style).Line) / 2f);
            layout.Pieces.Add(new LayoutPiece(new Rect(wordX, labelTop, end - wordX + 1f, Sizes(word.Style).Line), text.ToString(), labelStyle, Color.white, null));
            i = j;
        }
    }

    // Body text in any of its four faces shares one label: the label's own face, the others by <b> and <i> tags. Headings
    // keep their own.
    private static int Family(GUIStyle style) => style == s_heading1 ? 1 : style == s_heading2 ? 2 : 0;

    private static string Tagged(GUIStyle style, GUIStyle label, string text)
    {
        if (style == label) return text;
        bool bold = style == s_bold || style == s_boldItalic, italic = style == s_italic || style == s_boldItalic;
        return (bold ? "<b>" : string.Empty) + (italic ? "<i>" : string.Empty) + text + (italic ? "</i>" : string.Empty) + (bold ? "</b>" : string.Empty);
    }

    // Another class's passive gets no icon here (the game builds only the character's own passive pages), so no room is
    // kept for one.
    private static bool OtherClassPassive(Embed embed)
    {
        if (embed.Kind != EmbedKind.Node || ClassOfTree(embed.Tree) is not int cls) return false;
        var own = Game.LocalTree?.characterClass;
        return Game.Alive(own) && (int)own!.classID != cls;
    }

    // The site's colours as theme colour roles, which the colour-vision setting already adjusts, lifted to readable
    // contrast on the menu's background.
    private static Color GuideColourOf(GuideColour colour) => Readable(colour switch
    {
        GuideColour.Keyword => ColourRoles.Get("rarity.item.unique"),
        GuideColour.Yellow => ColourRoles.Get("rarity.item.rare"),
        GuideColour.Blue => ColourRoles.Get("rarity.item.magic"),
        GuideColour.Green => ColourRoles.Get("rarity.item.set"),
        GuideColour.Purple => ColourRoles.Get("rarity.item.exalted"),
        GuideColour.Red => ColourRoles.Get("danger.high"),
        GuideColour.Link => Theme.Accent,
        _ => Theme.Text,
    });

    // Items in their rarity's colour, affixes in the exalted one; skills and passives keep the text's.
    private static Color EmbedColour(Embed embed, GuideColour text) => embed.Kind switch
    {
        EmbedKind.Item when embed.Planned != null => Readable(ColourRoles.Item(PlannedRarity(embed.Planned))),
        EmbedKind.Item => Readable(ColourRoles.Item(embed.Unique == 0 ? 0 : embed.Set ? 8 : 7)),
        EmbedKind.Affix => Readable(ColourRoles.Get("rarity.item.exalted")),
        _ => GuideColourOf(text),
    };

    // The game's colours are made for its black tooltips; on the menu's grey the dark ones (unique, danger) are lightened,
    // hue kept, until they reach 4.5:1 contrast with the card colour, the lighter of the page's backgrounds.
    private const float MinContrast = 4.5f;

    private static Color Readable(Color colour)
    {
        var back = Widgets.Themed ? Theme.Values.Card : new Color(0.2f, 0.2f, 0.2f);
        float backLum = Luminance(back);
        var lifted = colour;
        for (int step = 1; step <= 20 && Contrast(Luminance(lifted), backLum) < MinContrast; step++)
            lifted = Color.Lerp(colour, Color.white, step * 0.05f);
        lifted.a = 1f;
        return lifted;
    }

    private static float Luminance(Color c)
    {
        static float Channel(float v) => v <= 0.03928f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * Channel(c.r) + 0.7152f * Channel(c.g) + 0.0722f * Channel(c.b);
    }

    private static float Contrast(float a, float b) => (Mathf.Max(a, b) + 0.05f) / (Mathf.Min(a, b) + 0.05f);

    // The game's rarity number for a planned item: exalted when an affix is past tier 5.
    private static int PlannedRarity(PlannedItem item) =>
        item.Rarity is >= 1 and <= 4 && item.Affixes.Exists(a => a.Tier >= 5) ? 5 : item.Rarity;

    // ---- Drawing ----

    private static readonly GUIContent s_bulletContent = new();

    /// <summary>Draws a block at its rect; the name under the pointer is remembered for the hover and a click on a link
    /// or a fold title acts on it.</summary>
    private static void DrawGuideBlock(Rect rect, GuideBlock block)
    {
        long start = s_costOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            DrawGuideBlockTimed(rect, block);
        }
        finally
        {
            if (s_costOn) s_costTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }
    }

    private static void DrawGuideBlockTimed(Rect rect, GuideBlock block)
    {
        // The event is read once: each read is a game call, made for every block on every GUI pass.
        var e = Event.current;
        var type = e.type;
        if (type != EventType.Repaint && type != EventType.MouseDown) return;
        if (s_costOn && type == EventType.Repaint) s_costBlocks++;
        var layout = Layout(block, rect.width);
        var mouse = e.mousePosition;
        if (type == EventType.MouseDown && e.button == 0 && rect.Contains(mouse))
        {
            if (block.Kind == BlockKind.FoldTitle)
            {
                ToggleFold(block);
                e.Use();
                return;
            }
            foreach (var (hot, _, url) in layout.Hot)
                if (url != null && Shift(hot, rect).Contains(mouse))
                {
                    UnityEngine.Application.OpenURL(url);
                    e.Use();
                    return;
                }
        }
        if (type != EventType.Repaint) return;

        if (block.Banner)
        {
            // Through the gap under the row, so the box's rows join into one box.
            var box = new Rect(rect.x, rect.y, rect.width, rect.height + RowLayout.RowGap);
            Theme.Fill(box, Widgets.Themed ? Theme.Values.Card : new Color(0f, 0f, 0f, 0.3f));
            Theme.Fill(new Rect(box.x, box.y, 2f, box.height), Widgets.Themed ? Theme.Accent : Color.white);
        }
        float lead = block.Banner ? BannerIndent : 0f;
        if (block.Kind == BlockKind.ListItem && block.Runs.Count > 0)
        {
            float bx = rect.x + lead + (block.Level - 1) * ListIndent;
            if (block.Number == 0)
            {
                // A drawn mark costs less than a text bullet; strengths and weaknesses get a bigger one in their colour.
                float line = Sizes(s_body!).Line, size = block.Bullet != null ? 6f : block.Level > 1 ? 3f : 4f;
                Theme.Fill(new Rect(bx + 1f, rect.y + Mathf.Floor((line - size) / 2f), size, size), layout.BulletColour);
            }
            else
            {
                s_bulletContent.text = layout.Bullet;
                GUI.contentColor = Theme.Secondary;
                GUI.Label(new Rect(bx, rect.y, BulletWidth + 4f, Sizes(s_body!).Line), s_bulletContent, s_body);
                GUI.contentColor = Color.white;
            }
        }
        if (block.Kind == BlockKind.FoldTitle)
        {
            s_bulletContent.text = FoldOpen(block.Doc, block.Opens) ? "▾" : "▸";
            GUI.contentColor = Theme.Accent;
            GUI.Label(new Rect(rect.x + lead, rect.y, FoldIndent, Sizes(s_bold!).Line), s_bulletContent, s_bold);
            GUI.contentColor = Color.white;
        }
        // The labels' colours are in their tags; the tint stays the white other drawing leaves it at.
        float now = s_guideNow;
        // Lines out of the page's view aren't drawn (a tall paragraph half in view).
        var view = PageView.InView;
        foreach (var piece in layout.Pieces)
        {
            var r = Shift(piece.Rect, rect);
            if (r.yMax < view.yMin || r.yMin > view.yMax) continue;
            if (piece.Embed != null)
            {
                long t0 = s_costOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (Icon(piece.Embed) is { } icon) DrawFitted(r, icon, now);
                if (s_costOn)
                {
                    s_costIcons++;
                    s_costIconTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                }
                continue;
            }
            long l0 = s_costOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            GUI.Label(r, piece.Text, piece.Style);
            if (s_costOn)
            {
                s_costLabels++;
                s_costLabelTicks += System.Diagnostics.Stopwatch.GetTimestamp() - l0;
            }
        }
        foreach (var (hot, embed, url) in layout.Hot)
        {
            var r = Shift(hot, rect);
            if (!r.Contains(mouse)) continue;
            if (url != null) Theme.Fill(new Rect(r.x, r.yMax - 2f, r.width, 1f), Theme.Accent);
            if (embed != null) GuideHovered(embed, r);
        }
    }

    private static Rect Shift(Rect r, Rect by) => new(r.x + by.x, r.y + by.y, r.width, r.height);

    // An icon fitted into its square at its own aspect (item art isn't square).
    private static void DrawFitted(Rect r, GuideIcon icon, float now)
    {
        // The game can unload a sprite's texture under us; it's asked for again and drawn once back. Checked once a
        // second: the check is a game call.
        if (now >= icon.NextCheck)
        {
            icon.NextCheck = now + 1f;
            if (!Game.Alive(icon.Texture))
            {
                AskAgain(icon);
                return;
            }
        }
        float w = icon.Size.x, h = icon.Size.y;
        if (w <= 0f || h <= 0f) return;
        float scale = Mathf.Min(r.width / w, r.height / h);
        var fit = new Rect(r.center.x - w * scale / 2f, r.center.y - h * scale / 2f, w * scale, h * scale);
        GUI.color = Color.white;
        GUI.DrawTextureWithTexCoords(fit, icon.Texture, icon.Uv);
    }
}
