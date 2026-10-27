using System.Globalization;
using System.Text.Json.Nodes;
using EpochAssist.Core;

namespace EpochAssist.Dev;

// cond: lists, edits and checks condition lists by name (contracts/dev-commands.md). Edits go through the setting, so
// they land in the profile in use and are saved like menu edits.
internal static class ConditionCommands
{
    public const string CondUsage = "cond kinds | list [<list>] | add <list> <kind> [arg=value ...] [off] [unknown=true|false] [label=...] "
        + "| set <list> <n> arg=value ... | remove <list> <n> | move <list> <from> <to> | clear <list> | doc <name> [<json>] | toggle <chord> | tallyfail on|off | bench [n]";

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    public const string WhyUsage = "why [<slot 1-5>] | why macros | why macro <name>";

    // why [slot]: every slot's verdict with each condition's value and result, from the slot macros.
    public static CommandResult Why(CommandCall call)
    {
        if (call.Lower(1) is "macros" or "macro")
        {
            if (FeatureHost.Find("macros") is not { State: FeatureState.Running }) return CommandResult.Error("why: macros aren't running");
            if (call.Lower(1) == "macro" && call.Args.Length < 2) return CommandResult.Error($"why: usage {WhyUsage}");
            return new CommandResult(DevOutput.Reply("why", Features.Macros.Explain(call.Lower(1) == "macro" ? call.Rest(2) : null)));
        }
        int? slot = null;
        if (call.Args.Length >= 1)
        {
            if (!int.TryParse(call.Lower(1), out int n) || n is < 1 or > Features.SlotMacros.Slots) return CommandResult.Error($"why: usage {WhyUsage}");
            slot = n - 1;
        }
        if (FeatureHost.Find("macros") is not { State: FeatureState.Running }) return CommandResult.Error("why: macros aren't running");
        return new CommandResult(DevOutput.Reply("why", Features.SlotMacros.Explain(slot)));
    }

    public static CommandResult Cond(CommandCall call) => call.Lower(1) switch
    {
        "kinds" => Kinds(),
        "list" => List(call),
        "add" => Add(call),
        "set" => Set(call),
        "remove" => Remove(call),
        "move" => Move(call),
        "clear" => Clear(call),
        "doc" => Doc(call),
        "toggle" => Toggle(call),
        "bench" => Bench(call),
        "tallyfail" when call.Lower(2) is "on" or "off" => TallyFail(call.Lower(2) == "on"),
        _ => CommandResult.Error($"cond: usage {CondUsage}"),
    };

    private static CommandResult Kinds()
    {
        var lines = new List<string> { $"ok cond kinds: {ConditionKinds.All.Count} kinds" };
        foreach (var kind in ConditionKinds.All)
        {
            string args = string.Join(", ", kind.Args.Select(ArgText));
            lines.Add($"{kind.Id,-16} {kind.Subject,-8} {args}{(kind.TrueWhenUnknown ? "  (true when unresolvable)" : string.Empty)}");
        }
        return new CommandResult(DevOutput.Reply("cond", lines));
    }

    private static string ArgText(ArgSpec arg) => arg.Kind switch
    {
        ArgKind.Number => $"{arg.Name} {Num(arg.Min)}–{Num(arg.Max)} step {Num(arg.Step)}{arg.Unit} ({arg.Default?.ToJsonString()})",
        ArgKind.Slot => $"{arg.Name} slot 0–5, 0 = this slot ({arg.Default?.ToJsonString()})",
        ArgKind.Choice or ArgKind.Rarity => $"{arg.Name} {string.Join("|", arg.Options)} ({arg.Default?.ToJsonString()})",
        ArgKind.Buff => $"{arg.Name} buff id (none)",
        _ => $"{arg.Name} key chord (not set)",
    };

    private static string Num(float value) => value.ToString("0.##", s_inv);

    // cond list [<list>]: every registered list, or one, each entry with its index, words, readout and stored JSON.
    private static CommandResult List(CommandCall call)
    {
        var lines = new List<string>();
        if (call.Args.Length >= 2)
        {
            if (!ConditionTargets.TryResolve(call.Args[1], out var target, out string? key)) return CommandResult.Error($"cond list: no list '{call.Args[1]}'");
            lines.Add(string.Empty);
            AddList(lines, target, call.Args[1], key);
            lines[0] = $"ok cond list {call.Args[1]} ({key}): {target.Setting.List(key).Entries.Count} entries";
            return new CommandResult(DevOutput.Reply("cond", lines));
        }
        lines.Add($"ok cond list: {ConditionTargets.All.Count} settings");
        foreach (var target in ConditionTargets.All)
        {
            foreach (string name in target.Names())
            {
                if (target.KeyFor(name) is not { } key) continue;
                lines.Add($"{name} ({key}, {target.DocName}): {target.Setting.List(key).Entries.Count} entries");
                AddList(lines, target, name, key, header: false);
            }
        }
        return new CommandResult(DevOutput.Reply("cond", lines));
    }

