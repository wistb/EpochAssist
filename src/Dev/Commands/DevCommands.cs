using System.Text.RegularExpressions;
using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Dev;

// The dev commands, registered by the dev bridge feature; the registry lists and runs them only under the dev loader.
internal static partial class DevCommands
{
    private const float ShotTimeoutSeconds = 5f;
    private const int SettleFrames = 2;

    private static bool s_drawTest, s_drawMinimap, s_drawArrows, s_drawBars, s_drawRoles;
    private static readonly List<WorldEntry> s_drawList = new();

    private static string ShotDir => Path.Combine(DevBridge.Dir, "shots");

    public static void Register(string owner)
    {
        void Add(string name, string usage, string help, Func<CommandCall, CommandResult> run) =>
            Commands.Register(new Command(name, usage, help, CommandMark.Dev, owner, run));

        Add("hud", "hud panel open|close", "Opens or closes the mini HUD's quick panel.", HudCommand);
        Add("probe", "probe <type> [filter]", "Writes a game type's members to probe.txt.", call => Plain(Probe(Arg(call, 1), call.Args.Length > 1 ? call.Args[1] : null)));
        Add("patchcheck", "patchcheck <game version>", "Checks every patch target's first bytes in GameAssembly.dll and saves patch-check.json for the release.", call =>
            Regex.IsMatch(Arg(call, 1), @"^[0-9]+(\.[0-9]+){1,3}$") ? PatchCheck.Run(Arg(call, 1))
                : CommandResult.Error("usage: patchcheck <game version, as the game shows it, e.g. 1.5.3>"));
        Add("instances", "instances <type> [count|all]", "Lists a type's live objects in the loaded scenes, nearest the player first.", Instances);
        Add("inspect", "inspect <target> [member.path]", "Shows a live object's members and values; target is player, <type>, <type>#n, hovered or hovered:item.", call =>
            Plain(Inspect.Run(Arg(call, 1), Arg(call, 2))));
        Add("statics", "statics <type>", "Shows a game type's static members and their values.", call => Plain(Inspect.Statics(Arg(call, 1))));
        Add("watch", "watch <target> <member.path> [seconds]|stop", "Samples one value 10 times a second into watch.txt (10 s when not given, up to 120).", call => Plain(Watch.Command(call)));
        Add("hovered", "hovered", "Names the game UI under the cursor and the item behind an open tooltip.", _ => Plain(Hovered.Run()));
        Add("rva", "rva <type> <method>", "Shows where each overload of a game method's code is in GameAssembly.dll, for a native decompile.", call =>
            Plain(PatchCheck.Rva(Arg(call, 1), Arg(call, 2))));
        Add("find", "find <text>", "Writes every game method whose name has the text to find.txt.", call => Plain(Find(Arg(call, 1))));
        Add("facts", "facts", "Reads the game values behind typing, pause, loading, arrival and death.", _ => CommandResult.Ok("facts: " + Facts()));
        Add("keys", "keys [frames]", "Lists the keys and buttons Unity reports down or newly down, frame by frame (10 frames when not given).", KeysCommand);
        Add("move", "move <x> <y> [true|false]", "Feeds the game a keyboard movement direction, as a held key would.", Move);
        Add("stopmove", "stopmove", "Stops the player's movement.", call =>
        {
            var mps = Game.Input?.movementProcessor;
            if (Game.Alive(mps)) mps!.StopActiveMovement();
            return Game.Alive(mps) ? CommandResult.Ok(call.Line) : CommandResult.Error($"{call.Line}: no movement processor");
        });
        Add("travel", "travel [scene]", "Travels to an unlocked waypoint, or lists them.", call => Plain(Travel(Arg(call, 1))));
        Add("fonts", "fonts", "Lists the fonts the game has loaded.", _ =>
            CommandResult.Ok($"fonts: {string.Join(", ", Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Font>()).Select(f => f.name).Distinct())}"));
        Add("wait", "wait [frames]", "Waits frames (30 when not given) before the next bridge command.", call =>
            CommandResult.Ok(call.Line) with { Settle = int.TryParse(call.Lower(1), out int frames) ? frames : 30 });
        Add("shot", "shot full|menu|hud [name] [matte]", "Saves a screenshot of the screen, the menu or the HUD to shots/; matte gives the menu or HUD a transparent background, trimmed to what is drawn.",
            call => StartCapture(call.Lower(1), call.Args.Length > 1 ? call.Args[1] : call.Lower(1), call.Lower(3) == "matte"));
        Add("draw", "draw test|minimap|arrows|bars|roles on|off", "Draws a test pattern around the player, markers on the minimap, edge arrows for the 10 nearest rares, bars over enemies, or the colour role swatch.", DrawCommand);
        Add("focus", "focus [key]", "Shows the menu control that has keyboard focus, or moves it to a control by its full key.", call =>
        {
            if (call.Args.Length > 0) Focus.TakeFull(call.Rest(1));
            string? focused = Focus.Current;
            return CommandResult.Ok($"focus: {focused ?? "none"} (page {Menu.Current}, region {Focus.Region ?? "none"})");
        });
        // The text keeps its case.
        Add("toast", "toast <text>", "Shows a toast.", call =>
        {
            Toasts.Show("dev", call.Rest(1), ToastKind.Info);
            return CommandResult.Ok("toast");
        });
        Add("toasts", "toasts", "Lists the toasts on screen.", _ =>
        {
            var shown = Toasts.Visible;
            return CommandResult.Ok($"toasts: {shown.Count} shown" + (shown.Count > 0 ? ": " : string.Empty)
                + string.Join(", ", shown.Select(t => $"\"{t.Text}\" ({Time.unscaledTime - t.ShownAt:F1} s)")));
        });
        // All textures in the game too, so textures leaked by an earlier load show up across reloads; and every Harmony
        // patch, ours and other mods', so a batch of read-only commands can be shown to patch nothing.
        Add("resources", "resources", "Counts the mod's textures, every texture in the game and every Harmony-patched method.", _ =>
            CommandResult.Ok($"resources: theme textures {Theme.TextureCount}, draw textures {Draw.ResourceCount}, "
                + $"unity textures {Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Texture2D>()).Length}, "
                + $"harmony patched methods {PatchedMethods()}, edits {Edits.Count}"));
        Add("reload", "reload", "Reloads the mod through the dev loader (after the bridge batch).", Reload);
        Add("diag", "diag", "Shows the diagnostics text, as Copy diagnostics puts it on the clipboard.", _ =>
        {
            string text = Diagnostics.Build().ToText();
            return CommandResult.Ok($"diag: {text.Split('\n').Length} lines\n{text}");
        });
        Add("crash", "crash fault|start|throw <n>|handler <n>", "Makes the crash test feature fault, fail its start, fault n times with different errors, or throw in its next n event handler calls.", Crash);
        Add("reports", "reports", "Lists the saved crash reports, newest first.", _ =>
        {
            var reports = Reports.List();
            return CommandResult.Ok("reports: " + (reports.Count == 0 ? "none" : string.Join(", ", reports.Select(r => $"{r.File} ({r.Feature}, count {r.Count})"))));
        });
        Add("profile", "profile", "Shows the character key's parts and the profile's state, never the name.", _ =>
            CommandResult.Ok($"profile: {Profiles.Describe()}"));
        Add("player", "player", "Shows how the player is found: the finder, the player objects, their components and actors.", _ =>
            CommandResult.Ok($"player: {PlayerFacts()}"));
        Add("cond", ConditionCommands.CondUsage, "Lists, edits and checks condition lists by name.", ConditionCommands.Cond);
        Add("why", ConditionCommands.WhyUsage, "Shows why each autocast slot casts or waits: its conditions' values and results.", ConditionCommands.Why);
        Add("model", ModelCommands.Usage, "Shows the player and world models, looks up game data, or simulates a missing model part.", ModelCommands.Run);
        Add("edits", ClientCommands.EditsUsage, "Applies, lists and removes test game edits (owned by the crash test feature), or writes a value past the registry.", ClientCommands.EditsCommand);
        Add("look", ClientCommands.LookUsage, "Outlines or tints the nearest enemies, recolours effects near the player, or changes an outline group's colour.", ClientCommands.LookCommand);
        Add("tooltip", ClientCommands.TooltipUsage, "Adds a test line to every item tooltip.", ClientCommands.TooltipCommand);
        Add("gameui", ClientCommands.GameUiUsage, "Hides or shows the game's own UI, as an edit.", ClientCommands.GameUiCommand);
        Add("ui", ClientCommands.UiUsage, "Presses a game prompt, respawn or quick move button through the dispatcher (offline only).", ClientCommands.UiCommand);
        Add("events", EventCommands.EventsUsage, "Lists the last events on the bus, or the subscriptions and producers.", EventCommands.Events);
        Add("jobs", EventCommands.JobsUsage, "Lists the scheduler's jobs and the automated presses per second; reset clears the press counts.", EventCommands.Jobs);
        Add("gate", "gate", "Shows autocast's enemy gate: open, enemies and weight in range, and the closest enemy within aim range.", _ => Gate());
        Add("perf", "perf [timers on|off] | waste <feature-id> <ms>|off", "Shows the mod's frame time and what measuring it costs, turns the timers off, or makes a feature waste time each frame.", Perf);
        Commands.AddDevVerb("mods", "simulate", owner, ModsSimulate);
        SessionCommands.Register(owner);
        MacroCommands.Register(owner);
    }

