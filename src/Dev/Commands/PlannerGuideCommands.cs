using System.Reflection;
using EpochAssist.Core;
using EpochAssist.Features;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Dev;

// planner guide: a build's guide as read from its planner data, how each rich-text node and embed came out, and the
// game objects behind an embed for the icon and tooltip facts. Read only.
internal static class PlannerGuideCommands
{
    public const string Usage = "planner guide [build] | planner guide cost [frames] [closed|still] | planner guide scroll <section> [block]|row:<row key> | planner guide hover <embed key>|close | planner guide view checklist|guide [build] | planner guide fold <section|fold title> open|closed | planner guide icons [build|all] | planner guide coverage [build|all] | planner guide probe skill|node|item <id> | planner guide probe abilities <text> | planner guide probe tip <skill name> [x y w h]|close";

    public static CommandResult Run(CommandCall call) => call.Lower(2) switch
    {
        "coverage" => Coverage(call),
        "probe" => Probe(call),
        "icons" => Icons(call),
        "view" => View(call),
        "cost" => Cost(call),
        "scroll" when call.Args.Length >= 3 && call.Args[2].StartsWith("row:") => Row(call.Args[2][4..]),
        "scroll" => CommandResult.Ok("planner guide scroll: " + Planner.ScrollGuideTo(call.Args.Length >= 3 ? call.Args[2] : string.Empty,
            call.Args.Length >= 4 && int.TryParse(call.Args[3], out int block) ? block : -1)),
        "hover" => CommandResult.Ok("planner guide hover: " + Planner.HoldGuideHover(call.Lower(3) == "close" ? null : call.Rest(3)) + "; " + Planner.GuideTipText),
        "fold" => Fold(call),
        _ => Summary(call),
    };

    // The build named, else the character's build, else the first in the library.
    private static Build? BuildFor(string id)
    {
        Planner.Library.EnsureLoaded();
        if (id.Length > 0) return Planner.Library.Find(id);
        return Planner.ActiveBuild ?? Planner.Library.Builds.FirstOrDefault();
    }

    private static IEnumerable<(string Name, GuideDoc? Doc)> Sections(BuildGuide guide)
    {
        yield return ("Strengths and weaknesses", guide.Strengths);
        yield return ("Intro", guide.Intro);
        yield return ("Summary", guide.Summary);
        for (int i = 0; i < guide.Stages.Length; i++)
        {
            var notes = guide.Stages[i];
            string shared = i > 0 && Array.IndexOf(guide.Stages, notes) < i ? $" (same as stage {Array.IndexOf(guide.Stages, notes) + 1})" : string.Empty;
            yield return ($"Stage {i + 1} Skills{shared}", notes?.Skills);
            yield return ($"Stage {i + 1} Passives{shared}", notes?.Passives);
            yield return ($"Stage {i + 1} Equipment{shared}", notes?.Equipment);
        }
        yield return ("FAQ", guide.Faq);
        yield return ("Changelog", guide.Changelog);
    }

    // planner guide [build]
    private static CommandResult Summary(CommandCall call)
    {
        string id = call.Args.Length >= 2 ? call.Args[1] : string.Empty;
        if (BuildFor(id) is not { } build) return CommandResult.Ok($"planner guide: no build {id}");
        var guide = Planner.GuideOf(build);
        var lines = new List<string> { $"ok planner guide {build.Id} \"{build.Name}\": {guide.Kind}, {guide.Embeds.Count} embeds, resolved {guide.Resolved}" };
        foreach (var (name, doc) in Sections(guide))
        {
            if (doc == null) continue;
            int chars = doc.Blocks.Sum(b => b.PlainText.Length);
            lines.Add($"  {name}: {doc.Blocks.Count} blocks, {chars} characters, {doc.FoldParents.Count} folds");
        }
        if (guide.ShowcaseId != null) lines.Add($"  showcase https://www.youtube.com/watch?v={guide.ShowcaseId}");
        foreach (var (label, url) in guide.Leveling) lines.Add($"  leveling \"{label}\" {url}");
        if (guide.Kind == GuideKind.LetLink) lines.Add($"  Last Epoch Tools guide: {guide.LetSlug ?? "none"}");
        return new CommandResult(DevOutput.Reply("planner-guide", lines));
    }

