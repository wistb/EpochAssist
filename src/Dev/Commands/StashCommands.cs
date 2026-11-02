using EpochAssist.Core;
using EpochAssist.Features;
using Il2Cpp;
using Il2CppItemFiltering;

namespace EpochAssist.Dev;

// stash: reads the stash's folders and tabs as the game holds them (names, icon and colour ids, order, rules, items) and
// the tab prices; opens and closes the game's stash window; reports the stash containers and the window's icon and colour
// lists; turns the trace of the game's own stash calls on and off; prints the Stash page's presets, preview and steps;
// sets its choices. Only next, save-preset and probe-call change anything: next as the page's Apply, probe-call on the saved copy only.
internal static class StashCommands
{
    public const string Usage = "stash layout | stash costs | stash open|close | stash ui | stash trace on|off | stash presets | stash key"
        + " | stash preview|steps [preset] [budget|max] [full] | stash next [--client] | stash choice <tabId|f:folderId> keep|reuse|reuse-if-empty"
        + " | stash fill [preset] | stash test <tabId> <item#> | stash save-preset <name> | stash budget <gold|max> | stash fill-cells <cells>|reset | stash overlay | stash bag | stash scramble | stash leave | stash reset [snapshot] [character] (saved copy: back to a tools/devloader/stash-saves.sh snapshot and in again)"
        + " | stash probe-call buy|new-folder|configure-tab|configure-folder|rules|reorder-tab … (saved copy)"
        + " | stash search [<json>|<code>|<saved name>|off|go <n>|panel|time|drop <group>|restore|save <name>|load <name>|code|saved] | stash why [n|all [every]|<tab> <x> <y>] | stash picker <query> | stash item <tab> [x y]|hovered | stash affixes <property> | stash press tab|folder <id> [onclick|pointer] [vert|row] | stash box [set <text>|text <text>|clear] | stash tip <tab> <x> <y> [slot] [compare]|close | stash tree quickview|bottom|<name> | stash syntax [read <text>|last] | stash tipstate";

    public static CommandResult Run(CommandCall call) => call.Lower(1) switch
    {
        "layout" => Layout(),
        "costs" => Costs(),
        "open" => Open(true),
        "close" => Open(false),
        "ui" => Ui(),
        "trace" => Trace(call),
        "presets" => PresetList(),
        "key" => Key(),
        "preview" => Preview(call, steps: false),
        "steps" => Preview(call, steps: true),
        "next" => Next(call),
        "choice" => Choice(call),
        "fill" => Fill(call),
        "test" => Test(call),
        "save-preset" => SavePreset(call),
        "budget" => Budget(call),
        "leave" => Leave(),
        "reset" => Reset(call),
        "time" => Time(),
        "fill-cells" => FillCells(call),
        "overlay" => CommandResult.Ok("stash overlay: " + Stash.OverlayState()),
        "bag" => Bag(),
        "limits" => Limits(),
        "scramble" => StashScramble.Start() is { } why ? CommandResult.Error("stash scramble: " + why) : CommandResult.Ok("stash scramble: started"),
        "burst" => Burst(call),
        "probe-call" => StashProbe.Run(call),
        "search" => Search(call),
        "item" => StashItemDump.Run(call),
        "why" => Why(call),
        "picker" => Picker(call),
        "affixes" => StashItemDump.Affixes(call),
        "press" => StashItemDump.Press(call),
        "box" => StashItemDump.Box(call),
        "tip" => StashItemDump.Tip(call),
        "tree" => StashItemDump.Tree(call),
        "audit" => new CommandResult(DevOutput.Reply("stash-audit", StashSearch.Audit())),
        "tipstate" => CommandResult.Ok("ok stash tipstate:\n" + ItemTooltip.State()),
        "syntax" => StashItemDump.Syntax(call),
        _ => CommandResult.Error($"stash: usage {Usage}"),
    };

