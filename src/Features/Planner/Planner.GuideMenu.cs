using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using EpochAssist.Ui;
using UnityEngine;
using S = EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

// The build view's Guide: a Checklist | Guide switch under the stage line, then the build's own guide as rows, one
// per paragraph, list item or heading, so only those in view draw.
internal static partial class Planner
{
    private const string GuideBlock = "planner.guide";
    private const string ViewBlock = "planner.guide.view";
    public const string GuideTipsGroup = "guide tips";
    // The clock, read once a frame for the icons' checks.
    private static float s_guideNow;

    // Kept for the session, never saved: Checklist until the player picks Guide.
    private static bool s_showingGuide;
    // Folds the player opened, by document and fold id; a section fold (FAQ, Changelog) is its document's -2.
    private static readonly HashSet<(GuideDoc Doc, int Fold)> s_openFolds = new();
    private const int SectionFold = -2;

    // The Guide rows made for a build, stage and library generation.
    private static (string? Build, int Stage, int Generation, List<MenuRow>? Head) s_guideRowsFor;
    private static List<MenuRow> s_guideRows = new();
    // The build and stage the Guide shows, for the hover's planned piece.
    private static (Build? Build, int Stage) s_guideView;

    /// <summary>Whether the build view offers the Guide: the switch is on and the build has a guide of some kind.</summary>
    private static bool GuideOffered(Build build) => S.GuideEnabled.Value && GuideOf(build).Kind != GuideKind.None;

    private static bool ShowingGuide(Build build) => s_showingGuide && GuideOffered(build);

    // The bound stage when this character uses the build, else the build's first.
    private static int GuideStage(Build build)
    {
        var binding = Library.Binding(Profiles.Key);
        int stage = binding is { } b && b.Build == build.Id ? b.Stage : build.FirstStage;
        return Math.Clamp(stage, 0, Math.Max(0, build.Stages.Count - 1));
    }

    // ---- The switch ----

    private static MenuRow ViewSwitch(Build build)
    {
        if (!MenuBlocks.Has(ViewBlock)) MenuBlocks.Register(ViewBlock, DrawViewSwitch, _ => new[] { $"view: {(s_showingGuide ? "Guide" : "Checklist")}" }, (_, _) => RowLayout.ControlHeight + RowLayout.RowGap);
        return new BlockRow(ViewBlock, build) { Key = "view", VisibleWhen = () => GuideOffered(build) };
    }

    // Two buttons, the chosen one shown as the primary.
    private static void DrawViewSwitch(Rect rect, object? data)
    {
        float x = rect.x;
        foreach (var (label, guide) in new[] { ("Checklist", false), ("Guide", true) })
        {
            float w = Controls.ButtonWidth(label);
            var r = new Rect(x, rect.y, w, RowLayout.ControlHeight);
            if (Controls.Button(r, label, "planner.view." + label.ToLowerInvariant(), primary: s_showingGuide == guide,
                    tip: guide ? "Shows the build's own guide" : "Shows the checklist and next steps"))
                SetGuideView(guide);
            x += w + 6f;
        }
    }

    /// <summary>Shows the build view's Guide or its checklist (the switch, the dev bridge).</summary>
    internal static void SetGuideView(bool guide) => s_showingGuide = guide;

    /// <summary>Opens a build's view in the library's place, as its Open button does (the dev bridge).</summary>
    internal static void OpenBuildView(string id) => s_viewing = id;

    internal static bool GuideViewShown => s_showingGuide;

    // ---- The rows ----

    /// <summary>The head rows (stage line and switch) followed by the build's guide for the active stage.</summary>
    private static IReadOnlyList<MenuRow> GuideRows(Build build, List<MenuRow> head)
    {
        int stage = GuideStage(build);
        s_guideView = (build, stage);
        if (s_guideRowsFor == (build.Id, stage, Library.Generation, head)) return s_guideRows;
        s_guideRowsFor = (build.Id, stage, Library.Generation, head);
        if (!MenuBlocks.Has(GuideBlock))
            MenuBlocks.Register(GuideBlock, (r, d) => DrawGuideBlock(r, (GuideBlock)d!), d => new[] { BridgeLine((GuideBlock)d!) }, (d, w) => GuideHeight((GuideBlock)d!, w));
        var guide = GuideOf(build);
        AskIcons(guide);
        var rows = new List<MenuRow>(head);
        AddGuidePage(rows, build, guide, stage);
        s_guideRows = rows;
        return rows;
    }