    // planner guide coverage [build|all]
    private static CommandResult Coverage(CommandCall call)
    {
        string id = call.Args.Length >= 3 ? call.Args[2] : string.Empty;
        Planner.Library.EnsureLoaded();
        var builds = id.Equals("all", StringComparison.OrdinalIgnoreCase) ? Planner.Library.Builds.ToList()
            : BuildFor(id) is { } one ? new List<Build> { one } : new List<Build>();
        if (builds.Count == 0) return CommandResult.Ok($"planner guide coverage: no build {id}");
        var lines = new List<string> { $"ok planner guide coverage: {builds.Count} builds" };
        var types = new SortedDictionary<string, int>();
        var fallbacks = new SortedDictionary<string, int>();
        var totals = new SortedDictionary<string, (int All, int Resolved)>();
        foreach (var build in builds)
        {
            var guide = Planner.GuideOf(build);
            lines.Add($"build {build.Id} \"{build.Name}\": {guide.Kind}, {guide.Embeds.Count} embeds, resolved {guide.Resolved}");
            foreach (var (t, n) in guide.NodeTypes) types[t] = types.GetValueOrDefault(t) + n;
            foreach (var (t, n) in guide.Fallbacks) fallbacks[t] = fallbacks.GetValueOrDefault(t) + n;
            foreach (var embed in guide.Embeds.Values.OrderBy(e => e.Kind).ThenBy(e => e.Key))
            {
                string kind = embed.Kind.ToString().ToLowerInvariant();
                var (all, resolved) = totals.GetValueOrDefault(kind);
                totals[kind] = (all + 1, resolved + (embed.Resolved ? 1 : 0));
                lines.Add($"  {embed.Key} \"{embed.SiteText}\" → {(embed.Resolved ? Target(embed) : "UNRESOLVED " + embed.Why)}{(embed.IconOnly ? " icon only" : string.Empty)}");
            }
        }
        lines.Insert(1, "node types: " + string.Join(", ", types.Select(p => $"{p.Key} {p.Value}")));
        lines.Insert(2, "fallbacks: " + (fallbacks.Count == 0 ? "none" : string.Join(", ", fallbacks.Select(p => $"{p.Key} {p.Value}"))));
        lines.Insert(3, "embeds: " + string.Join(", ", totals.Select(p => $"{p.Key} {p.Value.Resolved}/{p.Value.All}")));
        return new CommandResult(DevOutput.Reply("planner-guide-coverage", lines));
    }

    // planner guide icons [build|all]: asks the build's icons (a later call shows what has loaded since), then each
    // name's icon state, and the share of skill, passive and item names with one.
    private static CommandResult Icons(CommandCall call)
    {
        string id = call.Args.Length >= 3 ? call.Args[2] : string.Empty;
        Planner.Library.EnsureLoaded();
        var builds = id.Equals("all", StringComparison.OrdinalIgnoreCase) ? Planner.Library.Builds.ToList()
            : BuildFor(id) is { } one ? new List<Build> { one } : new List<Build>();
        var lines = new List<string>();
        int named = 0, ready = 0, asked = 0;
        foreach (var build in builds)
        {
            var guide = Planner.GuideOf(build);
            Planner.AskIcons(guide);
            if (guide.Embeds.Count > 0) lines.Add($"build {build.Id} \"{build.Name}\"");
            foreach (var embed in guide.Embeds.Values.Where(e => e.Kind != EmbedKind.Affix).OrderBy(e => e.Kind).ThenBy(e => e.Key))
            {
                named++;
                var icon = Planner.GuideIcons.FirstOrDefault(p => p.Key == IconKeyOf(embed)).Value;
                string state = icon == null ? $"none ({(embed.Resolved ? "not asked" : embed.Why)})"
                    : icon.State == Planner.IconState.Ready ? $"ready {icon.Source} {(icon.Texture != null ? icon.Texture.name : "?")} uv {icon.Uv.width * (icon.Texture?.width ?? 0):0}x{icon.Uv.height * (icon.Texture?.height ?? 0):0}"
                    : $"{icon.State.ToString().ToLowerInvariant()} {icon.Source} {icon.Why}";
                if (icon?.State == Planner.IconState.Ready) ready++;
                if (icon?.State == Planner.IconState.Asked) asked++;
                lines.Add($"  {embed.Key} \"{embed.SiteText}\": {state}");
            }
        }
        lines.Insert(0, $"ok planner guide icons: {ready} of {named} names with an icon ({(named > 0 ? 100f * ready / named : 0f):0.0}%), {asked} still loading");
        return new CommandResult(DevOutput.Reply("planner-guide-icons", lines));
    }

    private static CommandResult Row(string key)
    {
        Ui.PageView.ScrollToRow(key);
        return CommandResult.Ok($"planner guide scroll: to row {key}");
    }

