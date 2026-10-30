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
        Add("uievents", "uievents [on]", "Shows the game's UI event systems and whether each is on; on switches every one back on.", UiEvents);
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
        Add("wait", "wait [frames|<n>ms]", "Waits frames (30 when not given), or milliseconds as input.ps1's wait, before the next bridge command.", call =>
        {
            if (call.Lower(1).EndsWith("ms", StringComparison.Ordinal) && int.TryParse(call.Lower(1)[..^2], out int ms))
            {
                float until = Time.unscaledTime + ms / 1000f;
                return CommandResult.Ok(call.Line) with { Text = string.Empty, Pending = () => Time.unscaledTime >= until ? "ok " + call.Line : null };
            }
            return CommandResult.Ok(call.Line) with { Settle = int.TryParse(call.Lower(1), out int frames) ? frames : 30 };
        });
        Add("shot", "shot full|menu|hud|ui <target> [name] [matte] [jpg[quality]]", "Saves a screenshot of the screen, the menu, the HUD or one piece of game UI to shots/; matte gives a transparent background, trimmed to what is drawn; jpg saves a JPG.",
            ShotCommand);
        Add("see", "see [game|ours|text|all] [filter]", "Lists what is on screen, numbered, in game-window pixels: game windows, game controls and what covers them, our menu, HUD and menu controls; text adds game text. click #n and shot ... marks use the numbers.",
            SeeCommand);
        Add("draw", "draw test|minimap|arrows|bars|roles on|off", "Draws a test pattern around the player, markers on the minimap, edge arrows for the 10 nearest rares, bars over enemies, or the colour role swatch.", DrawCommand);
        Add("focus", "focus [key]", "Shows the menu control that has keyboard focus, or moves it to a control by its full key.", call =>
        {
            if (call.Args.Length > 0) Focus.TakeFull(call.Rest(1));
            string? focused = Focus.Current;
            return CommandResult.Ok($"focus: {focused ?? "none"} (page {Menu.Current}, region {Focus.Region ?? "none"})");
        });
        // The text keeps its case.
        Add("toast", "toast [sticky] <text>", "Shows a toast; sticky stays until its × is clicked.", call =>
        {
            bool sticky = call.Lower(1) == "sticky";
            Toasts.Show("dev", call.Rest(sticky ? 2 : 1), ToastKind.Info, sticky: sticky);
            return CommandResult.Ok("toast");
        });
        Add("toasts", "toasts", "Lists the toasts on screen.", _ =>
        {
            var shown = Toasts.Visible;
            return CommandResult.Ok($"toasts: {shown.Count} shown" + (shown.Count > 0 ? ": " : string.Empty)
                + string.Join(", ", shown.Select(t => $"\"{t.Text}\" ({Time.unscaledTime - t.ShownAt:F1} s{(t.Sticky ? ", until dismissed" : string.Empty)})")));
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
        Add("cond", ConditionCommands.CondUsage, "Lists, edits and checks condition lists by name.", call =>
            call.Lower(1) == "named" && Modules.NotLoaded("macros") is { } why ? CommandResult.Error($"cond named: {why}") : ConditionCommands.Cond(call));
        Add("why", ConditionCommands.WhyUsage, "Shows why each slot macro casts or waits: its conditions' values and results.", WhenLoaded("macros", "why", ConditionCommands.Why));
        Add("model", ModelCommands.Usage, "Shows the player and world models, looks up game data, or simulates a missing model part.", ModelCommands.Run);
        Add("edits", ClientCommands.EditsUsage, "Applies, lists and removes test game edits (owned by the crash test feature), or writes a value past the registry.", ClientCommands.EditsCommand);
        Add("look", ClientCommands.LookUsage, "Outlines or tints the nearest enemies, recolours effects near the player, or changes an outline group's colour.", ClientCommands.LookCommand);
        Add("tooltip", ClientCommands.TooltipUsage, "Adds a test line to every item tooltip.", ClientCommands.TooltipCommand);
        Add("gameui", ClientCommands.GameUiUsage, "Hides or shows the game's own UI, as an edit.", ClientCommands.GameUiCommand);
        Add("ui", ClientCommands.UiUsage, "Presses a game prompt, respawn or quick move button through the dispatcher (offline only).", ClientCommands.UiCommand);
        Add("events", EventCommands.EventsUsage, "Lists the last events on the bus, or the subscriptions and producers.", EventCommands.Events);
        Add("map", MapCommands.Usage, "Shows the minimap's zone map and reveal values, lists the game's map icons, reads the explored-volume record, or applies one reveal step.", WhenLoaded("mapreveal", "map", MapCommands.Run));
        Add("planner", PlannerCommands.Usage, "Reads the character's trees and the open tree and skill panels, logs when they change, imports and checks the planner fixtures, or pastes a planner link through the import's one request.", WhenLoaded("planner", "planner", PlannerCommands.Run));
        Add("stash", StashCommands.Usage, "Reads the stash's folders and tabs (names, icons, colours, order, rules, items) and the tab prices; reads only.", WhenLoaded("stash", "stash", StashCommands.Run));
        Add("interact", InteractCommands.Usage, "Lists what the game offers to use near the player, logs the game's own click path, or uses the nearest of a kind from in range (offline).", WhenLoaded("interact", "interact", InteractCommands.Run));
        Add("jobs", EventCommands.JobsUsage, "Lists the scheduler's jobs and the automated presses per second; reset clears the press counts.", EventCommands.Jobs);
        Add("modules", ModuleCommands.Usage, "Module loading tests: choices, a reload to apply, a reload as a fresh install or an upgrade, modules unavailable at the next reload, the first-start toast, the dev window section.", ModuleCommands.Run);
        Add("gate", "gate", "Shows the enemy gate: open, enemies and weight in range, and the shared slot target's pick.", WhenLoaded("macros", "gate", _ => Gate()));
        Add("perf", "perf [timers on|off] | waste <feature-id> <ms>|off", "Shows the mod's frame time and what measuring it costs, turns the timers off, or makes a feature waste time each frame.", Perf);
        Commands.AddDevVerb("mods", "simulate", owner, ModsSimulate);
        SessionCommands.Register(owner);
        MacroCommands.Register(owner);
    }

    /// <summary>A module's command: runs while the module is loaded, else answers "error verb: Module is not loaded"
    /// (checked on each call, so a module loaded by the first-start answer works at once).</summary>
    internal static Func<CommandCall, CommandResult> WhenLoaded(string module, string verb, Func<CommandCall, CommandResult> run) =>
        call => Modules.NotLoaded(module) is { } why ? CommandResult.Error($"{verb}: {why}") : run(call);

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
        MapCommands.Reset();
        PlannerCommands.Reset();
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

    // gate: the slot macros' enemy gate (the every-slot list's gate entry) as it decides, and the shared slot target's pick,
    // for comparing transitions and aim across builds; the name is the one the nameplate shows.
    private static CommandResult Gate()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var (words, pick) = Features.SlotMacros.SlotTargetNow;
        var gate = Features.SlotMacros.GateNow;
        string counted = gate.Counted ? string.Empty : " (not counted)";
        return CommandResult.Ok($"gate: open {gate.Open}, {(gate.On ? "on" : "off")}, range {gate.Range.ToString("0.##", inv)}, count {gate.Count}, "
            + $"weight {gate.Weight.ToString("0.##", inv)}{counted}, slot target: {words} → {pick}, t {Time.unscaledTime.ToString("F2", inv)}");
    }

    // The game's UI event systems, found even while switched off (a switched-off one is no longer EventSystem.current).
    private static CommandResult UiEvents(CommandCall call)
    {
        bool on = call.Lower(1) == "on";
        var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.EventSystems.EventSystem>());
        var parts = new List<string>();
        foreach (var obj in all)
        {
            var events = obj.TryCast<UnityEngine.EventSystems.EventSystem>();
            if (!Game.Alive(events)) continue;
            if (on && !events!.enabled) events.enabled = true;
            parts.Add($"{events!.gameObject.name} {(events.enabled ? "on" : "off")}");
        }
        string current = Game.Alive(UnityEngine.EventSystems.EventSystem.current) ? UnityEngine.EventSystems.EventSystem.current!.gameObject.name : "none";
        return CommandResult.Ok($"uievents: {(parts.Count > 0 ? string.Join(", ", parts) : "none")}; current {current}");
    }
}