    // stash search [<json> | off]: sets or clears the active search as the page would, or prints its state.
    private static CommandResult Search(CommandCall call)
    {
        var feature = FeatureHost.Find("stashsearch");
        if (feature == null) return CommandResult.Ok("stash search: not loaded");
        string arg = call.Rest(2).Trim();
        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            StashSearch.Load(new StashSearch.SearchDoc());
            return CommandResult.Ok("stash search: off");
        }
        if (call.Lower(2) == "drop" || call.Lower(2) == "restore")
        {
            // Simulates a game update that lost a group's members: the feature's own groups, or the data feature's values.
            var search = FeatureHost.Find("stashsearch");
            var data = FeatureHost.Find("data");
            if (search == null || data == null) return CommandResult.Error("stash search drop: features not found");
            if (call.Lower(2) == "restore")
            {
                foreach (string g in new[] { StashSearch.PanelGroup, StashSearch.TooltipGroup, StashSearch.GoGroup, StashSearch.BoxGroup, StashSearch.IconsGroup }) search.MissingGroups.Remove(g);
                data.MissingGroups.Remove(GameData.Values);
                return CommandResult.Ok("stash search restore: every group back (facts are read again by the next pass only for new items; reload for a clean read)");
            }
            string group = call.Rest(3).Trim();
            if (group == GameData.Values) data.MissingGroups.Add(group);
            else if (group is StashSearch.PanelGroup or StashSearch.TooltipGroup or StashSearch.GoGroup or StashSearch.BoxGroup or StashSearch.IconsGroup) search.MissingGroups.Add(group);
            else return CommandResult.Error($"stash search drop: one of \"{StashSearch.PanelGroup}\", \"{StashSearch.TooltipGroup}\", \"{StashSearch.GoGroup}\", \"{StashSearch.BoxGroup}\", \"{StashSearch.IconsGroup}\", \"{GameData.Values}\"");
            return CommandResult.Ok($"stash search drop: {group} missing until restore");
        }
        if (call.Lower(2) == "time")
        {
            var (items, scanTotal, scanMax, scanFrames) = StashSearch.ScanCost;
            var (checks, matchTotal, matchMax, matchFrames) = StashSearch.MatchCost;
            // A full re-check of the cache now, timed in one go, for the cost of a query change.
            double full = 0;
            if (StashSearch.Active is { } doc)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var place in StashSearch.Places)
                    if (StashSearch.FactsOf(place.Key) is { } f) StashSearch.Match(doc, f);
                full = sw.Elapsed.TotalMilliseconds;
            }
            return CommandResult.Ok($"stash search time: scan {items} items read, {scanTotal:0.00} ms in {scanFrames} frames, max {scanMax:0.000} ms a frame; "
                + $"match {checks} checks, {matchTotal:0.00} ms in {matchFrames} frames, max {matchMax:0.000} ms a frame; whole cache once {full:0.000} ms for {StashSearch.Places.Count} places"
                + (FeatureHost.Find("stashsearch") is { } fs ? $"; feature frames avg {fs.Record.Frames.AverageMs:0.000} ms, worst {fs.Record.Frames.WorstMs:0.000} ms, last second {fs.Record.Frames.LastSecondMs:0.000} ms, budget {fs.BudgetMs:0.0} ms" : string.Empty));
        }
        if (call.Lower(2) == "save")
        {
            string name = call.Rest(3).Trim().Trim('"');
            var error = StashSearch.SaveAs(StashSearch.Editing, name, out var saved);
            if (error != null) return CommandResult.Error("stash search save: " + error);
            StashSearch.Load(saved!, saved);
            return CommandResult.Ok($"stash search save: \"{saved!.Name}\" ({StashSearch.Saved.Count} saved, {StashSearch.Recent.Count} recent)");
        }
        if (call.Lower(2) == "load")
        {
            string name = call.Rest(3).Trim().Trim('"');
            if (StashSearch.FindSaved(name) is not { } saved) return CommandResult.Error($"stash search load: no saved search \"{name}\" (saved: {string.Join(", ", StashSearch.Saved.Select(d => d.Name))})");
            StashSearch.Load(saved, saved);
            return CommandResult.Ok($"stash search load: \"{saved.Name}\", {saved.Groups.Count} groups, {saved.RowCount} rows");
        }
        if (call.Lower(2) == "code")
        {
            var (code, error) = StashSearch.CodeOf(StashSearch.Editing);
            return code != null ? CommandResult.Ok($"stash search code: {code.Length} chars {code}") : CommandResult.Error("stash search code: " + error);
        }
        if (call.Lower(2) == "saved")
        {
            var list = new List<string> { $"ok stash search saved: {StashSearch.Saved.Count} saved, {StashSearch.Recent.Count} recent, current {StashSearch.CurrentSaved?.Name ?? "none"}" };
            list.AddRange(StashSearch.Saved.Select(d => $"  saved \"{d.Name}\": {d.ToJson()}"));
            list.AddRange(StashSearch.Recent.Select(d => $"  recent {StashSearch.Describe(d)}"));
            return new CommandResult(DevOutput.Reply("stash-search-saved", list));
        }
        if (arg.StartsWith(StashSearch.CodeTag, StringComparison.Ordinal))
        {
            if (StashSearch.FromCode(arg, out string? codeError) is not { } fromCode) return CommandResult.Error("stash search: " + codeError);
            StashSearch.Load(fromCode);
            return CommandResult.Ok($"stash search: from code, {fromCode.Groups.Count} groups, {fromCode.RowCount} rows, name \"{fromCode.Name}\"");
        }
        if (call.Lower(2) == "repack")
        {
            // Packing check: a code unpacked and packed again comes out the same, whatever its tag.
            string code = call.Rest(3).Trim();
            int colon = code.IndexOf(':');
            string tag = colon > 1 ? code[..(colon - 1)] : string.Empty;
            var (version, json, error) = ShareCode.Unpack(tag, 9, code);
            if (error != null) return CommandResult.Error("stash search repack: " + error);
            string again = ShareCode.Pack(tag, version, json!);
            return CommandResult.Ok($"stash search repack: {tag}{version}, {json!.Length} JSON chars, {(again == code ? "identical" : "DIFFERS")}");
        }
        if (call.Lower(2) == "panel") return CommandResult.Ok("stash search panel: " + StashSearch.PanelState() + $"; highlight {StashSearch.HighlightActive}; go: {StashSearch.GoLog}");
        if (call.Lower(2) == "go")
        {
            var results = StashSearch.Grouped().SelectMany(f => f.Tabs).SelectMany(t => t.Items).ToList();
            if (!int.TryParse(call.Lower(3), out int n) || n < 1 || n > results.Count) return CommandResult.Error($"stash search go: result 1–{results.Count}");
            return CommandResult.Ok($"stash search go {n}: {StashSearch.GoTo(results[n - 1].Place)} (stash search panel prints the steps)");
        }
        if (arg.StartsWith("{"))
        {
            var doc = StashSearch.Parse(arg, out string? error);
            if (doc == null) return CommandResult.Error("stash search: " + error);
            StashSearch.Load(doc);
            var notes = doc.Problems();
            return CommandResult.Ok($"stash search: set, {doc.Groups.Count} groups, {doc.RowCount} rows{(doc.IsEmpty ? ", empty (lists nothing)" : string.Empty)}{(notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty)}");
        }
        if (arg.Length > 0 && StashSearch.FindSaved(arg.Trim('"')) is { } named)
        {
            StashSearch.Load(named, named);
            return CommandResult.Ok($"stash search: saved \"{named.Name}\"");
        }
        if (arg.Length > 0) return CommandResult.Error("stash search <json|code|saved name> | off");
        var status = feature.Status;
        var active = StashSearch.Active;
        var lines = new List<string>
        {
            $"ok stash search: {feature.State}, {status.State} \"{status.Detail}\", facts {StashSearch.FactsCount} read, places {StashSearch.Places.Count}, pass {(StashSearch.FactsQueued > 0 ? "running" : "idle")}",
            active == null ? "  no active search" : $"  generation {StashSearch.Generation}, {(StashSearch.Matching ? "searching" : "done")}, {StashSearch.ResultCount} matches",
        };
        if (active != null) lines.Add("  " + active.ToJson());
        foreach (var folder in StashSearch.Grouped())
        {
            lines.Add($"  folder \"{folder.Folder.Name}\" · {folder.Count}");
            foreach (var tab in folder.Tabs)
                lines.Add($"    tab {tab.Tab.Id} \"{tab.Tab.Name}\" · {tab.Items.Count}: " + string.Join(", ", tab.Items.Select(r => $"({r.Place.X},{r.Place.Y}){(r.Badge != null ? " " + r.Badge : string.Empty)}")));
        }
        return new CommandResult(DevOutput.Reply("stash-search", lines));
    }

    // stash picker <query>: the picker's first ten entries for a query, with heading and score, best first per heading
    // order as the list shows them.
    private static CommandResult Picker(CommandCall call)
    {
        string query = call.Rest(2).Trim().Trim('"');
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return CommandResult.Error("stash picker <query>");
        var entries = StashSearch.Catalogue();
        var ranked = entries.Select((e, i) => (Entry: e, Index: i, Score: StashSearch.Rank(e, words))).Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Index).ToList();
        var lines = new List<string> { $"ok stash picker \"{query}\": {ranked.Count} of {entries.Count} (best overall; the list groups them under headings)" };
        foreach (var x in ranked.Take(10)) lines.Add($"  {x.Score,5} {x.Entry.Heading}: {x.Entry.Text} [{x.Entry.Ref.Source}:{x.Entry.Ref.Id}]");
        return new CommandResult(DevOutput.Reply("stash-picker", lines));
    }

    // stash why [n | all | <tab> <x> <y>]: the active search against one result, every item, or the item in a cell.
    private static CommandResult Why(CommandCall call)
    {
        var doc = StashSearch.Active;
        if (doc == null) return CommandResult.Ok("stash why: no active search");
        var results = StashSearch.Grouped().SelectMany(f => f.Tabs).SelectMany(t => t.Items).ToList();
        var lines = new List<string>();
        void One(StashSearch.ItemPlace place)
        {
            var facts = StashSearch.FactsOf(place.Key);
            lines.Add($"tab {place.TabId} ({place.X},{place.Y}) \"{facts?.Name}\"");
            if (facts == null) lines.Add("  not read yet");
            else lines.AddRange(StashSearch.Why(doc, facts).Select(l => "  " + l));
        }
        if (call.Lower(2) == "all")
        {
            int matched = 0;
            foreach (var place in StashSearch.Places.OrderBy(p => p.TabId).ThenBy(p => p.Y).ThenBy(p => p.X))
            {
                var facts = StashSearch.FactsOf(place.Key);
                if (facts == null) continue;
                var (match, badge) = StashSearch.Match(doc, facts);
                if (match) matched++;
                if (match || call.Lower(3) == "every") lines.Add($"{(match ? "MATCH" : "no   ")} tab {place.TabId} ({place.X},{place.Y}) \"{facts.Name}\"{(badge != null ? " badge " + badge : string.Empty)}");
            }
            lines.Insert(0, $"ok stash why all: {matched} of {StashSearch.Places.Count} places match (add 'every' for the rest)");
        }
        else if (int.TryParse(call.Lower(2), out int a) && int.TryParse(call.Lower(3), out int x) && int.TryParse(call.Lower(4), out int y))
        {
            var place = StashSearch.Places.FirstOrDefault(p => p.TabId == a && p.X == x && p.Y == y);
            if (place.Key == null) return CommandResult.Error($"stash why: nothing at ({x},{y}) in tab {a}");
            lines.Add("ok stash why:");
            One(place);
        }
        else if (int.TryParse(call.Lower(2), out int n))
        {
            if (n < 1 || n > results.Count) return CommandResult.Error($"stash why: result 1–{results.Count}");
            lines.Add($"ok stash why: result {n}");
            One(results[n - 1].Place);
        }
        else
        {
            lines.Add($"ok stash why: {results.Count} results");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                lines.Add($"  {i + 1}. tab {r.Place.TabId} ({r.Place.X},{r.Place.Y}) \"{StashSearch.FactsOf(r.Place.Key)?.Name}\"{(r.Badge != null ? " badge " + r.Badge : string.Empty)}");
            }
        }
        return new CommandResult(DevOutput.Reply("stash-why", lines));
    }

    private static CommandResult PresetList()
    {
        var lines = new List<string> { $"ok stash presets: {Stash.Presets.All.Count}" };
        foreach (var p in Stash.Presets.All)
        {
            var tabs = p.AllTabs().ToList();
            lines.Add($"  {p.Id} \"{p.Name}\"{(p.BuiltIn ? " built-in" : string.Empty)}: {p.Folders.Count} folders, {tabs.Count(t => !t.Tab.IsMerged)} sections, {tabs.Count(t => t.Tab.IsMerged)} merged");
        }
        return new CommandResult(DevOutput.Reply("stash-presets", lines));
    }

    private static CommandResult Key()
    {
        var snap = Stash.Read();
        if (snap == null) return CommandResult.Ok("stash key: no stash (load a character)");
        string path = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "EpochAssist", "stash", "stashes", snap.Key.File + ".json");
        return CommandResult.Ok($"stash key: {snap.Key.Id} ({snap.Source} copy), file stashes/{snap.Key.File}.json{(File.Exists(path) ? string.Empty : " (not saved yet)")}");
    }

    // [preset] [budget|max] [full], in any order; defaults are the page's.
    private static (Stash.Preset Preset, long Budget, bool Full, string? Error) Args(CommandCall call, int from)
    {
        var preset = Stash.CurrentPreset;
        long budget = Stash.CurrentChoices.Budget;
        bool full = Stash.CurrentChoices.FullLayout;
        for (int i = from; i <= call.Args.Length; i++)
        {
            string a = call.Lower(i);
            if (a == "full") full = true;
            else if (a == "max") budget = long.MaxValue;
            else if (long.TryParse(a, out long b)) budget = b;
            else if (Stash.Presets.Find(a) is { } p) preset = p;
            else return (preset, budget, full, $"stash: no preset {a}");
        }
        return (preset, budget, full, null);
    }

    private static CommandResult Preview(CommandCall call, bool steps)
    {
        var snap = Stash.Read();
        if (snap == null) return CommandResult.Ok("stash preview: no stash (load a character)");
        var (preset, budget, full, error) = Args(call, 2);
        if (error != null) return CommandResult.Error(error);
        var choices = Stash.ChoicesFile.Load(snap.Key, snap);
        choices.FullLayout = full;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var preview = Stash.PlanNow(snap, preset, choices, budget);
        double planMs = watch.Elapsed.TotalMilliseconds;
        var lines = new List<string>
        {
            $"ok stash {(steps ? "steps" : "preview")} {preset.Id} budget {(budget == long.MaxValue ? "max" : budget.ToString())}{(full ? " full" : string.Empty)}: {preview.Targets.Count} targets, {preview.Reusable} reusable, {preview.NewTabs} new, cost {preview.Cost}, whole preset {preview.FullCost} ({preview.FullTabs} tabs), split {string.Join(",", preview.Split)}, {preview.Warnings.Count} warnings, planned in {planMs:F1} ms",
        };
        foreach (string note in preview.Notes.Distinct()) lines.Add("  note " + note);
        if (!steps)
        {
            foreach (var f in preview.Folders.OrderBy(f => f.Index))
            {
                lines.Add($"  folder #{f.Index} \"{f.Name}\" {(f.Keep ? "keep" : f.ExistingId == null ? "new" : f.Configure ? $"configure {f.ExistingId}" : $"existing {f.ExistingId}")}");
                foreach (var x in preview.Targets.Where(x => x.Folder == f).OrderBy(x => x.Index))
                    lines.Add($"    #{x.Index} {x.Tab.Key} copy {x.Copy} rank {x.Tab.Rank} fill={Stash.FillText(x)}{(x.Changes.Count > 0 ? " changes " + string.Join(",", x.Changes) : string.Empty)} \"{x.Tab.Name}\" · {Stash.Rules.Describe(x.Rules)}");
            }
            foreach (var w in preview.Warnings) lines.Add($"  warn {w.TargetKey} taken by {w.TakenById} \"{w.TakenByName}\" ({w.Example})");
        }
        else
        {
            var list = Stash.StepsFor(snap, preview);
            int left = Stash.StepsLeft(snap, preview);
            lines.Add($"  {list.Count} steps now, {left} in all with the ones that follow, {list.Sum(s => s.Gold)} gold");
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                string blocked = i == 0 && Stash.Blocked(s, snap) is { } why ? $" · blocked: {why}" : string.Empty;
                lines.Add($"  {i + 1}. {s.Kind} id {s.Id?.ToString() ?? "-"} gold {s.Gold}: \"{s.Says}\" · sends {s.Sends}{blocked}");
            }
        }
        return new CommandResult(DevOutput.Reply("stash-preview", lines));
    }

    private static CommandResult Next(CommandCall call)
    {
        var snap = Stash.Read();
        if (snap == null) return CommandResult.Ok("stash next: no stash (load a character)");
        if (snap.ClientCopy && call.Lower(2) != "--client") return CommandResult.Error("stash next: client copy; add --client (maintainer passes only)");
        Stash.Refresh(force: true);
        if (!Stash.ApplyNext(out string message)) return CommandResult.Ok($"stash next: not sent: {message}");
        float until = UnityEngine.Time.unscaledTime + 6f;
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                Stash.Frame();
                if (Stash.StepsBusy && UnityEngine.Time.unscaledTime < until) return null;
                var after = Stash.Snapshot;
                return Stash.State == Stash.ApplyState.Stopped
                    ? $"error stash next: {message}; stopped: {Stash.StopReason}"
                    : $"ok stash next: {message}; confirmed; now {after?.TabCount} tabs, {after?.Folders.Count} folders, gold {after?.Gold}";
            },
        };
    }

    private static CommandResult Choice(CommandCall call)
    {
        var snap = Stash.Read();
        if (snap == null) return CommandResult.Ok("stash choice: no stash");
        Stash.Refresh();
        var choice = call.Lower(3) switch
        {
            "keep" => Stash.TabChoice.Keep, "reuse" => Stash.TabChoice.Reuse, "reuse-if-empty" => Stash.TabChoice.ReuseIfEmpty,
            _ => (Stash.TabChoice?)null,
        };
        if (choice == null) return CommandResult.Error("stash choice: keep, reuse or reuse-if-empty");
        string target = call.Lower(2);
        if (target.StartsWith("f:") && int.TryParse(target[2..], out int fid) && snap.Folder(fid) != null) Stash.CurrentChoices.SetFolder(snap, fid, choice.Value);
        else if (int.TryParse(target, out int tid) && snap.Tab(tid) != null) Stash.CurrentChoices.Tabs[tid] = choice.Value;
        else return CommandResult.Error($"stash choice: no tab or folder {target}");
        Stash.ChoicesChanged();
        return CommandResult.Ok($"stash choice: {target} {Stash.ChoiceText(choice.Value)}");
    }

    private static CommandResult Fill(CommandCall call)
    {
        var snap = Stash.Read();
        if (snap == null) return CommandResult.Ok("stash fill: no stash");
        var preset = call.Args.Length >= 2 && Stash.Presets.Find(call.Lower(2)) is { } p ? p : Stash.CurrentPreset;
        var tabs = Stash.FillCounts(snap, preset);
        var detail = Stash.FillDetail;
        var lines = new List<string> { $"ok stash fill {preset.Id}: {Stash.StashItems().Count()} items" };
        foreach (var (key, n) in tabs)
        {
            var d = detail.GetValueOrDefault(key);
            if (d.Items > 0 || n > 1) lines.Add($"  {key}: {d.Items} items, {d.Cells} cells, {n} tab{(n == 1 ? string.Empty : "s")}");
        }
        return new CommandResult(DevOutput.Reply("stash-fill", lines));
    }

    // The game's rule test: item n of the stash (as stash fill counts them) against a tab's rules or a preset tab's.
    private static CommandResult Test(CommandCall call)
    {
        var snap = Stash.Read();
        if (snap == null) return CommandResult.Ok("stash test: no stash");
        var items = Stash.StashItems().ToList();
        if (!int.TryParse(call.Lower(3), out int n) || n < 0 || n >= items.Count) return CommandResult.Error($"stash test: item 0–{items.Count - 1}");
        Stash.TabRules rules;
        string what = call.Lower(2);
        if (int.TryParse(what, out int tid) && snap.Tab(tid) is { } tab) rules = tab.Rules;
        else if (Stash.CurrentPreset.AllTabs().FirstOrDefault(p => p.Tab.Key == what).Tab is { } pt) rules = Stash.RulesOf(pt);
        else return CommandResult.Error($"stash test: no tab or preset tab {what}");
        var game = Stash.Rules.ToGame(rules);
        var item = items[n].Data;
        int level = PlayerFinder.getLocalTreeData()?.characterLevel ?? 100;
        bool match = game != null && game.Match(item, level);
        var back = Stash.Rules.FromGame(game);
        return CommandResult.Ok($"stash test: {what} ({Stash.Rules.Describe(rules)}) vs item {n} \"{item.FullName}\" (rarity {item.rarity}, type {item.itemType}, lp {item.legendaryPotential}): {(match ? "matches" : "no match")}; rules round trip {(back.Equals(rules) ? "equal" : "differs: " + Stash.Rules.Describe(back))}");
    }

    // The server's burst allowance, measured once with the maintainer's go-ahead: three colour-only configures of one empty
    // tab, gap ms apart (to colour A, to colour B, back to its own), through the window's own handler, then 3 s of the
    // client copy's changes. Ending on its own colour means all three landed; on A or B, the later ones were dropped.
    private static CommandResult Burst(CommandCall call)
    {
        if (!int.TryParse(call.Lower(2), out int id) || !int.TryParse(call.Lower(3), out int gap)) return CommandResult.Error("stash burst <tabId> <gap ms>");
        var snap = Stash.Read();
        if (snap?.Tab(id) is not { } tab) return CommandResult.Error($"stash burst: no tab {id}");
        if (tab.ItemCount > 0) return CommandResult.Error("stash burst: pick an empty tab");
        var ui = UnityEngine.Resources.FindObjectsOfTypeAll<StashTabbedUIControls>().FirstOrDefault(c => c.gameObject.scene.name != null);
        if (ui == null) return CommandResult.Error("stash burst: no stash controls");
        int own = tab.ColourId, a = own == 3 ? 4 : 3, b = own == 5 ? 6 : 5;
        int[] colours = { a, b, own };
        float start = UnityEngine.Time.unscaledTime;
        int sent = 0, last = own;
        var log = new List<string> { $"ok stash burst: tab {id} \"{tab.Name}\" colour {own}; sends {a}, {b}, {own} {gap} ms apart" };
        var game = Stash.Rules.ToGame(tab.Rules, forSend: true);
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                float t = UnityEngine.Time.unscaledTime - start;
                while (sent < colours.Length && t >= sent * gap / 1000f)
                {
                    ui.HandleConfigureTabResult(new Il2CppLE.UI.PanelSystem.StashConfigureTabModalResult
                    {
                        SelectedTabId = id, SelectedIconId = tab.IconId, SelectedColorId = colours[sent], SelectedParentId = tab.FolderId,
                        TabName = tab.Name, TabPriority = game, IgnoreFolderPriority = tab.IgnoresFolderRules,
                        Action = Il2CppLE.UI.PanelSystem.StashConfigureResult.Save,
                    });
                    log.Add($"  {t * 1000f,6:F0} ms sent colour {colours[sent]}");
                    sent++;
                }
                var now = Stash.Read()?.Tab(id);
                if (now != null && now.ColourId != last)
                {
                    log.Add($"  {t * 1000f,6:F0} ms client copy shows colour {now.ColourId}");
                    last = now.ColourId;
                }
                if (t < sent * gap / 1000f + 3f || sent < colours.Length) return null;
                log.Add(last == own ? "  ended on its own colour: all three applied" : $"  ended on colour {last}: the later sends were dropped");
                return string.Join("\n", log);
            },
        };
    }

    // The network layer's receive limits for the stash messages: a leaky bucket per message type (size, drained per
    // second). The receiving side drops a message over its bucket. Reads only.
    private static CommandResult Limits()
    {
        var names = new Dictionary<ushort, string>
        {
            [Il2CppLE.Networking.Generated.PlayerSync.MessageStashTabPurchase.MessageID] = "StashTabPurchase",
            [Il2CppLE.Networking.Generated.PlayerSync.MessageStashTabConfigure.MessageID] = "StashTabConfigure",
            [Il2CppLE.Networking.Generated.PlayerSync.MessageStashCategoryCreate.MessageID] = "StashCategoryCreate",
            [Il2CppLE.Networking.Generated.PlayerSync.MessageStashCategoryConfigure.MessageID] = "StashCategoryConfigure",
            [Il2CppLE.Networking.Generated.PlayerSync.MessageStashTabReorder.MessageID] = "StashTabReorder",
            [Il2CppLE.Networking.Generated.PlayerSync.MessageStashCategoryReorder.MessageID] = "StashCategoryReorder",
            [Il2CppLE.Networking.Generated.PlayerSync.MessageStashTabUpdateParent.MessageID] = "StashTabUpdateParent",
        };
        // Read by reflection: the limit types live in the game's networking assembly, which the mod doesn't reference.
        var router = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("Il2CppLE.Networking.Generated.GeneratedMessageRouter")).FirstOrDefault(t => t != null);
        var limits = router?.GetProperty("RateLimits")?.GetValue(null) as System.Collections.IEnumerable;
        var lines = new List<string> { $"ok stash limits: PlayerSync.StashRateLimitPerSecond {PlayerSync.StashRateLimitPerSecond}" };
        var seen = new HashSet<ushort>();
        int count = 0;
        if (limits != null)
            foreach (var l in limits)
            {
                count++;
                var t = l.GetType();
                object? F(string n) => t.GetField(n)?.GetValue(l) ?? t.GetProperty(n)?.GetValue(l);
                if (F("MessageID") is not ushort id || !names.TryGetValue(id, out var n)) continue;
                seen.Add(id);
                lines.Add($"  {n} (id {id}): bucket {F("ReceivedLimit")}, drains {F("ReceivedLeakRate")}/s");
            }
        lines[0] += $"; {count} limited message types";
        foreach (var (id, n) in names)
            if (!seen.Contains(id)) lines.Add($"  {n} (id {id}): no limit listed");
        return new CommandResult(DevOutput.Reply("stash-limits", lines));
    }

    // What each part of a refresh costs, over five runs (milliseconds, best and worst); reads only.
    // Fit items counts this many cells a tab (from 1; "reset" for the real 12 × 17), so the stash's few items spill into
    // copies of a tab.
    private static CommandResult FillCells(CommandCall call)
    {
        string arg = call.Lower(2);
        if (arg == "reset") Stash.SetFillTabCells(Stash.TabCells);
        else if (int.TryParse(arg, out int cells) && cells > 0) Stash.SetFillTabCells(cells);
        else if (arg.Length > 0) return CommandResult.Error("stash fill-cells: usage stash fill-cells <cells>|reset");
        return CommandResult.Ok($"stash fill-cells: {Stash.FillTabCells} cells a tab");
    }

    // Each item in the bag against the preview's tabs in the game's order: the first that takes it, and which tabs'
    // single conditions it passes (to see which one decides).
    private static CommandResult Bag()
    {
        var preview = Stash.CurrentPreview;
        if (preview == null) return CommandResult.Ok("stash bag: no preview (open the Stash page)");
        var content = ItemContainersManager.Instance?.inventory?.TryCast<IItemContainer>()?.GetContent();
        if (content == null) return CommandResult.Ok("stash bag: no inventory");
        int level = PlayerFinder.getLocalTreeData()?.characterLevel ?? 100;
        var tabs = preview.Folders.OrderBy(f => f.Index).SelectMany(f => preview.Targets.Where(x => x.Folder == f).OrderBy(x => x.Index))
            .Select(x => (x.Tab.Name, Rules: x.Rules, Game: Stash.Rules.ToGame(x.Rules))).Where(t => t.Game != null).ToList();
        var lines = new List<string> { $"ok stash bag: {content.Count} items, {tabs.Count} tabs with rules" };
        for (int i = 0; i < content.Count; i++)
        {
            if (content[i]?.data?.getAsUnpacked() is not { } item) continue;
            var first = tabs.FirstOrDefault(t => t.Game!.Match(item, level));
            string name;
            try { name = item.FullName; } catch { name = "?"; }
            // The class condition alone, for the tabs that have one.
            var byClass = tabs.Where(t => t.Rules.Class != 0).Select(t =>
            {
                var only = Stash.Rules.ToGame(new Stash.TabRules { Class = t.Rules.Class })!;
                return $"{t.Name}:{(only.Match(item, level) ? "yes" : "no")}";
            });
            lines.Add($"  {name} (type {item.itemType}, class req {item.classReq}) → {first.Name ?? "no tab"}; class alone: {string.Join(" ", byClass)}");
        }
        return new CommandResult(DevOutput.Reply("stash-bag", lines));
    }

    private static CommandResult Time()
    {
        var lines = new List<string>();
        var w = new System.Diagnostics.Stopwatch();
        var times = new Dictionary<string, List<double>>();
        void Lap(string part, Action run)
        {
            w.Restart();
            run();
            (times.TryGetValue(part, out var l) ? l : times[part] = new List<double>()).Add(w.Elapsed.TotalMilliseconds);
        }
        Stash.StashSnapshot? snap = null;
        Stash.Preview? preview = null;
        var preset = Stash.CurrentPreset;
        var choices = Stash.CurrentChoices;
        for (int i = 0; i < 5; i++)
        {
            Lap("read", () => snap = Stash.Read());
            if (snap == null) return CommandResult.Ok("stash time: no stash");
            Lap("plan", () => preview = Stash.Plan(Stash.InputFor(snap, preset, choices, choices.Budget)));
            Lap("fill counts", () => Stash.FillCounts(snap, preset));
            Lap("order check", () => Stash.CheckOrder(snap, preview!));
            Lap("steps", () => Stash.StepsFor(snap, preview!));
            Lap("stash items", () => Stash.StashItems().Count());
            Lap("refresh (forced)", () => Stash.Refresh(force: true));
            Lap("rows", Stash.BuildRows);
        }
        lines.Add($"ok stash time: {snap!.TabCount} tabs, {snap.Folders.Count} folders, {preview!.Targets.Count} targets");
        foreach (var (part, l) in times) lines.Add($"  {part}: {string.Join(" ", l.Select(v => v.ToString("F2")))} ms");
        w.Restart();
        foreach (var (_, tab) in preset.AllTabs()) Stash.RulesOf(tab);
        lines.Add($"  rules of every preset tab: {w.Elapsed.TotalMilliseconds:F2} ms");
        if (Modules.NotLoaded("planner") is { } why)
        {
            lines.Add($"  planner uniques: {why}");
        }
        else
        {
            w.Restart();
            Planner.LibraryUniques();
            lines.Add($"  planner uniques: {w.Elapsed.TotalMilliseconds:F2} ms");
        }
        return new CommandResult(DevOutput.Reply("stash-time", lines));
    }

    // On the saved copy only: the game's own Exit to Character Select, so the stash files can be swapped.
    private static CommandResult Leave()
    {
        if (Stash.Read() is not { ClientCopy: false }) return CommandResult.Error("stash leave: saved copy only");
        var ui = UIBase.instance;
        if (!Game.Alive(ui)) return CommandResult.Error("stash leave: no game UI");
        ui.ExitToCharacterSelect();
        return CommandResult.Ok("stash leave: exiting to character select");
    }

    // On the saved copy only: back to a snapshot saved by tools/devloader/stash-saves.sh and in again as the same (or the named)
    // character; the reply comes once the character is loaded.
    private static CommandResult Reset(CommandCall call)
    {
        string name = call.Args.Length >= 2 ? call.Args[1] : StashReset.DefaultSnapshot;
        if (StashReset.Start(name, call.Rest(3)) is { } why) return CommandResult.Error("stash reset: " + why);
        return new CommandResult(string.Empty) { Pending = () => StashReset.Running ? null : StashReset.Result };
    }

    private static CommandResult Budget(CommandCall call)
    {
        if (Stash.Read() == null) return CommandResult.Ok("stash budget: no stash");
        Stash.Refresh();
        long budget = call.Lower(2) == "max" ? long.MaxValue / 4 : long.TryParse(call.Lower(2), out long b) && b >= 0 ? b : -1;
        if (budget < 0) return CommandResult.Error("stash budget: gold or max");
        Stash.CurrentChoices.Budget = budget;
        Stash.ChoicesChanged();
        // As far as the page's slider goes: the whole preset's cost, at most the gold there is.
        if (call.Lower(2) == "max" && Stash.CurrentPreview is { } whole && Stash.Snapshot is { } snap)
        {
            budget = Math.Max(0, Math.Min(whole.FullCost, snap.Gold));
            Stash.CurrentChoices.Budget = budget;
            Stash.ChoicesChanged();
        }
        var p = Stash.CurrentPreview;
        return CommandResult.Ok($"stash budget: {budget}; {p?.NewTabs} new tabs, cost {p?.Cost}");
    }

    private static CommandResult SavePreset(CommandCall call)
    {
        var snap = Stash.Read();
        if (snap == null) return CommandResult.Ok("stash save-preset: no stash");
        string name = call.Rest(2).Trim();
        if (name.Length == 0) return CommandResult.Error("stash save-preset: name it");
        var p = Stash.Capture(snap, name);
        return p == null ? CommandResult.Error("stash save-preset: not saved (see the log)") : CommandResult.Ok($"stash save-preset: {p.Id} \"{p.Name}\", {p.Folders.Count} folders, {p.AllTabs().Count()} sections");
    }

    private static CommandResult Layout()
    {
        var lines = new List<string>();
        var info = (LocalPlayer.instance ?? PlayerFinder.localPlayer)?.stashInfo;
        var saved = PlayerFinder.getGlobalDataTracker()?.stash;
        if (info == null && saved == null) return CommandResult.Ok("stash layout: no stash (load a character)");
        if (info != null)
        {
            lines.Add($"ok stash layout (client copy): {info.categories?.Count ?? 0} folders, {info.tabs?.Count ?? 0} tabs");
            if (info.categories != null)
                foreach (var c in info.categories.ToArray().OrderBy(c => c.DisplayOrder))
                {
                    lines.Add($"  folder {c.CategoryId} #{c.DisplayOrder} \"{c.DisplayName}\" icon {c.IconId} colour {c.ColorId}{Rules(c.FolderPriority)}");
                    if (info.tabs != null)
                        foreach (var t in info.tabs.ToArray().Where(t => t.CategoryId == c.CategoryId).OrderBy(t => t.DisplayOrder))
                            lines.Add($"    tab {t.TabId} #{t.DisplayOrder} \"{t.DisplayName}\" icon {t.IconId} colour {t.ColorId}{(t.IgnoreFolderPriority ? " ignores folder rules" : string.Empty)}{Rules(t.TabPriority)}");
                }
            if (info.tabs != null)
                foreach (var t in info.tabs.ToArray().Where(t => info.categories == null || !info.categories.ToArray().Any(c => c.CategoryId == t.CategoryId)))
                    lines.Add($"  loose tab {t.TabId} #{t.DisplayOrder} \"{t.DisplayName}\" folder {t.CategoryId}{Rules(t.TabPriority)}");
        }
        if (saved != null)
        {
            int tabs = saved.Tabs?.Count ?? 0;
            lines.Add($"saved stash (save file's copy): type {saved.StashType}, {saved.Categories?.Count ?? 0} folders, {tabs} tabs, tabs made {saved._stashTabsCreated}");
            if (saved.Categories != null)
                foreach (var c in saved.Categories.ToArray().OrderBy(c => c.DisplayOrder))
                    lines.Add($"  folder {c.CategoryId} #{c.DisplayOrder} \"{c.DisplayName}\" icon {c.IconId} colour {c.ColorId}{Rules(c.FolderPriority)}");
            if (saved.Tabs != null)
                for (int i = 0; i < saved.Tabs.Count; i++)
                {
                    var t = saved.Tabs[i];
                    lines.Add($"  tab {t.TabId} #{t.DisplayOrder} folder {t.CategoryId} \"{t.DisplayName}\" icon {t.IconId} colour {t.ColorId}: {t.SavedItems?.Count ?? 0} items{(t.IgnoreFolderPriority ? " · ignores folder rules" : string.Empty)}{Rules(t.TabPriority)}");
                }
        }
        return new CommandResult(DevOutput.Reply("stash-layout", lines));
    }

    // A tab's or folder's rules in the loot filter's words; empty when it has none.
    private static string Rules(StashPriority? p)
    {
        if (p == null || !p.HasValue()) return " · no rules";
        var parts = new List<string>();
        void Add(Condition? c)
        {
            if (c == null) return;
            // The Planner describes conditions; without it, only the kind.
            if (!Modules.Loaded("planner"))
            {
                parts.Add(c.GetIl2CppType().Name);
                return;
            }
            try
            {
                parts.Add(Planner.LootFilterGame.DescribeCondition(c));
            }
            catch (Exception e)
            {
                parts.Add($"{c.GetIl2CppType().Name} ({e.GetType().Name})");
            }
        }
        if (p.RarityStashPriority?.HasValue() == true) Add(p.RarityStashPriority);
        if (p.TypeStashPriority?.HasValue() == true) Add(p.TypeStashPriority);
        if (p.ClassStashPriority?.HasValue() == true) Add(p.ClassStashPriority);
        if (p.PotentialStashPriority?.HasValue() == true) Add(p.PotentialStashPriority);
        if (p.UniquesStashPriority?.HasValue() == true) Add(p.UniquesStashPriority);
        if (p.CorruptionStashPriority?.HasValue() == true) Add(p.CorruptionStashPriority);
        if (p.FactionStashPriority?.HasValue() == true) Add(p.FactionStashPriority);
        if (p.LevelStashPriority is { } level && level.HasValue()) parts.Add($"Lvl{level.MinLevel?.ToString() ?? "-"}-{level.MaxLevel?.ToString() ?? "-"}");
        return " · " + string.Join(" & ", parts);
    }

    private static CommandResult Costs()
    {
        var lines = new List<string>
        {
            $"ok stash costs: per tab {StashConstants.GoldCostPerTab}, between thresholds base {StashConstants.BaseTabGoldCostWhileBetweenThresholds} +{StashConstants.GoldCostPerTabAboveThreshold1} from tab {StashConstants.TabIncreaseThreshold1}, above base {StashConstants.BaseTabGoldCostAboveThreshold2} +{StashConstants.GoldCostPerTabAboveThreshold2} from tab {StashConstants.TabIncreaseThreshold2}; max {StashConstants.MaxTabs} tabs, folders max {StashManager.MaxFolderCount}",
        };
        int owned = (LocalPlayer.instance ?? PlayerFinder.localPlayer)?.stashInfo?.tabs?.Count ?? PlayerFinder.getGlobalDataTracker()?.stash?.Tabs?.Count ?? -1;
        int gold = PlayerFinder.localGoldTracker?.value ?? -1;
        lines.Add($"  owned {owned} tabs, gold {gold}");
        long total = 0;
        var cost = new List<string>();
        for (int n = 1; n <= StashConstants.MaxTabs; n++)
        {
            int c = StashHelpers.NextTabCost(n);
            total += c;
            if (n <= 60 || n % 10 == 0) cost.Add($"{n}:{c}");
        }
        lines.Add("  next tab cost by current count: " + string.Join(" ", cost));
        lines.Add($"  all {StashConstants.MaxTabs} tabs from 1: {total} gold");
        return new CommandResult(DevOutput.Reply("stash-costs", lines));
    }

    private static CommandResult Open(bool open)
    {
        var ui = UIBase.instance;
        if (!Game.Alive(ui)) return CommandResult.Ok("stash: no game UI");
        if (open) ui.openStash(true, false, true, string.Empty, true);
        else ui.closeStash(false);
        return CommandResult.Ok($"stash {(open ? "open" : "close")}: requested (open now {ui.IsStashOpen()})");
    }

    // The live containers (which stash is current, each tab's grid size and item count), the client copy, the stash
    // window's icon and colour lists, and the game's stash send limit.
    private static CommandResult Ui()
    {
        var lines = new List<string>();
        var ui = UIBase.instance;
        lines.Add($"ok stash ui: window open {(Game.Alive(ui) ? ui.IsStashOpen().ToString() : "no UI")}, stash initialized {(Game.Alive(ui) ? ui.stashInitialized.ToString() : "?")}, send limit {PlayerSync.StashRateLimitPerSecond}/s");
        var info = (LocalPlayer.instance ?? PlayerFinder.localPlayer)?.stashInfo;
        lines.Add($"  client copy: {(info == null ? "none" : $"{info.categories?.Count ?? -1} folders, {info.tabs?.Count ?? -1} tabs")}");
        var holder = ItemContainersManager.Instance?.stash;
        if (holder == null) lines.Add("  containers: none");
        else
        {
            var all = holder.stashItemContainers;
            lines.Add($"  containers: {all?.Length ?? 0} stashes, current {holder._CurrentStashIndex_k__BackingField}, keys {(holder.indexToKey == null ? "-" : string.Join(",", holder.indexToKey))}");
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                {
                    var c = all[i];
                    if (c == null) continue;
                    var linked = c.LinkedStash;
                    string tabs = c.containers == null ? "-" : string.Join(" ", c.containers.ToArray().Select((t, n) => $"{n}:{t.size.x}x{t.size.y}/{t.content?.Count ?? -1}"));
                    lines.Add($"    stash {i}: linked {(linked == null ? "none" : $"{linked.StashType} cycle {(int)linked.Cycle} id {linked.Id}")}, remove only {c.RemoveOnly}, active tab {c.currentlyActiveTab}, tabs {tabs}");
                }
        }
        var controls = UnityEngine.Resources.FindObjectsOfTypeAll<NewStashTabbedUIControls>();
        lines.Add($"  window controls: {controls.Length}");
        foreach (var c in controls)
        {
            var icons = c._tabIcons;
            var colours = c._tabColors;
            lines.Add($"    {c.gameObject.name} active {c.gameObject.activeInHierarchy}: icons {icons?.Count ?? -1}, colours {colours?.Count ?? -1}");
            if (colours != null)
                lines.Add("      colours " + string.Join(" ", colours.ToArray().Select((k, n) => $"{n}:#{UnityEngine.ColorUtility.ToHtmlStringRGB(k)}")));
            if (icons != null)
                lines.Add("      icons " + string.Join(" ", icons.ToArray().Select((k, n) => $"{n}:{(k == null ? "-" : k.name)}")));
        }
        if (Game.Alive(ui)) lines.Add($"  new stash window: {ui.IsNewStash}");
        foreach (var c in UnityEngine.Resources.FindObjectsOfTypeAll<StashTabbedUIControls>())
        {
            var icons = c.tabIcons;
            var colours = c.tabColors;
            lines.Add($"  legacy controls {c.gameObject.name} active {c.gameObject.activeInHierarchy}: icons {icons?.Count ?? -1}, colours {colours?.Count ?? -1}");
            if (colours != null)
                lines.Add("      colours " + string.Join(" ", colours.ToArray().Select((k, n) => $"{n}:#{UnityEngine.ColorUtility.ToHtmlStringRGB(k)}")));
            if (icons != null)
                lines.Add("      icons " + string.Join(" ", icons.ToArray().Select((k, n) => $"{n}:{(k == null ? "-" : k.name)}")));
        }
        return new CommandResult(DevOutput.Reply("stash-ui", lines));
    }

    private static CommandResult Trace(CommandCall call)
    {
        switch (call.Lower(2))
        {
            case "on": StashTraceFeature.On = true; break;
            case "off": StashTraceFeature.On = false; break;
            case "": break;
            default: return CommandResult.Error($"stash: usage {Usage}");
        }
        bool running = FeatureHost.All.Any(f => f.Id == "stashtrace" && f.State == FeatureState.Running);
        return CommandResult.Ok($"stash trace: {(StashTraceFeature.On ? "on" : "off")}{(running ? string.Empty : " (trace feature not running)")}");
    }
}