    // planner guide cost [frames] [closed]: the guide's own time per frame (layout, drawing, icons, hover) over the frames,
    // scrolling the page shown top to bottom, or with the menu closed; mean and worst in ms.
    private static CommandResult Cost(CommandCall call)
    {
        int frames = call.Args.Length >= 3 && int.TryParse(call.Args[2], out int n) ? Math.Clamp(n, 10, 3000) : 300;
        bool closed = call.Lower(call.Args.Length) == "closed", still = call.Lower(call.Args.Length) == "still";
        var keys = closed ? new List<string>() : Planner.GuideRowKeys().ToList();
        if (!closed && keys.Count == 0) return CommandResult.Ok("planner guide cost: no guide shown");
        if (closed) Ui.Menu.Close();
        var samples = new List<double>();
        int frame = 0, lastFrame = -1;
        Planner.GuideCost(true);
        Planner.s_costHeightTicks = 0;
        Planner.s_costLabels = Planner.s_costIcons = Planner.s_costBlocks = 0;
        Planner.s_costLabelTicks = Planner.s_costIconTicks = 0;
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                if (Time.frameCount == lastFrame) return null;
                lastFrame = Time.frameCount;
                long ticks = Planner.GuideCost(true);
                if (frame > 0) samples.Add(ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (!closed && !still) Ui.PageView.ScrollToRow(keys[frame * keys.Count / frames % keys.Count]);
                if (++frame <= frames) return null;
                Planner.GuideCost(false);
                samples.Sort();
                double mean = samples.Average(), worst = samples[^1], p95 = samples[(int)(samples.Count * 0.95)];
                double heights = Planner.s_costHeightTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, samples.Count);
                int f = Math.Max(1, samples.Count);
                return $"ok planner guide cost: per frame {Planner.s_costBlocks / f} blocks, {Planner.s_costLabels / f} labels, {Planner.s_costIcons / f} icons, heights {heights:0.000} ms, labels {Planner.s_costLabelTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / f:0.000} ms, icons {Planner.s_costIconTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / f:0.000} ms; {(closed ? "menu closed" : still ? "guide shown, still" : $"guide shown, {keys.Count} rows scrolled")}, {samples.Count} frames: mean {mean:0.000} ms, 95th {p95:0.000} ms, worst {worst:0.000} ms";
            },
        };
    }

    // planner guide view checklist|guide
    private static CommandResult View(CommandCall call)
    {
        string which = call.Lower(3);
        if (which is not ("checklist" or "guide")) return CommandResult.Error($"planner guide view: usage {Usage}");
        Planner.SetGuideView(which == "guide");
        if (call.Args.Length >= 4)
        {
            if (Planner.Library.Find(call.Args[3]) is not { } build) return CommandResult.Ok($"planner guide view: no build {call.Args[3]}");
            Planner.OpenBuildView(build.Id);
        }
        return CommandResult.Ok($"planner guide view: {which}{(call.Args.Length >= 4 ? " of " + call.Args[3] : string.Empty)}");
    }

    // planner guide fold <section|fold title> open|closed
    private static CommandResult Fold(CommandCall call)
    {
        if (call.Args.Length < 4 || call.Lower(call.Args.Length) is not ("open" or "closed")) return CommandResult.Error($"planner guide fold: usage {Usage}");
        string name = string.Join(' ', call.Args.Skip(2).Take(call.Args.Length - 3));
        return CommandResult.Ok("planner guide fold: " + Planner.SetGuideFold(name, call.Lower(call.Args.Length) == "open"));
    }

    private static string IconKeyOf(Embed embed) => embed.Kind == EmbedKind.Item ? $"item:{embed.ItemType}:{embed.SubType}:{embed.Unique}" : embed.Key;

    private static string Target(Embed embed) => embed.Kind switch
    {
        EmbedKind.Skill => $"ability {embed.AbilityId} \"{AbilityName(embed.AbilityId)}\"",
        EmbedKind.Node => $"node \"{NodeName(embed.Tree, embed.Node)}\"",
        EmbedKind.Item => $"item \"{embed.Item?.Name}\"{(embed.Unique != 0 ? embed.Set ? " set" : " unique" : string.Empty)} (type {embed.ItemType})",
        _ => $"affix \"{AffixList.get()?.GetAffixName(ushort.Parse(embed.Id))}\"",
    };

    private static string AbilityName(string? id)
    {
        var ability = id != null ? AbilityManager.getPlayerAbility(id) : null;
        return Game.Alive(ability) ? ability!.abilityName ?? ability.name : "?";
    }

    private static string NodeName(string tree, byte node) => GlobalTreeData.get()?.getTreeData(tree)?.getNode(node)?.name ?? "?";

    // planner guide probe skill <name> | node <tree>:<node> | item <type>:<sub>[:<unique>]: the game object found and
    // its members whose names speak of an icon or sprite, with their values.
    private static CommandResult Probe(CommandCall call)
    {
        string kind = call.Lower(3);
        string id = call.Rest(4);
        object? found = null;
        string what;
        switch (kind)
        {
            case "tip":
                return Tip(call);
            case "abilities":
            {
                var all = AbilityManager.instance?.playerAbilities;
                var hits = new List<string>();
                for (int i = 0; all != null && i < all.Count; i++)
                    if (Game.Alive(all[i]) && ($"{all[i].name} {all[i].abilityName} {all[i].playerAbilityID}").Contains(id, StringComparison.OrdinalIgnoreCase))
                        hits.Add($"  {all[i].playerAbilityID} name \"{all[i].name}\" display \"{all[i].abilityName}\"");
                hits.Insert(0, $"ok planner guide probe abilities {id}: {hits.Count} of {all?.Count ?? 0}");
                return new CommandResult(DevOutput.Reply("planner-guide-probe", hits));
            }
            case "skill":
            {
                var cls = Planner.ActiveBuild is { Stages.Count: > 0 } b ? CharacterClassList.getClass(b.Stages[0].ClassId) : null;
                string? abilityId = Planner.FindAbility(id, cls);
                var ability = abilityId != null ? AbilityManager.getPlayerAbility(abilityId) : null;
                found = Game.Alive(ability) ? ability : null;
                what = $"ability {abilityId ?? "none"}";
                break;
            }
            case "node":
            {
                string[] parts = id.Split(':');
                var data = parts.Length == 2 && byte.TryParse(parts[1], out byte n) ? GlobalTreeData.get()?.getTreeData(parts[0])?.getNode(n) : null;
                found = data;
                what = $"node {id}";
                break;
            }
            case "item":
            {
                string[] parts = id.Split(':');
                if (parts.Length < 2 || !byte.TryParse(parts[0], out byte type) || !ushort.TryParse(parts[1], out ushort sub)) return CommandResult.Error($"planner guide probe: usage {Usage}");
                ushort unique = parts.Length > 2 && ushort.TryParse(parts[2], out ushort u) ? u : (ushort)0;
                var items = ItemList.get();
                found = unique != 0 ? UniqueList.getUnique(unique) : items?.GetEquipmentSubItem(type, sub) ?? (object?)items?.GetNonEquipmentSubItem(type, sub);
                what = $"item {type}:{sub}:{unique} base \"{Planner.SubTypeName(type, sub)}\"";
                break;
            }
            default:
                return CommandResult.Error($"planner guide probe: usage {Usage}");
        }
        var lines = new List<string> { $"ok planner guide probe {kind} {id}: {what}, {(found != null ? found.GetType().FullName : "nothing found")}" };
        if (found != null) lines.AddRange(IconMembers(found));
        return new CommandResult(DevOutput.Reply("planner-guide-probe", lines));
    }

    // Opens the game's skill tooltip for an ability at a screen rect (the game's pixels from the bottom left), to see
    // whether it opens with no skill icon under the pointer.
    private static CommandResult Tip(CommandCall call)
    {
        UITooltipAbility? tip = null;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<UITooltipAbility>(), true))
            if (o.TryCast<UITooltipAbility>() is { } t && Game.Alive(t)) tip = t;
        if (tip == null) return CommandResult.Ok("planner guide probe tip: no UITooltipAbility");
        if (call.Lower(4) == "close")
        {
            tip.CloseTooltip();
            return CommandResult.Ok("planner guide probe tip: closed");
        }
        string name = call.Args.Length >= 4 ? call.Args[3] : string.Empty;
        var cls = Planner.ActiveBuild is { Stages.Count: > 0 } b ? CharacterClassList.getClass(b.Stages[0].ClassId) : null;
        var ability = Planner.FindAbility(name, cls) is { } aid ? AbilityManager.getPlayerAbility(aid) : null;
        if (!Game.Alive(ability)) return CommandResult.Ok($"planner guide probe tip: no ability {name}");
        float F(int i, float d) => call.Args.Length > i && float.TryParse(call.Args[i], out float v) ? v : d;
        var rect = new UnityEngine.Rect(F(4, Screen.width * 0.75f), F(5, Screen.height * 0.5f), F(6, 40f), F(7, 40f));
        tip.OpenTooltip(ability!, rect, new UnityEngine.Vector2(0f, 0.5f), false, false, false, false);
        return CommandResult.Ok($"planner guide probe tip: opened {ability!.abilityName} at {rect}, active {tip.gameObject.activeInHierarchy}");
    }

    private static IEnumerable<string> IconMembers(object o)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        foreach (var p in o.GetType().GetProperties(flags))
        {
            if (p.GetIndexParameters().Length > 0 || !(p.Name.Contains("icon", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("sprite", StringComparison.OrdinalIgnoreCase)))
                continue;
            string value;
            try
            {
                value = Inspect.Format(p.GetValue(o));
            }
            catch (Exception e)
            {
                value = "? " + (e.InnerException ?? e).GetType().Name;
            }
            yield return $"  {p.PropertyType.Name} {p.Name} = {value}";
        }
    }
}
