using EpochAssist.Core;
using EpochAssist.Features;
using EpochAssist.Features.PlannerFormats;

namespace EpochAssist.Dev;

// planner stage: a Last Epoch Tools build's stages as stored, adding a fixture as the next stage with no request, and
// each stage's gear as read from its own planner data. planner fetch stand-in: answers the next link's request with a
// fixture or a failure, so the link path runs without asking the site.
internal static class PlannerStageCommands
{
    public const string Usage = "planner stage list [build] | planner stage add <fixture> [build] | planner stage gear [build] [stage] | planner stage merge <from> <to> | planner stage move <build> <stage> up|down | planner stage remove <build> <stage> | planner stage rename <build> <stage> <name> | planner fetch stand-in <fixture>|refuse|timeout|off";

    // Fixtures sit in .reference/fixtures/planner/ (local only); ./build.sh dev mirrors them here so the game can read them.
    private static string FixtureDir => Path.Combine(DevBridge.Dir, "fixtures", "planner");

    public static CommandResult Run(CommandCall call) => call.Lower(2) switch
    {
        "list" => List(call),
        "add" => Add(call),
        "gear" => Gear(call),
        "merge" => Merge(call),
        "move" or "remove" or "rename" => Arrange(call),
        _ => CommandResult.Error($"planner stage: usage {Usage}"),
    };

    // The build named, else the character's build, else the library's first Last Epoch Tools build.
    private static Build? BuildFor(string id)
    {
        Planner.Library.EnsureLoaded();
        if (id.Length > 0) return Planner.Library.Find(id);
        return Planner.ActiveBuild ?? Planner.Library.Builds.FirstOrDefault(b => b.Source == LetFormat.Source);
    }

    // A fixture by path, by name under the fixtures, or by its Last Epoch Tools code ("oN234yby" → let/oN234yby.json).
    private static string? FixturePath(string name)
    {
        foreach (string path in new[] { name, Path.Combine(FixtureDir, name), Path.Combine(FixtureDir, "let", name), Path.Combine(FixtureDir, "let", name + ".json") })
            if (Path.IsPathRooted(path) && File.Exists(path)) return path;
        return null;
    }

    // A fixture parsed as a link's answer would be: its file name is its code.
    private static Parsed ParseFixture(string path)
    {
        string code = Path.GetFileNameWithoutExtension(path);
        return LetFormat.Parse(File.ReadAllText(path), PasteKind.LetPage + code, code, DateTime.Now);
    }

    // planner stage list [build]
    private static CommandResult List(CommandCall call)
    {
        string id = call.Args.Length >= 3 ? call.Args[2] : string.Empty;
        if (BuildFor(id) is not { } build) return CommandResult.Ok($"planner stage list: no build {id}");
        return new CommandResult(DevOutput.Reply("planner-stage", StageLines(build)));
    }

