using System.Globalization;
using EpochAssist.Core;

namespace EpochAssist.Dev;

// The dispatch command (counts, reset, log, held) for comparing sends across builds; registered by the dev bridge
// feature.
internal static class MacroCommands
{
    public const string DispatchUsage = "dispatch counts|reset|log on|off|held|drink";

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    public const string MacroUsage = "macro list | add <share code> | code <set id> | on|off <set id> | fire <name> | cancel | clear <set id|all> | bench <n|off> | casts | ailments | hovered | xp";

    public static void Register(string owner)
    {
        Commands.Register(new Command("macro", MacroUsage, "Lists the macro library or removes sets; shows the cast signal, enemy ailments, the hovered enemy and the XP tracker.", CommandMark.Dev, owner, Macro));
        Commands.Register(new Command("dispatch", DispatchUsage,
            "Shows the dispatcher's presses per slot and client, clears them, logs every request outcome, lists held slots, or drinks one potion through a dev client.",
            CommandMark.Dev, owner, Dispatch));
    }

    private static DispatchClient? s_drinker;
    private static int s_drinkFrames;

    /// <summary>Stops the outcome log and drops the dev drink client (the dev bridge stopping or reloading).</summary>
    public static void Reset()
    {
        Dispatcher.Trace = null;
        if (s_drinker != null) Dispatcher.Unregister(s_drinker);
        s_drinker = null;
    }

    private static CommandResult Dispatch(CommandCall call)
    {
        switch (call.Lower(1))
        {
            case "counts":
                return new CommandResult(DevOutput.Reply("dispatch", CountLines()));
            case "reset":
                Dispatcher.ResetPressCounts();
                return CommandResult.Ok($"dispatch reset: t={Clock.Now.ToString("F2", s_inv)}");
            case "log" when call.Lower(2) is "on" or "off":
                Dispatcher.Trace = call.Lower(2) == "on" ? line => Log.Info($"dispatch: {line}") : null;
                return CommandResult.Ok($"dispatch log {call.Lower(2)}");
            case "held":
                return new CommandResult(DevOutput.Reply("dispatch", HeldLines()));
            case "drink":
                return Drink();
            default:
                return CommandResult.Error($"usage: {DispatchUsage}");
        }
    }

