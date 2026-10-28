using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Features;

/// <summary>What a build offers as its guide: the planner's own text, an export's note, a Last Epoch Tools link, or
/// nothing.</summary>
internal enum GuideKind { None, Full, Export, LetLink }

internal enum BlockKind { Heading, Paragraph, ListItem, FoldTitle, BannerTitle, BannerText }

/// <summary>The site's colours folded to a few, each drawn in a theme colour role.</summary>
internal enum GuideColour { Text, Keyword, Yellow, Blue, Green, Purple, Red, Link }

internal enum EmbedKind { Skill, Node, Item, Affix }

/// <summary>A build's guide, read from its stored planner data; runtime only, never stored.</summary>
internal sealed class BuildGuide
{
    public GuideKind Kind;
    public GuideDoc? Strengths, Intro, Summary, Faq, Changelog;
    /// <summary>By stage index; identical stages share one.</summary>
    public StageNotes?[] Stages = Array.Empty<StageNotes?>();
    public string? ShowcaseId;
    public readonly List<(string Label, string Url)> Leveling = new();
    public string? LetSlug;
    /// <summary>Every embed of every document, by key.</summary>
    public readonly Dictionary<string, Embed> Embeds = new();
    /// <summary>Rich-text node types seen and those drawn only as their text, with counts (the coverage verb).</summary>
    public readonly SortedDictionary<string, int> NodeTypes = new(), Fallbacks = new();
    public bool Resolved;
    public float RetryAt;
    /// <summary>The planner's own item list while the guide is read, for embeds that point into it.</summary>
    internal JsonObject? PlannerItems;

    public StageNotes? Stage(int stage) => stage >= 0 && stage < Stages.Length ? Stages[stage] : null;

    /// <summary>Every document, global ones first, then each distinct stage's.</summary>
    public IEnumerable<GuideDoc> Docs()
    {
        foreach (var doc in new[] { Strengths, Intro, Summary, Faq, Changelog })
            if (doc != null) yield return doc;
        foreach (var notes in Stages.Distinct())
            if (notes != null)
                foreach (var doc in new[] { notes.Skills, notes.Passives, notes.Equipment })
                    if (doc != null) yield return doc;
    }
}

internal sealed class StageNotes
{
    public GuideDoc? Skills, Passives, Equipment;
}

/// <summary>One rich-text document as blocks in reading order; a fold's title is followed by its content.</summary>
internal sealed class GuideDoc
{
    public readonly string Section;
    public readonly List<GuideBlock> Blocks = new();
    /// <summary>Fold id → the fold holding it, or -1.</summary>
    public readonly List<int> FoldParents = new();
    /// <summary>The guide holding it, whose embeds its runs name.</summary>
    public BuildGuide? Guide;

    public GuideDoc(string section) => Section = section;
}

internal sealed class GuideBlock
{
    public BlockKind Kind;
    /// <summary>A heading's size (1 large, 2 small) or a list item's depth (from 1).</summary>
    public int Level;
    /// <summary>A numbered list item's value, else 0.</summary>
    public int Number;
    /// <summary>The fold holding it, or -1.</summary>
    public int Fold = -1;
    /// <summary>A fold title's own fold, which it opens and closes.</summary>
    public int Opens = -1;
    /// <summary>Drawn on the banner card (a warning box's title, text and lists).</summary>
    public bool Banner;
    /// <summary>A list item's bullet colour when it isn't the plain one (strengths green, weaknesses red).</summary>
    public GuideColour? Bullet;
    public readonly List<GuideRun> Runs = new();
    public string PlainText = string.Empty;
    public GuideDoc Doc = null!;
}

/// <summary>Text with its look, or an embed (Embed set, Text the site's name for it). "\n" alone is a line break.</summary>
internal sealed class GuideRun
{
    public string Text = string.Empty;
    public bool Bold, Italic;
    public GuideColour Colour;
    public string? LinkUrl;
    public string? Embed;
}