    private static List<string> StageLines(Build build)
    {
        var lines = new List<string>
        {
            $"ok planner stage list {build.Id} \"{build.Name}\": {build.Source}, {build.Stages.Count} stages, first {build.FirstStage}, named by player {(build.NamedByPlayer ? "yes" : "no")}, guide title {Planner.Stages.GuideTitleFor(build) ?? "none"}",
        };
        for (int i = 0; i < build.Stages.Count; i++)
        {
            var s = build.Stages[i];
            string plannerId = s.PlannerId ?? (i == 0 ? build.PlannerId : null) ?? "-";
            lines.Add($"  {i} \"{s.Name}\": class {s.ClassId} mastery {s.Mastery}, level {s.Level}, planner id {plannerId}, "
                + $"{(s.Raw != null ? $"own raw {s.Raw.Length}" : $"build raw {build.Raw.Length}")}, guide {s.GuideSlug ?? "none"}, url {s.Url ?? "build's"}");
        }
        return lines;
    }

    // planner stage add <fixture> [build]: as a link would add it, with the fixture standing in for the request.
    private static CommandResult Add(CommandCall call)
    {
        if (call.Args.Length < 3) return CommandResult.Error($"planner stage: usage {Usage}");
        if (FixturePath(call.Args[2]) is not { } path) return CommandResult.Error($"planner stage add: no fixture {call.Args[2]}");
        string id = call.Args.Length >= 4 ? call.Args[3] : string.Empty;
        if (BuildFor(id) is not { } target) return CommandResult.Ok($"planner stage add: no build {id}");
        if (ParseFixture(path).Build is not { } fetched) return CommandResult.Ok($"planner stage add: {Path.GetFileName(path)} isn't a Last Epoch Tools build");
        var stage = Planner.Stages.StageFrom(fetched);
        if (Planner.Stages.CanAdd(target, stage) is { } refused) return CommandResult.Ok($"planner stage add: {refused}");
        int at = Planner.Stages.StageIndexOf(target, stage.PlannerId);
        if (at >= 0) return CommandResult.Ok($"planner stage add: {stage.PlannerId} is already stage {at} of {target.Id}");
        if (!Planner.Library.AddStages(target.Id, new[] { stage })) return CommandResult.Ok($"planner stage add: {target.Id} couldn't be saved");
        Planner.Stages.RenameIfGuide(target);
        var lines = StageLines(target);
        lines[0] = $"ok planner stage add {Path.GetFileName(path)}: stage {target.Stages.Count - 1} of {target.Id}; " + lines[0][3..];
        return new CommandResult(DevOutput.Reply("planner-stage", lines));
    }

    // planner stage gear [build] [stage]: the stage's planned gear as read from its own data.
    private static CommandResult Gear(CommandCall call)
    {
        string id = call.Args.Length >= 3 && !int.TryParse(call.Args[2], out _) ? call.Args[2] : string.Empty;
        if (BuildFor(id) is not { } build) return CommandResult.Ok($"planner stage gear: no build {id}");
        string? index = call.Args.Skip(2).LastOrDefault(a => int.TryParse(a, out _));
        int stage = index != null ? int.Parse(index) : build == Planner.ActiveBuild ? Planner.ActiveStage : build.FirstStage;
        if (stage < 0 || stage >= build.Stages.Count) return CommandResult.Ok($"planner stage gear: {build.Id} has no stage {stage}");
        var gear = Planner.GearOf(build, stage);
        var lines = new List<string>
        {
            $"ok planner stage gear {build.Id} stage {stage} \"{build.Stages[stage].Name}\": {gear.Slots.Count} slots, {gear.Idols.Count} idols, {gear.Blessings.Count} blessings, "
                + $"weaver {gear.Weaver?.Total ?? 0} points, {gear.WeaverItems.Count} weaver items",
        };
        foreach (var (slot, item) in gear.Slots) lines.Add($"  {slot}: {ItemText(item)}");
        foreach (var item in gear.Idols) lines.Add($"  idol {item.X},{item.Y}: {ItemText(item)}");
        foreach (var (timeline, item) in gear.Blessings) lines.Add($"  blessing {timeline}: {ItemText(item)}");
        foreach (var (socket, item) in gear.WeaverItems) lines.Add($"  weaver {socket}: {ItemText(item)}");
        return new CommandResult(DevOutput.Reply("planner-stage", lines));
    }

    // planner stage merge <from> <to>: the library add, with no request.
    private static CommandResult Merge(CommandCall call)
    {
        if (call.Args.Length < 4) return CommandResult.Error($"planner stage: usage {Usage}");
        Planner.Library.EnsureLoaded();
        string words = Planner.MergeStages(call.Args[2], call.Args[3]);
        var lines = Planner.Library.Find(call.Args[3]) is { } target ? StageLines(target) : new List<string> { "ok" };
        lines[0] = $"ok planner stage merge: {words}; {(Planner.Library.Find(call.Args[2]) == null ? "source gone" : "source kept")}; " + lines[0][3..];
        return new CommandResult(DevOutput.Reply("planner-stage", lines));
    }

    // planner stage move <build> <i> up|down | remove <build> <i> | rename <build> <i> <name>
    private static CommandResult Arrange(CommandCall call)
    {
        string verb = call.Lower(2);
        if (call.Args.Length < 4 || !int.TryParse(call.Args[3], out int index)) return CommandResult.Error($"planner stage: usage {Usage}");
        Planner.Library.EnsureLoaded();
        string id = call.Args[2];
        bool done = verb switch
        {
            "move" => call.Lower(5) is "up" or "down" && Planner.Library.MoveStage(id, index, call.Lower(5) == "up" ? -1 : 1),
            "remove" => Planner.Library.RemoveStage(id, index),
            _ => call.Args.Length >= 5 && Planner.Library.RenameStage(id, index, call.Rest(5)),
        };
        if (Planner.Library.Find(id) is not { } build) return CommandResult.Ok($"planner stage {verb}: no build {id}");
        var lines = StageLines(build);
        lines[0] = $"ok planner stage {verb}: {(done ? "done" : "no change")}; " + lines[0][3..];
        return new CommandResult(DevOutput.Reply("planner-stage", lines));
    }

    private static string ItemText(PlannedItem p) =>
        $"{(p.Name.Length > 0 ? p.Name : "?")} (type {p.ItemType} sub {p.SubType} unique {p.Unique} rarity {p.Rarity}) affixes [{string.Join(" ", p.Affixes.Select(a => $"{a.Id}/t{a.Tier}"))}]";

    // planner fetch stand-in <fixture>|refuse|timeout|off
    public static CommandResult Fetch(CommandCall call)
    {
        if (call.Lower(2) != "stand-in" || call.Args.Length < 3) return CommandResult.Error($"planner fetch: usage {Usage}");
        string what = call.Args[2];
        switch (what.ToLowerInvariant())
        {
            case "off":
                Planner.Fetch.NoStandIn();
                return CommandResult.Ok("planner fetch stand-in off");
            case "refuse":
                Planner.Fetch.StandIn(null, FetchState.Refused);
                break;
            case "timeout":
                Planner.Fetch.StandIn(null, FetchState.TimedOut);
                break;
            default:
                if (FixturePath(what) is not { } path) return CommandResult.Error($"planner fetch stand-in: no fixture {what}");
                Planner.Fetch.StandIn(File.ReadAllText(path));
                break;
        }
        return CommandResult.Ok($"planner fetch stand-in: the next link's request is answered with {Planner.Fetch.StandInNow}; no request is made");
    }
}