    // The nearest enemies' ailments: the visuals' counters (both modes) beside the actor's stacks (offline).
    private static List<string> AilmentLines()
    {
        var lines = new List<string>();
        var near = World.Entries.Where(e => e.Kind == WorldKind.Enemy && !e.Gone && !e.Dead && e.Active).OrderBy(e => e.Distance).Take(8);
        foreach (var entry in near)
        {
            var shown = new List<int>();
            var list = Game.Alive(entry.Visuals) ? entry.Visuals!.ailmentVFXCreator?.ailmentVisuals : null;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                if (list[i] is { } counter) shown.Add((int)counter.getAilmentID());
            }
            string visuals = list == null ? "no visuals" : string.Join(", ", shown.Select(id => $"{id}x{World.AilmentStacks(entry, id)?.ToString(s_inv) ?? "?"}"));
            var stacks = entry.Actor?.ailmentReceiver?.ailmentStacks;
            string actor = stacks == null ? "no actor" : string.Join(", ", shown.Select(id => $"{id}={(stacks.ContainsKey((Il2Cpp.AilmentID)(byte)id) ? stacks[(Il2Cpp.AilmentID)(byte)id] : 0)}"));
            string mods = string.Join(", ", entry.Mods.Select(m => $"{m.Key}:{m.Name}"));
            string health = Game.Alive(entry.Sync) ? $" sync ratio {entry.Sync!.healthRatio} percent {entry.Sync.getHealthPercent():0.###} ratio() {entry.Sync.getHealthRatio()}" : string.Empty;
            lines.Add($"{entry.Distance.ToString("F1", s_inv)} m {entry.Name}: visuals [{visuals}] actor [{actor}]{health}{(mods.Length > 0 ? $" mods [{mods}]" : string.Empty)}");
        }
        lines.Insert(0, $"ok macro ailments: {lines.Count} enemies, mode {(PlayerModel.Online is bool o ? o ? "online" : "offline" : "?")}");
        return lines;
    }

    private static CommandResult Macro(CommandCall call)
    {
        switch (call.Lower(1))
        {
            case "hovered":
                return CommandResult.Ok(Features.AutoCast.HoveredEnemy() is { } h
                    ? $"macro hovered: {h.Name} {h.Distance.ToString("F1", s_inv)} m, rarity {h.Rarity}, cursor {(Features.AutoCast.CursorPoint() is { } c ? c.ToString() : "none")}"
                    : $"macro hovered: none, cursor {(Features.AutoCast.CursorPoint() is { } p ? p.ToString() : "none")}");
            case "casts":
                var casts = new List<string> { $"ok macro casts: signal {PlayerModel.CastSignal}, class {PlayerModel.ClassName ?? "?"} ({PlayerModel.ClassId?.ToString(s_inv) ?? "?"})" };
                for (int i = 1; i <= Dispatcher.SlotCount + 1; i++)
                {
                    float at = PlayerModel.LastCastAt(i);
                    casts.Add($"slot {i} {PlayerModel.Slot(i)?.AbilityId ?? "-"}: {(float.IsNegativeInfinity(at) ? "never" : (Clock.Now - at).ToString("F1", s_inv) + " s ago")}");
                }
                return new CommandResult(DevOutput.Reply("macro", casts));
            case "xp":
                var tracker = Player.Object?.GetComponent<Il2Cpp.ExperienceTracker>();
                if (!Game.Alive(tracker)) return CommandResult.Error("macro xp: no experience tracker");
                var cumulative = tracker!.GetCumulativeXP();
                return CommandResult.Ok($"macro xp: level {tracker.CurrentLevel}, current {tracker.CurrentExperience}, next {tracker.NextLevelExperience}, cumulative {cumulative.Item1} / {cumulative.Item2}");
            case "ailments":
                return new CommandResult(DevOutput.Reply("macro", AilmentLines()));
            case "list":
                return new CommandResult(DevOutput.Reply("macro", ListLines()));
            case "add" when call.Args.Length >= 2:
                return CommandResult.Ok("macro add: " + Features.Macros.Import(call.Rest(2)));
            case "code" when call.Args.Length >= 2:
                if (Features.AutoCastSettings.LibraryDoc.Doc.Find(call.Args[1]).Set is not { } shared) return CommandResult.Error($"macro code: no set {call.Args[1]}");
                var (code, error) = ShareCode.Encode(shared);
                return code != null ? CommandResult.Ok($"macro code {call.Args[1]} ({code.Length} chars, {(ShareCode.Compressed == true ? "deflate" : "uncompressed")}): {code}")
                    : CommandResult.Error($"macro code: {error}");
            case "fire" when call.Args.Length >= 2:
                return new CommandResult(Features.Macros.DevFire(call.Rest(2)));
            case "bench" when call.Args.Length >= 2:
                return new CommandResult(Features.Macros.Bench(call.Lower(2) == "off" ? 0 : int.TryParse(call.Lower(2), out int n) ? Math.Clamp(n, 0, 200) : 0));
            case "on" or "off" when call.Args.Length >= 2:
                bool on = call.Lower(1) == "on";
                string id = call.Args[1];
                bool found = false;
                Features.Macros.EditLibrary(doc =>
                {
                    if (doc.Find(id).Set is { } set)
                    {
                        set.On = on;
                        found = true;
                    }
                });
                return found ? CommandResult.Ok($"macro {call.Lower(1)} {id}") : CommandResult.Error($"macro {call.Lower(1)}: no set {id}");
            case "edit" when call.Args.Length >= 2:
                Features.Macros.DevEdit(call.Args[1]);
                return CommandResult.Ok($"macro edit {call.Args[1]}");
            case "cancel":
                string? running = Features.Macros.RunningName;
                Features.Macros.Cancel("dev");
                return CommandResult.Ok(running != null ? $"macro cancel: {running} cancelled (dev)" : "macro cancel: nothing running");
            case "clear" when call.Args.Length >= 2:
                string target = call.Args[1];
                int removed = 0;
                bool done = Features.Macros.EditLibrary(doc => removed = doc.Sets.RemoveAll(set => target == "all" || set.Id == target));
                return done ? CommandResult.Ok($"macro clear {target}: {removed} set{(removed == 1 ? string.Empty : "s")} removed") : CommandResult.Error("macro clear: the library can't be changed");
            default:
                return CommandResult.Error($"usage: {MacroUsage}");
        }
    }

    // Every set and macro: ids, class, on, state.
    private static List<string> ListLines()
    {
        var doc = Features.AutoCastSettings.LibraryDoc.Doc;
        var lines = new List<string>
        {
            $"ok macro list: v{doc.Version}{(doc.ReadOnly ? " (newer, read-only)" : string.Empty)}, {doc.Sets.Count} sets, class shown {Features.Macros.PickedClass}, character {PlayerModel.ClassName ?? "?"} ({PlayerModel.ClassId?.ToString(s_inv) ?? "?"})",
        };
        foreach (var set in doc.Sets)
        {
            lines.Add($"set {set.Id} {Ui.RowText.Quote(set.Name)} class {set.Class} {(set.On ? "on" : "off")}{(set.Invalid is { } bad ? $" invalid ({bad})" : string.Empty)}");
            foreach (var macro in set.Macros)
            {
                var (state, _) = Features.Macros.MacroState(set, macro);
                lines.Add($"  macro {macro.Id} {Ui.RowText.Quote(macro.Name)} {(macro.On ? "on" : "off")} trigger {macro.Trigger.Kind}{(macro.Trigger.Key.Length > 0 ? " " + macro.Trigger.Key : string.Empty)} "
                    + $"move {macro.Move} wait {ArgSpec.Format(macro.WaitMs)} ms, {macro.All.NodeCount} lines, {macro.Actions.Count} steps: {state}");
            }
        }
        return lines;
    }

    private static List<string> CountLines()
    {
        var lines = new List<string>
        {
            $"ok dispatch counts: total {Dispatcher.PressesSinceReset} since reset, last 1s {Dispatcher.PressesLastSecond}, "
                + $"peak {Dispatcher.PeakPressesPerSecond}/s, cap {Dispatcher.MaxPressesPerSecond}/s, t={Clock.Now.ToString("F2", s_inv)}",
        };
        for (int i = 0; i < Dispatcher.SlotCount; i++) lines.Add("slot " + SlotText(i));
        foreach (var client in Dispatcher.Clients) lines.Add(ClientText(client));
        return lines;
    }

    // "autocast (skills): sent 1: 97, 3: 1580; refused spacing 4021, frame taken 12; drinks 0"
    private static string ClientText(DispatchClient client)
    {
        var sent = Enumerable.Range(0, Dispatcher.SlotCount).Where(i => client.Sent[i] > 0).Select(i => $"{i + 1}: {client.Sent[i]}");
        var refused = Enum.GetValues<SendOutcome>().Where(o => o != SendOutcome.Sent && client.Refused[(int)o] > 0)
            .Select(o => $"{Dispatcher.OutcomeWord(client, o).Split(' ')[0]} {client.Refused[(int)o]}");
        return $"{client.Name} ({client.Priority.ToString().ToLowerInvariant()}): sent {Joined(sent)}; refused {Joined(refused)}; drinks {client.DrinksSent}";
    }

    private static string Joined(IEnumerable<string> parts)
    {
        string text = string.Join(", ", parts);
        return text.Length == 0 ? "none" : text;
    }

    // dispatch drink: one drink through a dev survival client (registered for this, dropped when it answers), with
    // no gap of its own; the reply is its outcome.
    private static CommandResult Drink()
    {
        if (FeatureHost.Find("dev") is not { } dev) return CommandResult.Error("dispatch drink: no dev feature");
        if (s_drinker == null)
        {
            s_drinker = Dispatcher.Register(new DispatchClient(dev, PriorityClass.Survival, s_alwaysOn, Array.Empty<PauseRule>()) { Name = "dev drink" });
        }
        float before = Dispatcher.LastDrinkAt;
        s_drinkFrames = 0;
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                var client = s_drinker;
                if (client == null) return "error dispatch drink: client dropped";
                if (s_drinkFrames++ == 0)
                {
                    Dispatcher.RequestDrink(client);
                    return null;
                }
                string outcome = Dispatcher.OutcomeWord(client, client.DrinkOutcome);
                Dispatcher.Unregister(client);
                s_drinker = null;
                var potions = PlayerModel.Potions;
                return $"ok dispatch drink: {outcome}, mode {(PlayerModel.Online is bool online ? online ? "online" : "offline" : "unknown")}, "
                    + $"can drink {Dispatcher.CanDrink}, last drink {(float.IsNegativeInfinity(before) ? "never" : (Clock.Now - before).ToString("F2", s_inv) + " s ago")} before, "
                    + $"potions {potions?.Charges?.ToString(s_inv) ?? "?"}/{potions?.MaxCharges?.ToString(s_inv) ?? "?"} after";
            },
        };
    }

    private static readonly Toggle s_alwaysOn = new(new Section(new SettingsGroup("devdrink", "Dev drink"), "drink", "Dev drink"), "on", "On", true);

    // "2: 41 presses, gap mean 1.42 s min 0.61 s, last 12.30 s ago" since the last reset.
    private static string SlotText(int i)
    {
        int n = Dispatcher.SlotPresses[i];
        string text = $"{i + 1}: {n} presses";
        if (n >= 2)
        {
            text += $", gap mean {(Dispatcher.SlotGapSum[i] / (n - 1)).ToString("F2", s_inv)} s min {Dispatcher.SlotMinGap[i].ToString("F2", s_inv)} s";
        }
        if (n > 0) text += $", last {(Clock.Now - Dispatcher.LastPressAt[i]).ToString("F2", s_inv)} s ago";
        return text;
    }

    private static List<string> HeldLines()
    {
        var lines = new List<string> { $"ok dispatch held: {Dispatcher.Clients.Count} clients, t={Clock.Now.ToString("F2", s_inv)}" };
        foreach (var client in Dispatcher.Clients)
        {
            var held = new List<string>();
            for (int i = 0; i < Dispatcher.SlotCount; i++)
            {
                if (!client.Holds(i)) continue;
                bool channelled = client.Channelled?.Invoke(i) ?? false;
                held.Add(channelled ? $"{i + 1} (channel)" : (i + 1).ToString(s_inv));
            }
            string blocking = client.Blocking is { } rule ? $", blocked by {rule.Name}" : string.Empty;
            string outcomes = string.Join(" ", client.Outcome.Select((o, i) => o == SendOutcome.None ? null : $"{i + 1}:{Dispatcher.OutcomeWord(client, o)}").Where(t => t != null));
            lines.Add($"{client.Name} ({client.Priority.ToString().ToLowerInvariant()}): held {(held.Count == 0 ? "none" : string.Join(", ", held))}{blocking}"
                + (outcomes.Length > 0 ? $", last {outcomes}" : string.Empty)
                + (client.DrinkOutcome != SendOutcome.None ? $", drink {Dispatcher.OutcomeWord(client, client.DrinkOutcome)}" : string.Empty));
        }
        lines.Add($"last drink {(float.IsNegativeInfinity(Dispatcher.LastDrinkAt) ? "never" : (Clock.Now - Dispatcher.LastDrinkAt).ToString("F2", s_inv) + " s ago")}{(Dispatcher.DrinkLanding ? ", landing" : string.Empty)}");
        return lines;
    }
}