    private static void AddList(List<string> lines, ConditionTargets.Target target, string name, string key, bool header = true)
    {
        var list = target.Setting.List(key);
        if (list.Invalid != null)
        {
            lines.Add($"  list invalid: {list.Invalid}; stored {list.Raw?.ToJsonString()}");
            return;
        }
        var ctx = target.ContextFor(name);
        var results = new ConditionResult[Math.Max(1, list.Entries.Count)];
        var verdict = Conditions.Check(list, ctx, results);
        for (int i = 0; i < list.Entries.Count; i++)
        {
            var entry = list.Entries[i];
            string readout = entry.On ? $"{Conditions.ValueText(entry, results[i])} {Conditions.StateWord(results[i].State)}" : "off";
            string flags = $"{(entry.On ? "on" : "off")}{(entry.TrueWhenUnknown ? " unknown=true" : string.Empty)}{(target.Setting.IsKept(key, entry) ? " kept" : string.Empty)}";
            lines.Add($"  {i + 1} [{flags}] {entry.Words}   {readout}   {JsonOf(entry)}");
        }
        lines.Add($"  verdict {(verdict.Passed ? "pass" : $"fail (first failing {verdict.FirstFailing + 1})")}");
    }

    private static string JsonOf(ConditionEntry entry)
    {
        var doc = new ConditionDoc();
        var list = new ConditionList();
        list.Entries.Add(entry);
        doc.Set("x", list);
        return doc.ToJson()["lists"]!["x"]!["all"]![0]!.ToJsonString();
    }

    // cond add <list> <kind> [arg=value ...] [off] [unknown=true|false] [label=...]
    private static CommandResult Add(CommandCall call)
    {
        if (call.Args.Length < 3) return CommandResult.Error($"cond add: usage {CondUsage}");
        if (!ConditionTargets.TryResolve(call.Args[1], out var target, out string? key)) return CommandResult.Error($"cond add: no list '{call.Args[1]}'");
        // Ids are camel case ("skill.sinceCast"); typed in any case.
        if ((ConditionKinds.Find(call.Args[2]) ?? ConditionKinds.All.FirstOrDefault(k => k.Id.Equals(call.Args[2], StringComparison.OrdinalIgnoreCase))) is not { } kind) return CommandResult.Error($"cond add: unknown kind '{call.Args[2]}' (cond kinds lists them)");
        if (target.Setting.List(key).Full) return CommandResult.Error($"cond add: the list is full ({ConditionList.Limit} conditions at most)");

        var entry = ConditionEntry.New(kind);
        if (ApplyPairs(entry, call.Args.Skip(3), allowOff: true) is { } error) return CommandResult.Error($"cond add: {error}");
        return Edited("add", target, call.Args[1], key, list =>
        {
            list.Entries.Add(entry);
            return list;
        });
    }

    // cond set <list> <n> arg=value ... | on=true|false | unknown=true|false | label=...
    private static CommandResult Set(CommandCall call)
    {
        if (call.Args.Length < 4 || !int.TryParse(call.Lower(3), out int n)) return CommandResult.Error($"cond set: usage {CondUsage}");
        if (!ConditionTargets.TryResolve(call.Args[1], out var target, out string? key)) return CommandResult.Error($"cond set: no list '{call.Args[1]}'");
        string? error = null;
        var result = Edited("set", target, call.Args[1], key, list =>
        {
            if (n < 1 || n > list.Entries.Count) error = $"no entry {n}";
            else error = ApplyPairs(list.Entries[n - 1], call.Args.Skip(3), allowOff: false);
            return list;
        }, () => error);
        return result;
    }

    private static CommandResult Remove(CommandCall call)
    {
        if (call.Args.Length < 3 || !int.TryParse(call.Lower(3), out int n)) return CommandResult.Error($"cond remove: usage {CondUsage}");
        if (!ConditionTargets.TryResolve(call.Args[1], out var target, out string? key)) return CommandResult.Error($"cond remove: no list '{call.Args[1]}'");
        string? error = null;
        return Edited("remove", target, call.Args[1], key, list =>
        {
            if (n < 1 || n > list.Entries.Count) error = $"no entry {n}";
            else if (target.Setting.IsKept(key, list.Entries[n - 1])) error = $"entry {n} can be switched off, not removed";
            else list.Entries.RemoveAt(n - 1);
            return list;
        }, () => error);
    }

    private static CommandResult Move(CommandCall call)
    {
        if (call.Args.Length < 4 || !int.TryParse(call.Lower(3), out int from) || !int.TryParse(call.Lower(4), out int to))
            return CommandResult.Error($"cond move: usage {CondUsage}");
        if (!ConditionTargets.TryResolve(call.Args[1], out var target, out string? key)) return CommandResult.Error($"cond move: no list '{call.Args[1]}'");
        string? error = null;
        return Edited("move", target, call.Args[1], key, list =>
        {
            if (from < 1 || from > list.Entries.Count || to < 1 || to > list.Entries.Count) error = "index out of range";
            else
            {
                var entry = list.Entries[from - 1];
                list.Entries.RemoveAt(from - 1);
                list.Entries.Insert(to - 1, entry);
            }
            return list;
        }, () => error);
    }