    // mods simulate <name>|off: a pretend loaded mod (kind mod, file <name>.dll) until off or a reload, then the start
    // toast rule again.
    private static CommandResult ModsSimulate(CommandCall call)
    {
        string name = call.Rest(2).Trim();
        if (name.Length == 0) return CommandResult.Error("usage: mods simulate <name>|off");
        if (name.Equals("off", StringComparison.OrdinalIgnoreCase)) ModOverlap.Simulated.Clear();
        else if (!ModOverlap.Simulated.Contains(name, StringComparer.OrdinalIgnoreCase)) ModOverlap.Simulated.Add(name);
        ModOverlap.CheckNow();
        return CommandResult.Ok($"mods simulate: {(ModOverlap.Simulated.Count == 0 ? "none" : string.Join(", ", ModOverlap.Simulated))}") with { Settle = SettleFrames };
    }

    // Harmony keeps listing a method after its last patch is removed, so only methods that still carry one count.
    private static int PatchedMethods() => HarmonyLib.Harmony.GetAllPatchedMethods().Count(m =>
        HarmonyLib.Harmony.GetPatchInfo(m) is { } info && info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count + info.Finalizers.Count > 0);

    public static void Reset()
    {
        s_drawTest = s_drawMinimap = s_drawArrows = s_drawBars = s_drawRoles = false;
        Watch.Stop("bridge stopped");
        EventCommands.StopWatch("bridge stopped");
        Hovered.Reset();
        MacroCommands.Reset();
    }