internal sealed class Embed
{
    public string Key = string.Empty;
    public EmbedKind Kind;
    public string SiteText = string.Empty;
    public bool IconOnly;
    public string Id = string.Empty;
    public string? AbilityId;
    public string Tree = string.Empty;
    public byte Node;
    public byte ItemType;
    public ushort SubType, Unique;
    /// <summary>An item the planner lists among the build's own (an embed by its item number), affixes and all.</summary>
    public PlannedItem? Planned;
    /// <summary>The item for its tooltip: a plain base or unique, rolls at their best.</summary>
    public PlannedItem? Item;
    public bool Set;
    public bool Resolved;
    public string Why = "not checked yet";
    /// <summary>Its icon's key, made on first use.</summary>
    internal string? IconKey;
}

internal static partial class Planner
{
    private static readonly Dictionary<string, BuildGuide> s_guides = new();
    private static int s_guideGeneration = -1;
    private const float GuideRetrySeconds = 1f;

    /// <summary>
    /// The build's guide, read from its planner data on first use and kept until the library changes; its embeds are
    /// checked against the game's data once that can be read.
    /// </summary>
    public static BuildGuide GuideOf(Build build)
    {
        if (s_guideGeneration != Library.Generation)
        {
            s_guideGeneration = Library.Generation;
            s_guides.Clear();
        }
        if (!s_guides.TryGetValue(build.Id, out var guide))
        {
            try
            {
                guide = ReadGuide(build);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:guide", $"Planner: the build's guide couldn't be read: {e.Message}", 30);
                guide = new BuildGuide();
            }
            s_guides[build.Id] = guide;
        }
        if (!guide.Resolved && Clock.Now >= guide.RetryAt) ResolveEmbeds(build, guide);
        return guide;
    }

    /// <summary>Drops every guide read (the feature stopping).</summary>
    public static void ForgetGuides()
    {
        s_guides.Clear();
        s_guideGeneration = -1;
    }

    // ---- Reading ----

    private static readonly Regex s_videoId = new("^[A-Za-z0-9_-]{11}$");