    // Kept entries stay (switched as they were).
    private static CommandResult Clear(CommandCall call)
    {
        if (call.Args.Length < 2) return CommandResult.Error($"cond clear: usage {CondUsage}");
        if (!ConditionTargets.TryResolve(call.Args[1], out var target, out string? key)) return CommandResult.Error($"cond clear: no list '{call.Args[1]}'");
        return Edited("clear", target, call.Args[1], key, list =>
        {
            list.Entries.RemoveAll(e => !target.Setting.IsKept(key, e));
            return list;
        });
    }

    private static CommandResult Edited(string verb, ConditionTargets.Target target, string name, string key, Func<ConditionList, ConditionList> edit, Func<string?>? error = null)
    {
        string before = target.Setting.Value;
        if (!target.Setting.Edit(key, edit, out string reason)) return CommandResult.Error($"cond {verb}: {reason}");
        if (error?.Invoke() is { } problem)
        {
            target.Setting.Value = before;
            return CommandResult.Error($"cond {verb}: {problem}");
        }
        string where = Profiles.InProfile && !target.Setting.Section.Group.Shared ? $" (profile '{Profiles.Active.Name}')" : string.Empty;
        return CommandResult.Ok($"{name}: {target.Setting.List(key).Entries.Count} entries{where}");
    }

    // arg=value pairs, plus on=, unknown=, label= and a bare "off". Numbers are stored as numbers, the rest as text.
    private static string? ApplyPairs(ConditionEntry entry, IEnumerable<string> pairs, bool allowOff)
    {
        foreach (string pair in pairs)
        {
            if (allowOff && pair.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                entry.On = false;
                continue;
            }
            int eq = pair.IndexOf('=');
            if (eq <= 0) return $"'{pair}' isn't name=value";
            string name = pair[..eq], value = pair[(eq + 1)..];
            switch (name.ToLowerInvariant())
            {
                case "on" when bool.TryParse(value, out bool on):
                    entry.On = on;
                    break;
                case "unknown" when bool.TryParse(value, out bool unknown):
                    entry.TrueWhenUnknown = unknown;
                    break;
                case "label":
                    entry.Label = value.Length > 0 ? value.Replace('_', ' ') : null;
                    break;
                case "on" or "unknown":
                    return $"{name} takes true or false";
                default:
                    entry.Args[name] = double.TryParse(value, NumberStyles.Float, s_inv, out double number) && !value.Contains('+')
                        ? JsonValue.Create(number) : JsonValue.Create(value);
                    break;
            }
        }
        return null;
    }

    // cond doc <name> [<json>]: a setting's whole document, or replaces it (for tests of bad input).
    private static CommandResult Doc(CommandCall call)
    {
        if (call.Args.Length < 2) return CommandResult.Error($"cond doc: usage {CondUsage}");
        if (ConditionTargets.ByDocName(call.Args[1]) is not { } target)
            return CommandResult.Error($"cond doc: no setting '{call.Args[1]}' ({string.Join(", ", ConditionTargets.All.Select(t => t.DocName))})");
        if (call.Args.Length >= 3)
        {
            if (!target.Setting.SetText(call.Rest(3), out string reason)) return CommandResult.Error($"cond doc: {reason}");
        }
        return CommandResult.Ok($"doc {target.DocName}: {target.Setting.Value}");
    }

    // cond bench [n]: times n passes of every registered list (the shared conditions and five slot lists), each as a
    // frame's first check (model reads not yet cached), and prints the mean per pass.
    private static CommandResult Bench(CommandCall call)
    {
        int n = int.TryParse(call.Lower(2), out int count) && count > 0 ? Math.Min(count, 10000) : 200;
        var lists = new List<(ConditionList List, ConditionContext Ctx)>();
        foreach (var target in ConditionTargets.All)
        {
            foreach (string name in target.Names())
            {
                if (target.KeyFor(name) is { } key) lists.Add((target.Setting.List(key), target.ContextFor(name)));
            }
        }
        int entries = lists.Sum(l => l.List.Entries.Count(e => e.On));
        var results = new ConditionResult[64];
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++)
        {
            ConditionKinds.ResetFrame();
            foreach (var (list, ctx) in lists) Conditions.Check(list, ctx, results);
        }
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / n;
        return CommandResult.Ok($"bench: {lists.Count} lists, {entries} entries on, {ms.ToString("0.0000", s_inv)} ms per pass ({n} passes)");
    }

    // Makes every enemy count throw, as a broken world read would.
    private static CommandResult TallyFail(bool on)
    {
        WorldTally.SimulateFailure = on;
        return CommandResult.Ok($"tallyfail {(on ? "on: enemy counts fail" : "off")}");
    }

    private static CommandResult Toggle(CommandCall call)
    {
        if (call.Args.Length < 2 || !Chord.TryParse(call.Args[1], HotkeyKind.Hold, out var chord) || chord.IsEmpty)
            return CommandResult.Error("cond toggle: usage cond toggle <chord> (e.g. Mouse4)");
        ConditionToggles.Flip(chord.Text);
        return CommandResult.Ok($"toggle {chord.Text} {(ConditionToggles.IsOn(chord.Text) ? "on" : "off")}");
    }
}