    // instances <type> [count|all]: count 5 when not given, 1 to 50; all lists every one (into instances.txt past 40).
    private static CommandResult Instances(CommandCall call)
    {
        var lookup = GameTypes.Resolve(Arg(call, 1));
        if (lookup.Type is not { } type) return CommandResult.Error($"instances: {lookup.Error}");
        int count = call.Lower(2) == "all" ? int.MaxValue : int.TryParse(Arg(call, 2), out int n) ? Math.Clamp(n, 1, 50) : 5;
        var (list, error) = GameTypes.Instances(lookup);
        if (list == null) return CommandResult.Error($"instances: {error}");
        if (list.Count == 0) return CommandResult.Ok($"instances {type.FullName}: 0 live");
        var lines = new List<string> { $"ok instances {type.FullName}: {list.Count} live ({list.Count(i => i.Active)} active) in {GameState.ActiveSceneName}" };
        lines.AddRange(list.Take(count).Select(i =>
            $"#{i.Index} {(i.Distance is float d ? d.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "-")} m {(i.Active ? "active" : "inactive")} {i.Path.Value}"));
        return Plain(DevOutput.Reply("instances", lines));
    }

    // The dev loader reloads on its next update; from the bridge, once this batch's results are written.
    private static CommandResult Reload(CommandCall call)
    {
        if (call.Source == "bridge") DevBridge.ReloadAfterBatch = true;
        else Runtime.ReloadRequested = true;
        return CommandResult.Ok("reload requested");
    }

    // crash fault | start | throw <n>: the crash test feature is (re)started as needed so the error reaches the host.
    private static CommandResult Crash(CommandCall call)
    {
        if (FeatureHost.Get<CrashTestFeature>() is not { } feature) return CommandResult.Error("crash: no crash test feature");
        switch (call.Lower(1))
        {
            case "fault":
                if (feature.State != FeatureState.Running) FeatureHost.Restart(feature);
                CrashTestFeature.ArmFault();
                return CommandResult.Ok("crash fault armed: the next 5 updates throw") with { Settle = 10 };
            case "start":
                CrashTestFeature.ArmStart("crash test start");
                FeatureHost.Restart(feature);
                return CommandResult.Ok($"crash start: {feature.State.ToString().ToLowerInvariant()}") with { Settle = SettleFrames };
            case "throw" when int.TryParse(call.Lower(2), out int count) && count > 0:
                // One failed start per frame, each with its own message, so each is a different report.
                int done = 0;
                return new CommandResult(string.Empty)
                {
                    Pending = () =>
                    {
                        CrashTestFeature.ArmStart($"crash test {done + 1} of {count}");
                        FeatureHost.Restart(feature);
                        return ++done < count ? null : $"ok crash throw {count}: {count} faults";
                    },
                };
            case "handler" when int.TryParse(call.Lower(2), out int calls) && calls > 0:
                if (feature.State != FeatureState.Running) FeatureHost.Restart(feature);
                CrashTestFeature.ArmHandler(calls);
                return CommandResult.Ok($"crash handler armed: its next {calls} ZoneChange handler calls throw");
            default:
                return CommandResult.Error("crash: usage crash fault|start|throw <n>|handler <n>");
        }
    }