    private static BuildGuide ReadGuide(Build build)
    {
        var guide = new BuildGuide();
        if (build.Source == MaxrollFormat.ExportSource)
        {
            guide.Kind = GuideKind.Export;
            return guide;
        }
        if (MaxrollFormat.Parse(build.Raw) is not JsonObject raw) return guide;
        if (build.Source == LetFormat.Source)
        {
            guide.Kind = GuideKind.LetLink;
            guide.LetSlug = Build.Str(raw["guide"]) is { Length: > 0 } slug ? slug : null;
            return guide;
        }
        if (Build.Str(raw["data"]) is not string dataText || MaxrollFormat.Parse(dataText) is not JsonObject data) return guide;

        guide.PlannerItems = data["items"] as JsonObject;
        if (data["strAndWeak"] is JsonObject sw) guide.Strengths = StrengthsDoc(sw);
        if (data["globalNotes"] is JsonObject notes)
        {
            guide.Intro = Doc(guide, notes["intro"], "Intro");
            guide.Summary = Doc(guide, notes["summary"], "Summary");
            guide.Faq = Doc(guide, notes["faqs"], "FAQ");
            guide.Changelog = Doc(guide, notes["changelog"], "Changelog");
        }
        // Stages are the profiles that are objects, in order, as the link's import adds them.
        var profiles = (data["profiles"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        guide.Stages = new StageNotes?[profiles.Count];
        var shared = new Dictionary<string, StageNotes?>();
        for (int i = 0; i < profiles.Count; i++)
        {
            if (profiles[i]["widgetNotes"] is not JsonObject widget) continue;
            string text = widget.ToJsonString();
            if (!shared.TryGetValue(text, out var stage))
            {
                stage = new StageNotes
                {
                    Skills = Doc(guide, widget["skills"], "Skills"),
                    Passives = Doc(guide, widget["passives"], "Passives"),
                    Equipment = Doc(guide, widget["equipment"], "Equipment"),
                };
                if (stage.Skills == null && stage.Passives == null && stage.Equipment == null) stage = null;
                shared[text] = stage;
            }
            guide.Stages[i] = stage;
        }
        if (Build.Str(data["showcaseVideo"]) is string video && s_videoId.IsMatch(video)) guide.ShowcaseId = video;
        if ((data["buildGuide"] as JsonObject)?["levelingRelatedPosts"] is JsonArray posts)
            foreach (var post in posts)
                if (Build.Str(post) is string url && url.StartsWith("https://maxroll.gg/", StringComparison.Ordinal))
                    guide.Leveling.Add((LevelingLabel(url), url));

        bool any = guide.Strengths != null || guide.Intro != null || guide.Summary != null || guide.Faq != null || guide.Changelog != null
            || guide.Stages.Any(s => s != null) || guide.ShowcaseId != null || guide.Leveling.Count > 0;
        guide.Kind = any ? GuideKind.Full : GuideKind.None;
        guide.PlannerItems = null;
        return guide;
    }

    // "https://maxroll.gg/last-epoch/build-guides/rip-blood-lich-leveling-guide" → "Rip Blood Lich leveling guide".
    private static string LevelingLabel(string url)
    {
        string last = url.TrimEnd('/');
        last = last[(last.LastIndexOf('/') + 1)..];
        int query = last.IndexOfAny(new[] { '?', '#' });
        if (query >= 0) last = last[..query];
        const string tail = "-leveling-guide";
        bool leveling = last.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
        if (leveling) last = last[..^tail.Length];
        var words = last.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        string name = string.Join(" ", words);
        return leveling ? (name.Length > 0 ? name + " leveling guide" : "Leveling guide") : name.Length > 0 ? name : "Leveling guide";
    }

    // Two short lists, as headings and list items so they wrap, draw and search like the rest.
    private static GuideDoc? StrengthsDoc(JsonObject sw)
    {
        var doc = new GuideDoc("Strengths and weaknesses");
        foreach (var (key, title, bullet) in new[] { ("strengths", "Strengths", GuideColour.Green), ("weaknesses", "Weaknesses", GuideColour.Red) })
        {
            var phrases = (sw[key] as JsonArray)?.Select(Build.Str).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (phrases is not { Count: > 0 }) continue;
            Add(doc, new GuideBlock { Kind = BlockKind.Heading, Level = 2 }, title);
            foreach (var phrase in phrases) Add(doc, new GuideBlock { Kind = BlockKind.ListItem, Level = 1, Bullet = bullet }, phrase!.Trim());
        }
        return doc.Blocks.Count > 0 ? doc : null;

        static void Add(GuideDoc doc, GuideBlock block, string text)
        {
            block.Doc = doc;
            block.Runs.Add(new GuideRun { Text = text });
            block.PlainText = text;
            doc.Blocks.Add(block);
        }
    }

    // ---- Rich text (Lexical) ----

    // Where the walk is: the fold holding new blocks, the list depth and type, and whether it's inside a banner.
    private readonly record struct Place(int Fold, int Depth, bool Numbered, bool Banner);

    private static GuideDoc? Doc(BuildGuide guide, JsonNode? node, string section)
    {
        if ((node as JsonObject)?["root"] is not JsonObject root) return null;
        var doc = new GuideDoc(section) { Guide = guide };
        Blocks(guide, doc, root, new Place(-1, 0, false, false));
        doc.Blocks.RemoveAll(b => b.Runs.Count == 0 || b.PlainText.Trim().Length == 0 && !b.Runs.Any(r => r.Embed != null));
        return doc.Blocks.Count > 0 ? doc : null;
    }

    private static bool Inline(string type) => type is "text" or "embed" or "link" or "linebreak" or "tab";

    // The children of a block container; inline children outside any block gather in a paragraph of their own.
    private static void Blocks(BuildGuide guide, GuideDoc doc, JsonObject container, Place at)
    {
        GuideBlock? loose = null;
        foreach (var child in Children(container))
        {
            string type = Count(guide, child);
            if (Inline(type))
            {
                loose ??= NewBlock(doc, at.Banner ? BlockKind.BannerText : BlockKind.Paragraph, at);
                Runs(guide, loose, child, null);
                continue;
            }
            if (loose != null) Finish(loose);
            loose = null;
            Block(guide, doc, child, type, at);
        }
        if (loose != null) Finish(loose);
    }

    private static void Block(BuildGuide guide, GuideDoc doc, JsonObject node, string type, Place at)
    {
        switch (type)
        {
            case "heading":
            {
                string tag = Build.Str(node["tag"]) ?? "h3";
                var block = NewBlock(doc, at.Banner ? BlockKind.BannerTitle : BlockKind.Heading, at);
                block.Level = tag is "h1" or "h2" ? 1 : 2;
                InlineChildren(guide, block, node);
                break;
            }
            case "paragraph":
            {
                var block = NewBlock(doc, at.Banner ? BlockKind.BannerText : BlockKind.Paragraph, at);
                InlineChildren(guide, block, node);
                break;
            }
            case "list":
                Blocks(guide, doc, node, at with { Depth = at.Depth + 1, Numbered = Build.Str(node["listType"]) == "number" });
                break;
            case "listitem":
            {
                // A list item holds its own text, then any list nested in it, one level deeper.
                var block = NewBlock(doc, BlockKind.ListItem, at);
                block.Level = Math.Max(1, at.Depth);
                if (at.Numbered) block.Number = Build.Int(node["value"]) ?? 0;
                var nested = new List<JsonObject>();
                foreach (var child in Children(node))
                {
                    string t = Count(guide, child);
                    if (Inline(t)) Runs(guide, block, child, null);
                    else nested.Add(child);
                }
                Finish(block);
                foreach (var child in nested)
                {
                    string t = Build.Str(child["type"]) ?? string.Empty;
                    if (t == "list") Block(guide, doc, child, t, at);
                    else Block(guide, doc, child, t, at with { Depth = at.Depth + 1 });
                }
                break;
            }
            case "collapsible-container":
            {
                int fold = doc.FoldParents.Count;
                doc.FoldParents.Add(at.Fold);
                foreach (var child in Children(node))
                {
                    string t = Count(guide, child);
                    if (t == "collapsible-title")
                    {
                        var title = NewBlock(doc, BlockKind.FoldTitle, at);
                        title.Opens = fold;
                        InlineChildren(guide, title, child);
                    }
                    else
                    {
                        var inner = at with { Fold = fold };
                        if (t == "collapsible-content") Blocks(guide, doc, child, inner);
                        else Block(guide, doc, child, t, inner);
                    }
                }
                break;
            }
            case "disclaimer-container":
            {
                var inner = at with { Banner = true };
                foreach (var child in Children(node))
                {
                    string t = Count(guide, child);
                    if (t == "disclaimer-title")
                    {
                        var title = NewBlock(doc, BlockKind.BannerTitle, inner);
                        InlineChildren(guide, title, child);
                    }
                    else if (t == "disclaimer-content") Blocks(guide, doc, child, inner);
                    else Block(guide, doc, child, t, inner);
                }
                break;
            }
            case "collapsible-content" or "disclaimer-content" or "root":
                Blocks(guide, doc, node, at);
                break;
            default:
            {
                // Anything else keeps its text, as a paragraph of its own.
                Fallback(guide, type);
                if (node["children"] is JsonArray)
                {
                    Blocks(guide, doc, node, at);
                }
                else if (Build.Str(node["text"]) is { Length: > 0 } text)
                {
                    var block = NewBlock(doc, at.Banner ? BlockKind.BannerText : BlockKind.Paragraph, at);
                    block.Runs.Add(new GuideRun { Text = text });
                    Finish(block);
                }
                break;
            }
        }
    }

    private static GuideBlock NewBlock(GuideDoc doc, BlockKind kind, Place at)
    {
        var block = new GuideBlock { Kind = kind, Fold = at.Fold, Banner = at.Banner, Doc = doc };
        doc.Blocks.Add(block);
        return block;
    }

    private static void InlineChildren(BuildGuide guide, GuideBlock block, JsonObject node)
    {
        foreach (var child in Children(node))
        {
            Count(guide, child);
            Runs(guide, block, child, null);
        }
        Finish(block);
    }

    private static void Finish(GuideBlock block) => block.PlainText = string.Concat(block.Runs.Select(r => r.Text)).Trim();

    // One inline node as runs; a link's children take its address when it's one that may be opened.
    private static void Runs(BuildGuide guide, GuideBlock block, JsonObject node, string? link)
    {
        string type = Build.Str(node["type"]) ?? string.Empty;
        switch (type)
        {
            case "text":
            {
                string text = Build.Str(node["text"]) ?? string.Empty;
                if (text.Length == 0) return;
                int format = Build.Int(node["format"]) ?? 0;
                block.Runs.Add(new GuideRun
                {
                    Text = text,
                    Bold = (format & 1) != 0,
                    Italic = (format & 2) != 0,
                    Colour = link != null ? GuideColour.Link : ColourOf(Build.Str(node["style"])),
                    LinkUrl = link,
                });
                return;
            }
            case "linebreak":
                block.Runs.Add(new GuideRun { Text = "\n" });
                return;
            case "tab":
                block.Runs.Add(new GuideRun { Text = " " });
                return;
            case "embed":
                if (ReadEmbed(guide, node) is { } embed)
                {
                    block.Runs.Add(new GuideRun { Text = embed.SiteText, Embed = embed.Key });
                    return;
                }
                Fallback(guide, "embed " + (Build.Str(node["embedType"]) ?? "?"));
                if (Build.Str(node["text"]) is { Length: > 0 } plain) block.Runs.Add(new GuideRun { Text = plain });
                return;
            case "link":
            {
                string? url = Build.Str(node["url"]);
                string? allowed = url != null && AllowedLink(url) ? url : null;
                foreach (var child in Children(node))
                {
                    Count(guide, child);
                    Runs(guide, block, child, allowed);
                }
                return;
            }
            default:
                Fallback(guide, type);
                if (node["children"] is JsonArray)
                    foreach (var child in Children(node))
                    {
                        Count(guide, child);
                        Runs(guide, block, child, link);
                    }
                else if (Build.Str(node["text"]) is { Length: > 0 } other) block.Runs.Add(new GuideRun { Text = other });
                return;
        }
    }

    /// <summary>Only the planner's own site and YouTube open from the guide.</summary>
    internal static bool AllowedLink(string url) =>
        url.StartsWith("https://maxroll.gg/", StringComparison.Ordinal) || url.StartsWith("https://www.youtube.com/", StringComparison.Ordinal)
        || url.StartsWith("https://youtu.be/", StringComparison.Ordinal);

    private static IEnumerable<JsonObject> Children(JsonObject node) => (node["children"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    private static string Count(BuildGuide guide, JsonObject node)
    {
        string type = Build.Str(node["type"]) ?? string.Empty;
        guide.NodeTypes[type] = guide.NodeTypes.GetValueOrDefault(type) + 1;
        return type;
    }

    private static void Fallback(BuildGuide guide, string type) => guide.Fallbacks[type] = guide.Fallbacks.GetValueOrDefault(type) + 1;

    // The site's colours by meaning; others by hue, greys and near-whites as plain text.
    private static readonly Dictionary<string, GuideColour> s_siteColours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["e3e3e3"] = GuideColour.Text, ["ffffff"] = GuideColour.Text, ["dbc09b"] = GuideColour.Text,
        ["986d3e"] = GuideColour.Keyword, ["cf7a04"] = GuideColour.Keyword, ["c13c1d"] = GuideColour.Keyword,
        ["bbac43"] = GuideColour.Yellow,
        ["77d4f0"] = GuideColour.Blue, ["057af0"] = GuideColour.Blue,
        ["4b9257"] = GuideColour.Green, ["25a179"] = GuideColour.Green,
        ["a36fd1"] = GuideColour.Purple, ["c184ff"] = GuideColour.Purple,
        ["ca094c"] = GuideColour.Red, ["ff4d4d"] = GuideColour.Red, ["851522"] = GuideColour.Red,
    };

    private static readonly Regex s_styleColour = new(@"(?:^|;)\s*color\s*:\s*#([0-9a-fA-F]{6})\b");

    private static GuideColour ColourOf(string? style)
    {
        if (string.IsNullOrEmpty(style) || s_styleColour.Match(style) is not { Success: true } m) return GuideColour.Text;
        string hex = m.Groups[1].Value;
        if (s_siteColours.TryGetValue(hex, out var known)) return known;
        int rgb = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        Color.RGBToHSV(new Color((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f), out float h, out float s, out float v);
        if (s < 0.25f || v < 0.25f) return GuideColour.Text;
        float deg = h * 360f;
        return deg switch
        {
            < 15f or >= 320f => GuideColour.Red,
            < 45f => GuideColour.Keyword,
            < 75f => GuideColour.Yellow,
            < 170f => GuideColour.Green,
            < 250f => GuideColour.Blue,
            _ => GuideColour.Purple,
        };
    }

    // ---- Embeds ----

    private static Embed? ReadEmbed(BuildGuide guide, JsonObject node)
    {
        string kind = Build.Str(node["embedType"]) ?? string.Empty;
        string id = Build.Str(node["id"]) ?? (Build.Int(node["id"]) is int n ? n.ToString(s_inv) : string.Empty);
        var options = node["options"] as JsonObject;
        var attributes = options?["attributes"] as JsonObject;
        string? Attr(string name) => Build.Str(attributes?[name]) ?? (Build.Int(attributes?[name]) is int i ? i.ToString(s_inv) : null);
        var embed = new Embed
        {
            SiteText = Build.Str(node["text"]) ?? string.Empty,
            IconOnly = options?["iconOnly"] is JsonValue v && v.TryGetValue(out bool only) && only,
        };
        switch (kind)
        {
            case "le-skill":
                embed.Kind = EmbedKind.Skill;
                embed.Id = id.Length > 0 ? id : Attr("id") ?? string.Empty;
                if (embed.Id.Length == 0) return null;
                embed.Key = "skill:" + embed.Id;
                break;
            case "le-passive":
            {
                embed.Kind = EmbedKind.Node;
                string tree, nodeId;
                int colon = id.IndexOf(':');
                if (colon > 0)
                {
                    tree = id[..colon];
                    nodeId = id[(colon + 1)..];
                }
                else
                {
                    tree = Attr("tree") ?? string.Empty;
                    nodeId = id.Length > 0 ? id : Attr("id") ?? string.Empty;
                }
                if (tree.Length == 0 || !byte.TryParse(nodeId, NumberStyles.None, s_inv, out byte number)) return null;
                embed.Tree = tree;
                embed.Node = number;
                embed.Key = $"node:{tree}:{number}";
                break;
            }
            case "le-item" or "le-custom-item" when !id.Contains(':') && Attr("type") == null:
            {
                // The site's newer form: the number of an item in the build's own item list.
                if (guide.PlannerItems?[id.Length > 0 ? id : Attr("id") ?? string.Empty] is not JsonObject listed) return null;
                embed.Kind = EmbedKind.Item;
                embed.Planned = MaxrollItem(listed, PlannedPlace.Slot, string.Empty);
                embed.ItemType = embed.Planned.ItemType;
                embed.SubType = embed.Planned.SubType;
                embed.Unique = embed.Planned.Unique;
                embed.Key = "planned:" + (id.Length > 0 ? id : Attr("id"));
                break;
            }
            case "le-item":
            {
                embed.Kind = EmbedKind.Item;
                string[] parts = id.Length > 0 ? id.Split(':') : new[] { Attr("type") ?? string.Empty, Attr("sub") ?? string.Empty, Attr("unique") ?? "0" };
                if (parts.Length < 2 || !byte.TryParse(parts[0], NumberStyles.None, s_inv, out byte type)
                    || !ushort.TryParse(parts[1], NumberStyles.None, s_inv, out ushort sub)) return null;
                ushort unique = parts.Length > 2 && ushort.TryParse(parts[2], NumberStyles.None, s_inv, out ushort u) ? u : (ushort)0;
                embed.ItemType = type;
                embed.SubType = sub;
                embed.Unique = unique;
                embed.Key = $"item:{type}:{sub}:{unique}";
                break;
            }
            case "le-affix":
                embed.Kind = EmbedKind.Affix;
                embed.Id = id.Length > 0 ? id : Attr("id") ?? string.Empty;
                if (!ushort.TryParse(embed.Id, NumberStyles.None, s_inv, out _)) return null;
                embed.Key = "affix:" + embed.Id;
                break;
            default:
                return null;
        }
        if (guide.Embeds.TryGetValue(embed.Key, out var known)) return known;
        guide.Embeds[embed.Key] = embed;
        return embed;
    }

    // Each embed checked against the game's data once it can be read; until then tried again once a second.
    private static void ResolveEmbeds(Build build, BuildGuide guide)
    {
        guide.RetryAt = Clock.Now + GuideRetrySeconds;
        if (guide.Embeds.Count == 0)
        {
            guide.Resolved = true;
            return;
        }
        var feature = s_run?.Feature;
        if (feature == null) return;
        bool data = feature.Has(GameDataGroup), items = feature.Has(ItemDataGroup);
        try
        {
            var trees = data ? GlobalTreeData.get() : null;
            if (data && (!Game.Alive(trees) || !AbilityManager.Available)) return;
            if (items && ItemList.get() == null) return;
            var cls = build.Stages.Count > 0 ? CharacterClassList.getClass(build.Stages[Math.Clamp(build.FirstStage, 0, build.Stages.Count - 1)].ClassId) : null;
            foreach (var embed in guide.Embeds.Values) ResolveEmbed(embed, data, items, trees, cls);
            guide.Resolved = true;
            s_guideStamp++;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:guide embeds", $"Planner: the guide's names couldn't be checked: {e.Message}", 30);
        }
    }

    private static void ResolveEmbed(Embed embed, bool data, bool items, GlobalTreeData? trees, CharacterClass? cls)
    {
        embed.Resolved = false;
        switch (embed.Kind)
        {
            case EmbedKind.Skill:
                if (!data) { embed.Why = "game data group missing"; return; }
                embed.AbilityId = FindAbility(embed.Id, cls) ?? (embed.SiteText.Length > 0 ? FindAbility(embed.SiteText, cls) : null);
                embed.Resolved = embed.AbilityId != null;
                embed.Why = embed.Resolved ? string.Empty : "no ability by that name";
                return;
            case EmbedKind.Node:
                if (!data) { embed.Why = "game data group missing"; return; }
                var tree = trees!.getTreeData(embed.Tree);
                embed.Resolved = tree != null && tree.getNode(embed.Node) != null;
                embed.Why = embed.Resolved ? string.Empty : tree == null ? "no tree " + embed.Tree : "no node in tree";
                return;
            case EmbedKind.Item:
            {
                if (!items) { embed.Why = "item data group missing"; return; }
                var item = embed.Planned ?? new PlannedItem { Place = PlannedPlace.Slot, ItemType = embed.ItemType, SubType = embed.SubType, Unique = embed.Unique, RollsFilled = true };
                if (embed.Planned == null) item.Rarity = RarityOf(item.Unique, 0);
                Check(item);
                embed.Set = item.Rarity == 8;
                embed.Item = item.Unmakeable ? null : item;
                embed.Resolved = !item.Unmakeable;
                embed.Why = embed.Resolved ? string.Empty : "unknown " + string.Join(", ", item.Unknown);
                return;
            }
            default:
                if (!items) { embed.Why = "item data group missing"; return; }
                embed.Resolved = ushort.TryParse(embed.Id, NumberStyles.None, s_inv, out ushort id) && AffixList.get()?.GetAffix(id) != null;
                embed.Why = embed.Resolved ? string.Empty : "no affix by that id";
                return;
        }
    }
}