    private static void AddGuidePage(List<MenuRow> rows, Build build, BuildGuide guide, int stage)
    {
        switch (guide.Kind)
        {
            case GuideKind.Export:
                rows.Add(new HintRow(() => "Guide text comes only with a planner link. Paste the build's Maxroll link to bring its guide.") { Key = "guide.export" });
                return;
            case GuideKind.LetLink when guide.LetSlug is { } slug:
                rows.Add(TabButton("Read guide on Last Epoch Tools", "guide.let",
                    () => UnityEngine.Application.OpenURL(LetGuidePage + Uri.EscapeDataString(slug)), tip: "Opens the guide in the browser"));
                return;
            case GuideKind.LetLink:
                rows.Add(new HintRow(() => "This build has no guide.") { Key = "guide.none" });
                return;
        }

        if (guide.ShowcaseId is { } video)
            rows.Add(TabButton("Watch showcase", "guide.showcase", () => UnityEngine.Application.OpenURL("https://www.youtube.com/watch?v=" + video), tip: "Opens the video in the browser"));
        for (int i = 0; i < guide.Leveling.Count; i++)
        {
            var (label, url) = guide.Leveling[i];
            rows.Add(TabButton(label, $"guide.leveling.{i}", () => UnityEngine.Application.OpenURL(url), tip: "Opens the guide in the browser"));
        }

        AddDoc(rows, guide.Strengths, "Strengths and weaknesses", folded: false);
        AddDoc(rows, guide.Intro, "Intro", folded: false);
        AddDoc(rows, guide.Summary, "Summary", folded: false);
        var notes = guide.Stage(stage);
        string stageName = build.Stages.Count > stage ? build.Stages[stage].Name.Trim() : string.Empty;
        string suffix = stageName.Length > 0 ? " · " + stageName : string.Empty;
        AddDoc(rows, notes?.Skills, "Skills" + suffix, folded: false);
        AddDoc(rows, notes?.Passives, "Passives" + suffix, folded: false);
        AddDoc(rows, notes?.Equipment, "Equipment" + suffix, folded: false);
        AddDoc(rows, guide.Faq, "FAQ", folded: true);
        AddDoc(rows, guide.Changelog, "Changelog", folded: true);
    }

    /// <summary>The Last Epoch Tools guide page, followed by the build's guide name.</summary>
    private const string LetGuidePage = "https://www.lastepochtools.com/build-guides/";

    // A section: its heading (a fold title for FAQ and Changelog, closed at first), then one row per block.
    private static void AddDoc(List<MenuRow> rows, GuideDoc? doc, string title, bool folded)
    {
        if (doc == null) return;
        string key = "guide." + MenuEntry.Slug(doc.Section);
        if (folded)
        {
            var section = new GuideBlock { Kind = BlockKind.FoldTitle, Opens = SectionFold, Doc = doc, PlainText = title };
            section.Runs.Add(new GuideRun { Text = title });
            rows.Add(new BlockRow(GuideBlock, section) { Key = key });
        }
        else
        {
            rows.Add(new HeadingRow(title) { Key = key });
        }
        for (int i = 0; i < doc.Blocks.Count; i++)
        {
            var block = doc.Blocks[i];
            rows.Add(new BlockRow(GuideBlock, block) { Key = $"{key}.{i}", VisibleWhen = () => GuideBlockShown(block) });
        }
    }

    // ---- Folds ----

    private static bool FoldOpen(GuideDoc doc, int fold) => s_openFolds.Contains((doc, fold));

    // Inside every fold that holds it, and inside its section's fold when the section is folded.
    private static bool GuideBlockShown(GuideBlock block)
    {
        var doc = block.Doc;
        if ((doc.Section is "FAQ" or "Changelog") && !FoldOpen(doc, SectionFold)) return false;
        for (int fold = block.Fold; fold >= 0; fold = doc.FoldParents[fold])
            if (!FoldOpen(doc, fold)) return false;
        return true;
    }

    /// <summary>Opens or closes a section's fold (FAQ, Changelog) or a fold by its title's start, in the guide shown, for
    /// the dev bridge; returns what it changed.</summary>
    internal static string SetGuideFold(string name, bool open)
    {
        if (s_guideView.Build is not { } build) return "no guide shown";
        var guide = GuideOf(build);
        foreach (var doc in guide.Docs())
        {
            if (doc.Section.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (open) s_openFolds.Add((doc, SectionFold)); else s_openFolds.Remove((doc, SectionFold));
                return $"section {doc.Section} {(open ? "open" : "closed")}";
            }
            foreach (var block in doc.Blocks)
                if (block.Kind == BlockKind.FoldTitle && block.PlainText.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    if (open) s_openFolds.Add((doc, block.Opens)); else s_openFolds.Remove((doc, block.Opens));
                    return $"fold \"{block.PlainText}\" in {doc.Section} {(open ? "open" : "closed")}";
                }
        }
        return $"no fold {name}";
    }