    // perf: the timers' own cost per frame (calls times the cost of one timestamp pair) as a share of the mod's time.
    private static CommandResult Perf(CommandCall call)
    {
        if (call.Lower(1) == "timers" && call.Lower(2) is "on" or "off")
        {
            Diagnostics.TimersOff = call.Lower(2) == "off";
            return CommandResult.Ok($"perf timers {call.Lower(2)}");
        }
        // perf waste <feature-id> <ms> | off: the host spins that long in the feature's update guard (budget check).
        if (call.Lower(1) == "waste")
        {
            if (call.Lower(2) == "off")
            {
                FeatureHost.SetWaste(null, 0d);
                return CommandResult.Ok("perf waste off");
            }
            if (FeatureHost.Find(call.Lower(2)) is not { } target) return CommandResult.Error($"perf waste: unknown feature '{call.Lower(2)}'");
            if (!double.TryParse(call.Lower(3), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms) || ms < 0.1 || ms > 20)
                return CommandResult.Error("perf waste: usage perf waste <feature-id> <ms 0.1-20> | perf waste off");
            FeatureHost.SetWaste(target, ms);
            return CommandResult.Ok($"perf waste {target.Id} {ms.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}ms");
        }
        if (call.Args.Length > 0) return CommandResult.Error("perf: usage perf [timers on|off] | perf waste <feature-id> <ms>|off");

        const int pairs = 10000;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < pairs; i++)
        {
            System.Diagnostics.Stopwatch.GetTimestamp();
            System.Diagnostics.Stopwatch.GetTimestamp();
        }
        double costUs = FrameTimes.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000d / pairs;
        double modMs = Diagnostics.ModFrames.AverageMs, profileUs = ProfileCheckMicroseconds();
        int calls = Diagnostics.TimedCallsLastFrame;
        double share = modMs > 0d ? (calls * costUs + profileUs) / 1000d / modMs * 100d : 0d;
        return CommandResult.Ok($"perf: mod avg {modMs:F3} ms, timed calls/frame {calls}, cost/call {costUs:F3} µs, timer share {share:F2}%, "
            + $"profile check {profileUs:F3} µs{(Diagnostics.TimersOff ? " (timers off)" : string.Empty)}");
    }

    // One character key read and compare, as the profiles feature makes once a second (every update while reading).
    private static double ProfileCheckMicroseconds()
    {
        const int reads = 100;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < reads; i++)
        {
            if (Profiles.TryReadKey(out var key, out _)) _ = key.Equals(key with { });
        }
        return FrameTimes.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000d / reads;
    }

    // The argument as typed (type names and scenes keep their case), or empty.
    private static string Arg(CommandCall call, int i) => i <= call.Args.Length ? call.Args[i - 1] : string.Empty;

    // A reply already written with its ok or error.
    private static CommandResult Plain(string text) => new(text);

    private static CommandResult HudCommand(CommandCall call)
    {
        if (call.Lower(1) != "panel") return CommandResult.Error($"unknown command: {call.Line}");
        string panel = Hud.PanelOpen == (call.Lower(2) == "open") ? "ok" : Actions.Run("hud.togglePanel", "dev bridge");
        return new CommandResult(panel.StartsWith("ok") ? $"ok {call.Line}" : panel) { Settle = SettleFrames };
    }

    // gate: autocast's enemy gate (the every-slot list's gate entry) as it decides, for comparing its transitions and aim
    // across builds; the name is the one the nameplate shows.
    private static CommandResult Gate()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string closest = Features.AutoCast.ClosestEnemy is var (name, distance) ? $"\"{name ?? "?"}\" {distance.ToString("F1", inv)}m" : "none";
        var gate = Features.AutoCast.GateNow;
        string counted = gate.Counted ? string.Empty : " (not counted)";
        return CommandResult.Ok($"gate: open {gate.Open}, {(gate.On ? "on" : "off")}, range {gate.Range.ToString("0.##", inv)}, count {gate.Count}, "
            + $"weight {gate.Weight.ToString("0.##", inv)}{counted}, closest {closest}, t {Time.unscaledTime.ToString("F2", inv)}");
    }
}