    /// <summary>Scrolls the Guide to a section's heading or one of its blocks (search, the dev bridge); opens the folds
    /// holding it. Returns what it did.</summary>
    internal static string ScrollGuideTo(string section, int block = -1)
    {
        if (s_guideView.Build is not { } build) return "no guide shown";
        foreach (var doc in GuideOf(build).Docs())
        {
            if (!doc.Section.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            string key = "guide." + MenuEntry.Slug(doc.Section);
            if (block >= 0 && block < doc.Blocks.Count)
            {
                var b = doc.Blocks[block];
                if (doc.Section is "FAQ" or "Changelog") s_openFolds.Add((doc, SectionFold));
                for (int fold = b.Fold; fold >= 0; fold = doc.FoldParents[fold]) s_openFolds.Add((doc, fold));
                key += "." + block;
            }
            PageView.ScrollToRow(key);
            return "scrolled to " + key;
        }
        return $"no section {section}";
    }

    private static void ToggleFold(GuideBlock title)
    {
        var key = (title.Doc, title.Opens);
        if (!s_openFolds.Remove(key)) s_openFolds.Add(key);
    }

    private static string BridgeLine(GuideBlock block) => block.Kind switch
    {
        BlockKind.FoldTitle => $"{(FoldOpen(block.Doc, block.Opens) ? "▾" : "▸")} {block.PlainText}",
        BlockKind.ListItem => $"{new string(' ', 2 * (block.Level - 1))}{(block.Number > 0 ? block.Number + "." : "•")} {block.PlainText}",
        BlockKind.Heading or BlockKind.BannerTitle => $"## {block.PlainText}",
        _ => block.PlainText,
    };

    // ---- Hover ----

    // The name under the pointer: an item opens the game's tooltip beside the menu, as a gear line does.
    private static void GuideHovered(Embed embed, Rect rect)
    {
        if (embed.Kind is EmbedKind.Skill or EmbedKind.Node)
        {
            s_tipHover = embed;
            s_tipHoverSeen = Clock.Now;
            // The game shows its skill tooltip only beside the element under the pointer: the name's own rect.
            var top = GUIUtility.GUIToScreenPoint(rect.position);
            var bottom = GUIUtility.GUIToScreenPoint(new Vector2(rect.xMax, rect.yMax));
            s_tipRect = Rect.MinMaxRect(top.x, top.y, bottom.x, bottom.y);
            return;
        }
        if (embed.Kind != EmbedKind.Item || s_guideView.Build is not { } build) return;
        if (GuideItem(embed, build, s_guideView.Stage) is not { } item) return;
        s_menuHover = item;
        s_menuHoverSeen = Clock.Now;
    }

    /// <summary>The item a guide name stands for: the stage's planned piece when one matches (same unique, else same base
    /// and subtype), else the plain base or unique, rolls at their best.</summary>
    internal static PlannedItem? GuideItem(Embed embed, Build build, int stage)
    {
        if (embed.Planned != null) return embed.Planned.Unmakeable ? null : embed.Planned;
        if (!embed.Resolved) return null;
        var gear = GearOf(build, stage);
        var planned = gear.Slots.Values.Concat(gear.Idols).Concat(gear.Blessings.Values).Concat(gear.WeaverItems.Values).Where(p => !p.Unmakeable);
        var match = embed.Unique != 0
            ? planned.FirstOrDefault(p => p.Unique == embed.Unique)
            : planned.FirstOrDefault(p => p.Unique == 0 && p.ItemType == embed.ItemType && p.SubType == embed.SubType);
        return match ?? embed.Item;
    }

    // The skill or passive the pointer rests on, when the menu last drew it so, and the one whose game tooltip is open;
    // the dev bridge can hold one open with no pointer.
    private static Embed? s_tipHover, s_tipShown, s_tipHeld;
    private static float s_tipHoverSeen = -1f;
    // The hovered name's rect on screen, from the top left.
    private static Rect s_tipRect;
    private static GameObject? s_tipAnchor;

    /// <summary>
    /// Every frame: with the pointer on a skill or passive name in the Guide, the game's own skill or node tooltip beside
    /// the menu, on the side with room, level with the pointer; closed when the pointer leaves or the menu closes.
    /// </summary>
    private static void GuideTipFrame(Run run)
    {
        var hovered = s_tipHeld ?? (Clock.Now - s_tipHoverSeen <= MenuHoverGrace && Menu.Visible ? s_tipHover : null);
        if (hovered != null && hovered == s_tipShown)
        {
            PlaceGuideTip(hovered);
            return;
        }
        if (hovered == s_tipShown) return;
        CloseGuideTip();
        if (hovered == null || !run.Feature.Has(GuideTipsGroup) || TipAnchor() is not { } anchor) return;
        var rect = anchor.GetComponent<RectTransform>();
        bool right = MenuSide();
        var at = s_tipHeld != null ? new Rect(Screen.width / 2f, Screen.height / 2f, 40f, 20f) : s_tipRect;
        float scale = anchor.GetComponentInParent<Canvas>()?.scaleFactor ?? 1f;
        rect.sizeDelta = new Vector2(at.width, at.height) / Mathf.Max(0.01f, scale);
        rect.position = new Vector3(at.x, Screen.height - at.y, 0f);
        anchor.SetActive(true);
        try
        {
            if (hovered.Kind == EmbedKind.Skill)
            {
                var ability = hovered.AbilityId != null ? Il2Cpp.AbilityManager.getPlayerAbility(hovered.AbilityId) : null;
                if (!Game.Alive(ability) || Panels.Find<Il2Cpp.UITooltipAbility>() is not { } tip) return;
                tip.OpenTooltip(ability!, rect, new Vector2(right ? 0f : 1f, 0.5f), false, false, false, false);
            }
            else
            {
                if (TreeNodeOf(hovered) is not { } node || Il2Cpp.NodeTooltipManager.instance is not { } nodes || !Game.Alive(nodes)) return;
                nodes.openForNode(node, rect);
            }
            s_tipShown = hovered;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:guide tip", $"Planner: the game's tooltip couldn't be opened: {e.Message}", 30);
        }
    }

    // The skill tooltip's anchor: an invisible element on the game's tooltip canvas, laid over the hovered name.
    private static GameObject? TipAnchor()
    {
        if (Game.Alive(s_tipAnchor)) return s_tipAnchor;
        var canvas = Il2Cpp.UITooltipItem.instance?.GetComponentInParent<Canvas>()?.rootCanvas;
        if (!Game.Alive(canvas)) return null;
        var go = new GameObject(Marks.MarkName + ".GuideTipAnchor");
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(canvas!.transform, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        // Invisible, so the reload sweep finds it by name.
        var image = go.AddComponent<UnityEngine.UI.Image>();
        image.raycastTarget = false;
        image.color = Color.clear;
        s_tipAnchor = go;
        return go;
    }

    // The game parks its tooltip off screen when no element of its own is under the pointer; it's placed beside the
    // menu instead, level with the name, on the side with room.
    private static void PlaceGuideTip(Embed shown)
    {
        RectTransform? tip = shown.Kind == EmbedKind.Skill ? Panels.Find<Il2Cpp.UITooltipAbility>()?.rectTransform
            : Il2Cpp.NodeTooltipManager.instance?.rectTransform;
        if (!Game.Alive(tip)) return;
        tip!.GetWorldCorners(s_tipCorners);
        float width = s_tipCorners[2].x - s_tipCorners[0].x, height = s_tipCorners[2].y - s_tipCorners[0].y;
        var menu = Menu.ScreenRect;
        bool right = MenuSide();
        float left = right ? menu.xMax + MenuTooltipGap : menu.x - MenuTooltipGap - width;
        float topY = Mathf.Clamp(Screen.height - s_tipRect.y + height / 2f, height, Screen.height);
        var pivot = tip.pivot;
        tip.position = new Vector3(left + pivot.x * width, topY - (1f - pivot.y) * height, tip.position.z);
    }

    private static void CloseGuideTip()
    {
        if (Game.Alive(s_tipAnchor) && s_tipShown != null) s_tipAnchor!.SetActive(false);
        if (s_tipShown == null) return;
        var shown = s_tipShown;
        s_tipShown = null;
        try
        {
            if (shown.Kind == EmbedKind.Skill) Panels.Find<Il2Cpp.UITooltipAbility>()?.CloseTooltip();
            else if (Il2Cpp.NodeTooltipManager.instance is { } nodes && Game.Alive(nodes)) nodes.close();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:guide tip", $"Planner: the game's tooltip couldn't be closed: {e.Message}", 30);
        }
    }

    /// <summary>Holds a guide name's hover open as if the pointer rested on it, or lets go (null); for the dev bridge.</summary>
    internal static string HoldGuideHover(string? key)
    {
        s_tipHeld = null;
        if (key == null) return "closed";
        if (s_guideView.Build is not { } build || !GuideOf(build).Embeds.TryGetValue(key, out var embed)) return $"no {key} in the guide shown";
        if (embed.Kind is EmbedKind.Skill or EmbedKind.Node)
        {
            s_tipHeld = embed;
            return $"{key} held";
        }
        return $"{key} isn't a skill or passive";
    }

    internal static string GuideTipText => $"tip shown {s_tipShown?.Key ?? "none"}, held {s_tipHeld?.Key ?? "none"}";

    /// <summary>The row keys of the guide shown, top to bottom (the dev bridge's cost check scrolls through them).</summary>
    internal static IReadOnlyList<string> GuideRowKeys() => s_guideRows.Where(r => r.Key.StartsWith("guide.", StringComparison.Ordinal)).Select(r => r.Key).ToList();

    // ---- Search ----

    private const string SearchSource = "planner.guide";

    /// <summary>Offers the menu search the guide text of the build this character uses: one entry per block, its path
    /// "Planner › build › Guide › section"; opening one shows the Guide there.</summary>
    private static void AddGuideSearch() => MenuSearch.AddSource(SearchSource, GuideSearchGeneration, GuideSearchEntries);

    private static void RemoveGuideSearch() => MenuSearch.RemoveSource(SearchSource);

    // Read again when the library, the character's build or stage, the switch or the names' check changes.
    private static int GuideSearchGeneration()
    {
        var binding = Library.Binding(Profiles.Key);
        return HashCode.Combine(Library.Generation, binding?.Build, binding?.Stage, S.GuideEnabled.Value, s_guideStamp);
    }

    private static IReadOnlyList<SearchEntry> GuideSearchEntries()
    {
        if (!S.GuideEnabled.Value || Library.Binding(Profiles.Key) is not { } binding || Library.Find(binding.Build) is not { } build)
            return Array.Empty<SearchEntry>();
        var guide = GuideOf(build);
        var entries = new List<SearchEntry>();
        void Add(GuideDoc? doc, string title)
        {
            if (doc == null) return;
            string path = $"Planner › {build.Name} › Guide › {title}";
            for (int i = 0; i < doc.Blocks.Count; i++)
            {
                int block = i;
                if (doc.Blocks[i].PlainText.Length > 0) entries.Add(new SearchEntry(path, doc.Blocks[i].PlainText, () => OpenGuideAt(build.Id, doc.Section, block)));
            }
        }
        Add(guide.Strengths, "Strengths and weaknesses");
        Add(guide.Intro, "Intro");
        Add(guide.Summary, "Summary");
        int stage = Math.Clamp(binding.Stage, 0, Math.Max(0, build.Stages.Count - 1));
        var notes = guide.Stage(stage);
        Add(notes?.Skills, "Skills");
        Add(notes?.Passives, "Passives");
        Add(notes?.Equipment, "Equipment");
        Add(guide.Faq, "FAQ");
        Add(guide.Changelog, "Changelog");
        return entries;
    }

    // The build view on Guide in the Planner tab, scrolled to the block with its folds open.
    private static void OpenGuideAt(string build, string section, int block)
    {
        if (MenuRoutes.ForFeature("planner") is { } page) MenuRoutes.Show(page);
        s_viewing = build;
        s_showingGuide = true;
        if (Library.Find(build) is { } b) s_guideView = (b, GuideStage(b));
        ScrollGuideTo(section, block);
    }

    // ---- Off ----

    // Every frame: with the Guide switched off, its hover, icons and layouts go at once.
    private static void GuideFrame()
    {
        s_layoutStamp = LayoutStampNow;
        s_guideNow = Clock.Now;
        if (s_run is { } run) GuideTipFrame(run);
        if (S.GuideEnabled.Value || s_icons.Count == 0 && s_layouts.Count == 0) return;
        s_tipHeld = null;
        CloseGuideTip();
        FreeIcons();
        ForgetLayouts();
        s_guideRowsFor = default;
    }

    /// <summary>Drops the Guide's state (the feature stopping).</summary>
    private static void ForgetGuidePage()
    {
        s_tipHeld = null;
        CloseGuideTip();
        if (Game.Alive(s_tipAnchor)) UnityEngine.Object.Destroy(s_tipAnchor);
        s_tipAnchor = null;
        FreeIcons();
        ForgetLayouts();
        ForgetGuides();
        s_guideRowsFor = default;
        s_guideRows = new List<MenuRow>();
        s_guideView = default;
    }
}
