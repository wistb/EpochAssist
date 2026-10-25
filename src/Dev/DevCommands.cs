using System.Text.RegularExpressions;
using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Dev;

// The dev commands, registered by the dev bridge feature; the registry lists and runs them only under the dev loader.
internal static class DevCommands
{
    private const float ShotTimeoutSeconds = 5f;
    private const int SettleFrames = 2;

    private static bool s_drawTest, s_drawMinimap, s_drawArrows, s_drawBars;
    private static readonly List<WorldEntry> s_drawList = new();

    private static string ShotDir => Path.Combine(DevBridge.Dir, "shots");

    public static void Register(string owner)
    {
        void Add(string name, string usage, string help, Func<CommandCall, CommandResult> run) =>
            Commands.Register(new Command(name, usage, help, CommandMark.Dev, owner, run));

        Add("menu", "menu open <page> [tab]|close|search [text]", "Opens the menu on a page, closes it, or searches the settings.", MenuCommand);
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
        Add("draw", "draw test|minimap|arrows|bars on|off", "Draws a test pattern around the player, markers on the minimap, edge arrows for the 10 nearest rares, or bars over enemies.", DrawCommand);
        Add("focus", "focus", "Shows the menu control that has keyboard focus.", _ =>
        {
            string? focused = Focus.Current;
            return CommandResult.Ok("focus: " + (focused == null ? "none" : focused.Contains('/') ? focused : $"{Menu.Current}/{focused}"));
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
    }

    // keys [frames]: Unity's own view of the keyboard each frame, to see what makes Input.anyKeyDown true.
    private static CommandResult KeysCommand(CommandCall call)
    {
        int frames = int.TryParse(call.Lower(1), out int n) ? Math.Clamp(n, 1, 120) : 10;
        var all = Enum.GetValues<KeyCode>().Distinct().Where(k => k != KeyCode.None).ToArray();
        var lines = new List<string> { $"ok keys: {frames} frames, focused {Application.isFocused}" };
        int seen = 0;
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                var down = all.Where(Input.GetKeyDown).Select(k => k.ToString()).ToList();
                var held = all.Where(Input.GetKey).Select(k => k.ToString()).ToList();
                lines.Add($"frame {Time.frameCount}: anyKeyDown {Input.anyKeyDown}, anyKey {Input.anyKey}, down [{string.Join(", ", down)}], held [{string.Join(", ", held)}], "
                    + $"inputString \"{Input.inputString}\"");
                return ++seen < frames ? null : DevOutput.Reply("keys", lines);
            },
        };
    }

    // Harmony keeps listing a method after its last patch is removed, so only methods that still carry one count.
    private static int PatchedMethods() => HarmonyLib.Harmony.GetAllPatchedMethods().Count(m =>
        HarmonyLib.Harmony.GetPatchInfo(m) is { } info && info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count + info.Finalizers.Count > 0);

    public static void Reset()
    {
        s_drawTest = s_drawMinimap = s_drawArrows = s_drawBars = false;
        Watch.Stop("bridge stopped");
        EventCommands.StopWatch("bridge stopped");
        Hovered.Reset();
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

    // draw minimap on also prints the mapping and, per game icon, its screen point against ours.
    private static CommandResult DrawCommand(CommandCall call)
    {
        bool on = call.Lower(2) == "on";
        if (call.Lower(2) is not ("on" or "off")) return CommandResult.Error("draw: usage draw test|minimap|arrows|bars on|off");
        switch (call.Lower(1))
        {
            case "test":
                s_drawTest = on;
                break;
            case "minimap":
                s_drawMinimap = on;
                if (on) return new CommandResult(DevOutput.Reply("draw", MinimapLines())) with { Settle = SettleFrames };
                break;
            case "arrows":
                s_drawArrows = on;
                break;
            case "bars":
                s_drawBars = on;
                break;
            default:
                return CommandResult.Error($"unknown command: {call.Line}");
        }
        if (on) World.WakeFor(600f);
        return CommandResult.Ok($"draw {call.Lower(1)} {(on ? "on" : "off")}") with { Settle = SettleFrames };
    }

    private static List<string> MinimapLines()
    {
        World.WakeFor(600f);
        if (!MinimapMap.Ready) return new List<string> { "ok draw minimap on: no minimap mapping now (no minimap, hidden, or visuals: minimap missing)" };
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rect = MinimapMap.ScreenRect;
        var (sx, sy, rotation) = MinimapMap.Scale;
        var lines = new List<string>
        {
            $"ok draw minimap on: rect ({rect.x.ToString("F0", inv)}, {rect.y.ToString("F0", inv)}, {rect.width.ToString("F0", inv)} x {rect.height.ToString("F0", inv)}), "
                + $"scale {sx.ToString("F3", inv)} / {sy.ToString("F3", inv)} px/m, rotation {rotation.ToString("F1", inv)}, source {MinimapMap.Source}",
        };
        foreach (var (name, game, ours) in MinimapMap.Icons())
        {
            if (!rect.Contains(game)) continue;
            string d = ours is Vector2 o ? Vector2.Distance(o, game).ToString("F2", inv) + " px" : "-";
            lines.Add($"{name}: game ({game.x.ToString("F1", inv)}, {game.y.ToString("F1", inv)}) ours ({ours?.x.ToString("F1", inv)}, {ours?.y.ToString("F1", inv)}) off {d}");
        }
        return lines;
    }

    // Markers, arrows and bars for the dev checks, from the world model.
    private static void DrawWorldTests()
    {
        if (s_drawMinimap)
        {
            World.InRange(new WorldQuery { Kind = WorldKind.Enemy }, s_drawList);
            foreach (var e in s_drawList.OrderBy(e => e.Distance).Take(10)) Draw.MinimapMarker(e.Position, Theme.Pause);
            World.InRange(new WorldQuery { Kind = WorldKind.Shrine, Alive = false }, s_drawList);
            foreach (var e in s_drawList) Draw.MinimapMarker(e.Position, Theme.Good);
            World.InRange(new WorldQuery { Kind = WorldKind.Chest, Alive = false }, s_drawList);
            foreach (var e in s_drawList) Draw.MinimapMarker(e.Position, Theme.Accent);
        }
        if (s_drawArrows)
        {
            World.InRange(new WorldQuery { Kind = WorldKind.Enemy, MinRarity = Rarity.Rare }, s_drawList);
            foreach (var e in s_drawList.OrderBy(e => e.Distance).Take(10)) Draw.EdgeArrow(e.Position, Theme.Pause);
        }
        if (s_drawBars)
        {
            World.InRange(new WorldQuery { Kind = WorldKind.Enemy }, s_drawList);
            foreach (var e in s_drawList.OrderBy(e => e.Distance).Take(10))
            {
                if (e.HealthFraction is not float health) continue;
                float? ward = e.Ward is float w && e.MaxHealth is float max && max > 0f ? w / max : null;
                Draw.WorldBar(e.Position, 2.4f, health, ward, BarColours.Default);
            }
            if (Player.Transform is Transform me && PlayerModel.Vitals is { } vitals)
            {
                float? ward = vitals.Ward is float w && vitals.MaxHealth > 0f ? w / vitals.MaxHealth : null;
                Draw.WorldBar(me.position, -0.3f, vitals.HealthFraction, ward, BarColours.Default, 80f, 7f);
            }
        }
    }

    /// <summary>The draw test pattern around the player (a circle, a line, a label and a marker), and the other draw tests.</summary>
    public static void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        DrawWorldTests();
        if (!s_drawTest || Player.Transform is not Transform t) return;
        Vector3 p = t.position;
        Draw.WorldCircle(p, 3f, Theme.Accent, 2f);
        Draw.WorldLine(p, p + new Vector3(3f, 0f, 0f), Theme.Pause, 2f);
        Draw.WorldMarker(p + new Vector3(0f, 2.5f, 0f), Theme.Good, 14f);
        Draw.WorldLabel(p + new Vector3(0f, 3.5f, 0f), "Draw test", Theme.Text);
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

    private static CommandResult MenuCommand(CommandCall call)
    {
        switch (call.Lower(1))
        {
            case "open":
                string page = call.Lower(2);
                if (!Menu.PageNames.Contains(page)) return CommandResult.Error($"unknown page {page}");
                string opened = Actions.Run($"menu.page.{page}", "dev bridge");
                if (page == Menu.SettingsPage && int.TryParse(call.Lower(3), out int tab)) Menu.SetSettingsTab(tab);
                return new CommandResult(opened.StartsWith("ok") ? $"ok {call.Line}" : opened) { Settle = SettleFrames };
            case "close":
                string closed = Actions.Run("menu.close", "dev bridge");
                return new CommandResult(closed.StartsWith("ok") ? $"ok {call.Line}" : closed) { Settle = SettleFrames };
            case "search":
                Menu.SetSearch(call.Rest(2));
                return CommandResult.Ok(call.Line) with { Settle = SettleFrames };
            default:
                return CommandResult.Error($"unknown command: {call.Line}");
        }
    }

    private static CommandResult HudCommand(CommandCall call)
    {
        if (call.Lower(1) != "panel") return CommandResult.Error($"unknown command: {call.Line}");
        string panel = Hud.PanelOpen == (call.Lower(2) == "open") ? "ok" : Actions.Run("hud.togglePanel", "dev bridge");
        return new CommandResult(panel.StartsWith("ok") ? $"ok {call.Line}" : panel) { Settle = SettleFrames };
    }

    // move <x> <y> [held]: feeds the game a keyboard movement direction, as a held key would.
    private static CommandResult Move(CommandCall call)
    {
        var mpm = Game.Input?.movementProcessor;
        if (!Game.Alive(mpm) || call.Args.Length < 2
            || !float.TryParse(call.Args[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mx)
            || !float.TryParse(call.Args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float my))
        {
            return CommandResult.Error($"{call.Line}: usage move <x> <y> [true|false]");
        }
        mpm!.ProcessKeyboardMovement(new Vector2(mx, my), call.Lower(3) != "false");
        return CommandResult.Ok(call.Line);
    }

    // gate: autocast's enemy gate as it decides, for comparing its gate transitions and aim across builds; the name is
    // the one the nameplate shows.
    private static CommandResult Gate()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string closest = Features.AutoCast.ClosestEnemy is var (name, distance) ? $"\"{name ?? "?"}\" {distance.ToString("F1", inv)}m" : "none";
        return CommandResult.Ok($"gate: open {Features.AutoCast.EnemyGateOpen}, count {Features.AutoCast.EnemiesInRange}, "
            + $"weight {Features.AutoCast.EnemyWeightInRange.ToString("0.##", inv)}, closest {closest}, t {Time.unscaledTime.ToString("F2", inv)}");
    }

    // player: what the player lookup sees, for a mode where it finds the object but no actor (online).
    private static string PlayerFacts()
    {
        static string Read(string label, Func<string> read)
        {
            try
            {
                return $"{label} {read()}";
            }
            catch (Exception e)
            {
                return $"{label} ? ({e.GetType().Name})";
            }
        }
        static string PathOf(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
        static string Components(GameObject obj) => string.Join(", ", obj.GetComponents<Component>().Select(c => c.GetIl2CppType().Name));

        var parts = new List<string>
        {
            Read("finder", () => Il2Cpp.PlayerFinder.getPlayerActor() is { } a ? PathOf(a.transform) : "null"),
            Read("lookup", () => $"object {(Player.Object is { } o ? PathOf(o.transform) : "none")}, actor {(Player.Actor is { } a ? PathOf(a.transform) : "none")}, online {Player.Online?.ToString() ?? "?"}"),
        };
        foreach (string name in new[] { "MainPlayer(Clone)", "Local Player(Clone)" })
        {
            parts.Add(Read($"'{name}'", () =>
            {
                var obj = GameObject.Find(name);
                if (obj == null) return "not found";
                var child = obj.GetComponentInChildren<Il2Cpp.Actor>(true);
                var parent = obj.GetComponentInParent<Il2Cpp.Actor>();
                return $"components [{Components(obj)}], actor in children {(child != null ? PathOf(child.transform) : "none")}, in parents {(parent != null ? PathOf(parent.transform) : "none")}";
            }));
        }
        static string Tracker(Il2Cpp.CharacterDataTracker? t) =>
            t == null ? "null" : $"slot {t.characterSlotId}, data {(t.charData != null ? $"name {t.charData.CharacterName?.Length ?? -1} chars" : "null")}";
        parts.Add(Read("getPlayerDataTracker", () => Tracker(Il2Cpp.PlayerFinder.getPlayerDataTracker())));
        parts.Add(Read("localPlayerDataTracker", () => Tracker(Il2Cpp.PlayerFinder.localPlayerDataTracker)));
        parts.Add(Read("getPlayerData", () => Il2Cpp.PlayerFinder.getPlayerData() is { } d ? $"name {d.CharacterName?.Length ?? -1} chars" : "null"));
        parts.Add(Read("getLocalPlayerInMultiplayer", () => Il2Cpp.PlayerFinder.getLocalPlayerInMultiplayer() is { } lp ? PathOf(lp.transform) : "null"));
        parts.Add(Read("gameplay service", () =>
        {
            if (!GameplayCharacter.TryRead(out string id, out string name, out bool offline, out string reason)) return reason;
            // Only a hash of the id: enough to compare across sessions without logging it.
            return $"id {(uint)id.Aggregate(17, (h, c) => h * 31 + c) % 100000:D5} (hash, {id.Length} chars), name {name.Length} chars, offline {offline}";
        }));
        parts.Add(Read("localPlayerInfo", () => Il2Cpp.LocalPlayer.instance?.localPlayerInfo is { } info ? Members(info) : "null"));
        parts.Add(Read("trackers in scene", () =>
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Il2Cpp.CharacterDataTracker>())
                .Select(o => o.TryCast<Il2Cpp.CharacterDataTracker>()).Where(t => t != null).Select(t => $"{PathOf(t!.transform)} ({Tracker(t)})").ToList();
            return all.Count == 0 ? "none" : string.Join(", ", all);
        }));
        parts.Add(Read("actors with a character tracker", () =>
        {
            var tracked = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Il2Cpp.Actor>())
                .Select(o => o.TryCast<Il2Cpp.Actor>()).Where(a => a != null && a.characterDataTracker != null)
                .Select(a => $"{PathOf(a!.transform)} (slot {a.characterDataTracker!.characterSlotId}, data {(a.characterDataTracker.charData != null ? "yes" : "no")})").ToList();
            return tracked.Count == 0 ? "none" : string.Join(", ", tracked);
        }));
        return string.Join("; ", parts);
    }

    // A game object's own properties with their values; strings only as their length, so no names are shown.
    private static string Members(object obj)
    {
        var type = obj.GetType();
        var values = new List<string>();
        foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (p.DeclaringType?.Namespace?.StartsWith("Il2CppInterop") != false || p.GetIndexParameters().Length > 0) continue;
            string value;
            try
            {
                value = p.GetValue(obj) switch
                {
                    null => "null",
                    string s => $"{s.Length} chars",
                    var v when v.GetType().IsPrimitive || v.GetType().IsEnum => v.ToString()!,
                    var v => v.GetType().Name,
                };
            }
            catch (Exception e)
            {
                value = $"? ({(e.InnerException ?? e).GetType().Name})";
            }
            values.Add($"{p.Name}={value}");
        }
        return $"{type.Name} {{{string.Join(", ", values)}}}";
    }

    // facts: read-only values behind the state checks (typing, game pause menu, loading, arrival, death),
    // each read on its own so one missing member doesn't hide the rest.
    private static string Facts()
    {
        static string Read(string label, Func<string> read)
        {
            try
            {
                return $"{label} {read()}";
            }
            catch (Exception e)
            {
                return $"{label} ? ({e.GetType().Name})";
            }
        }
        static IEnumerable<T> All<T>() where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase =>
            Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<T>()).Select(o => o.TryCast<T>()).Where(o => o != null)!;

        var ui = Il2Cpp.UIBase.instance;
        var obj = Player.Object;
        string scene = GameState.Scene;
        return string.Join("; ", new[]
        {
            Read("timeScale", () => $"{Time.timeScale:F2}, focused {Application.isFocused}"),
            Read("typing:", () => $"game {GameState.Typing}, chat {(Game.Alive(ui) ? ui!.IsChatInputFocused.ToString() : "no UIBase")}, imgui keyboardControl {GUIUtility.keyboardControl}"),
            Read("selected", () =>
            {
                var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                if (selected == null) return "none";
                var field = selected.GetComponentInParent<UnityEngine.UI.InputField>();
                bool tmp = selected.GetComponentsInParent<Component>().Any(c => c.GetIl2CppType().Name == "TMP_InputField");
                return $"'{selected.name}' (InputField {(field != null ? $"focused {field.isFocused}" : "none")}, TMP_InputField {(tmp ? "present" : "none")})";
            }),
            Read("pause: menuOpen", () => Game.Alive(ui) ? $"{ui!.menuOpen}, keybindMenuOpen {ui.keybindMenuOpen}" : "no UIBase"),
            Read("bottomScreenMenu", () =>
            {
                var bottom = Game.Alive(ui) ? ui!.bottomScreenMenu : null;
                if (!Game.Alive(bottom)) return "none";
                bool image = bottom!.transform.Find("Menu Image") != null;
                return $"active {bottom.gameObject.activeSelf}, 'Menu Image' {(image ? "present" : "missing")}";
            }),
            Read("pauseWhileActiveIfOffline", () => string.Join(", ", All<Il2Cpp.PauseWhileActiveIfOffline>()
                .Where(p => p.gameObject.activeInHierarchy).Select(p => $"'{p.gameObject.name}' hasPaused {p.hasPaused}")) is { Length: > 0 } s ? s : "none active"),
            Read("loading: visible", () => Il2CppLE.UI.LoadingScreen.IsVisible.ToString()),
            Read("scenes:", () =>
            {
                var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var names = Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                    .Select(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i))
                    .Select(s => s.name + (s.handle == active.handle ? "*" : string.Empty) + (s.isLoaded ? string.Empty : "(loading)"));
                return string.Join(", ", names);
            }),
            Read("zone:", () => scene.Length == 0 ? "no scene" : $"town {Il2Cpp.SceneList.IsTownZone(scene)}, nonCombat {Il2Cpp.SceneList.IsNonCombatZone(scene)}, restZone {Il2Cpp.SceneList.IsMonolithRestZone(scene)}"),
            Read("arrival:", () => Player.Health is Il2Cpp.PlayerHealth h
                ? $"isInSceneLoad {h.isInSceneLoad}, damageable {(Game.Alive(h.playerZoneTransition) ? h.playerZoneTransition.damageable().ToString() : "no handler")}, isDamageable {h.isDamageable()}"
                : "no PlayerHealth"),
            Read("death: health", () => Player.Health is Il2Cpp.PlayerHealth h ? $"{h.currentHealth:F0}/{h.maxHealth:F0}" : "none"),
            Read("visuals.dead", () => obj != null && obj.GetComponent<Il2Cpp.ActorVisuals>() is Il2Cpp.ActorVisuals v ? v.dead.ToString() : "no ActorVisuals"),
            Read("sync.IsDead", () => obj != null && obj.GetComponent<Il2Cpp.ActorSync>() is Il2Cpp.ActorSync a ? a.IsDead.ToString() : "no ActorSync"),
            Read("deathScreen active", () => string.Join(", ", All<Il2Cpp.DeathScreen>().Select(d => d.gameObject.activeInHierarchy.ToString())) is { Length: > 0 } s ? s : "none"),
        });
    }

    // travel [scene]: the game's own waypoint travel, to reach a zone change offline; no scene lists the unlocked
    // waypoints. The waypoints live in the world map, so it must have been opened once.
    private static string Travel(string scene)
    {
        var waypoints = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Il2Cpp.UIWaypoint>())
            .Select(o => o.TryCast<Il2Cpp.UIWaypoint>()).Where(w => w != null && w.isActive && !string.IsNullOrEmpty(w.sceneName)).ToList();
        if (waypoints.Count == 0) return "error travel: no unlocked waypoints loaded (open the map once)";
        if (scene.Length == 0) return "ok waypoints: " + string.Join(", ", waypoints.Select(w => w!.sceneName).Distinct().OrderBy(n => n));
        var target = waypoints.FirstOrDefault(w => w!.sceneName.Equals(scene, StringComparison.OrdinalIgnoreCase));
        if (target == null) return $"error travel: no unlocked waypoint {scene}";
        target.LoadWaypointScene();
        return $"ok travel {target.sceneName}";
    }

    // probe <type> [filter]: the type's declared methods, fields and properties, from the game's interop assemblies.
    private static string Probe(string typeName, string? filter)
    {
        var lookup = GameTypes.Resolve(typeName);
        if (lookup.Type is not { } type) return $"error probe: {lookup.Error}";

        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        var members = type.GetMethods(all).Where(m => !m.IsSpecialName)
            .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name))})")
            .Concat(type.GetProperties(all).Select(x => $"prop {x.PropertyType.Name} {x.Name}"))
            .Where(m => filter == null || m.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct().OrderBy(m => m).ToList();

        Directory.CreateDirectory(DevBridge.Dir);
        File.WriteAllLines(Path.Combine(DevBridge.Dir, "probe.txt"), members.Prepend(type.FullName ?? typeName));
        return $"ok probe {type.FullName}: {members.Count} members in probe.txt";
    }

    // find <text>: Type.Member for every method in the game's Il2Cpp assemblies whose name contains the text.
    private static string Find(string text)
    {
        if (text.Length < 3) return "error find: give at least 3 characters";
        var hits = new List<string>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Il2Cpp") == true))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var type in types)
            {
                foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (!m.Name.StartsWith("NativeMethodInfoPtr") && m.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                        hits.Add($"{assembly.GetName().Name}: {type.FullName}.{m.Name}");
                }
            }
        }
        Directory.CreateDirectory(DevBridge.Dir);
        File.WriteAllLines(Path.Combine(DevBridge.Dir, "find.txt"), hits.Distinct().OrderBy(h => h));
        return $"ok find {text}: {hits.Count} methods in find.txt";
    }

    // ---- Screenshots ----

    private sealed class Capture
    {
        public string Name = string.Empty;
        public string FullPath = string.Empty;
        public Rect? Crop;
        public float Deadline;
        public long LastSize = -1;
        public bool Matte;
        // Matte shots: the first capture (over black) waits here while the second (over white) is taken.
        public string? BlackPath;
    }

    // Matte shots fill the screen under our HUD and menu with this colour; null draws nothing.
    private static Color? s_backdrop;

    /// <summary>Called by the host before the HUD layer draws.</summary>
    public static void DrawBackdrop()
    {
        if (s_backdrop is not Color color || Event.current.type != EventType.Repaint) return;
        Matrix4x4 matrix = GUI.matrix;
        Color old = GUI.color;
        GUI.matrix = Matrix4x4.identity;
        GUI.color = color;
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = old;
        GUI.matrix = matrix;
    }

    private static CommandResult StartCapture(string target, string name, bool matte)
    {
        if (matte && target == "full") throw new ArgumentException("matte needs the menu or the HUD");
        Rect? crop = target switch
        {
            "menu" => Menu.Visible ? Menu.ScreenRect : throw new InvalidOperationException("the menu is closed"),
            "hud" => Hud.ScreenRect is { width: > 0 } r ? r : throw new InvalidOperationException("the HUD is not shown"),
            "full" => null,
            _ => throw new ArgumentException("shot target is full, menu or hud"),
        };

        Directory.CreateDirectory(ShotDir);
        string full = Path.Combine(ShotDir, "_screen.png");
        if (File.Exists(full)) File.Delete(full);
        if (matte) s_backdrop = Color.black;
        ScreenCapture.CaptureScreenshot(full, 1);
        var capture = new Capture { Name = SafeName(name), FullPath = full, Crop = crop, Deadline = Time.unscaledTime + ShotTimeoutSeconds, Matte = matte };
        return new CommandResult(string.Empty) { Pending = () => ContinueCapture(capture) };
    }

    // Unity writes the screenshot at the end of a later frame; wait until the file exists and stops growing.
    private static string? ContinueCapture(Capture capture)
    {
        if (Time.unscaledTime > capture.Deadline)
        {
            s_backdrop = null;
            return $"error shot {capture.Name}: the screenshot was not written";
        }
        if (!File.Exists(capture.FullPath)) return null;
        long size = new FileInfo(capture.FullPath).Length;
        if (size == 0 || size != capture.LastSize)
        {
            capture.LastSize = size;
            return null;
        }

        if (capture.Matte && capture.BlackPath == null)
        {
            capture.BlackPath = Path.Combine(ShotDir, "_black.png");
            File.Copy(capture.FullPath, capture.BlackPath, overwrite: true);
            File.Delete(capture.FullPath);
            capture.LastSize = -1;
            s_backdrop = Color.white;
            ScreenCapture.CaptureScreenshot(capture.FullPath, 1);
            return null;
        }

        try
        {
            string output = Path.Combine(ShotDir, capture.Name + ".png");
            Vector2Int dims = capture.BlackPath != null && capture.Crop is Rect matteCrop
                ? MatteTo(capture.BlackPath, capture.FullPath, matteCrop, output)
                : capture.Crop is Rect crop ? CropTo(capture.FullPath, crop, output) : CopyFull(capture.FullPath, output);
            return $"shot {capture.Name}.png {dims.x}x{dims.y}";
        }
        catch (Exception e)
        {
            return $"error shot {capture.Name}: {e.Message}";
        }
        finally
        {
            s_backdrop = null;
            if (capture.BlackPath != null) File.Delete(capture.BlackPath);
        }
    }

    private static Vector2Int CopyFull(string full, string output)
    {
        File.Copy(full, output, overwrite: true);
        var probe = LoadTexture(full);
        var dims = new Vector2Int(probe.width, probe.height);
        UnityEngine.Object.Destroy(probe);
        return dims;
    }

    // crop is in screen pixels with a top-left origin; textures count rows from the bottom.
    private static Vector2Int CropTo(string full, Rect crop, string output)
    {
        Texture2D screen = LoadTexture(full);
        try
        {
            float sx = screen.width / (float)Screen.width, sy = screen.height / (float)Screen.height;
            int x = Mathf.Clamp(Mathf.FloorToInt((crop.x - 4f) * sx), 0, screen.width - 1);
            int w = Mathf.Clamp(Mathf.CeilToInt((crop.width + 8f) * sx), 1, screen.width - x);
            int top = Mathf.Clamp(Mathf.FloorToInt((crop.y - 4f) * sy), 0, screen.height - 1);
            int h = Mathf.Clamp(Mathf.CeilToInt((crop.height + 8f) * sy), 1, screen.height - top);
            int y = screen.height - top - h;

            var cut = new Texture2D(w, h, TextureFormat.RGBA32, false);
            cut.SetPixels(screen.GetPixels(x, y, w, h));
            cut.Apply();
            File.WriteAllBytes(output, ImageConversion.EncodeToPNG(cut).ToArray());
            UnityEngine.Object.Destroy(cut);
            return new Vector2Int(w, h);
        }
        finally
        {
            UnityEngine.Object.Destroy(screen);
        }
    }

    // Over black a pixel reads a·c, over white a·c + (1 − a): the difference gives alpha, the black capture the colour.
    // The result is trimmed to the pixels that were drawn.
    private static Vector2Int MatteTo(string blackPath, string whitePath, Rect crop, string output)
    {
        Texture2D black = LoadTexture(blackPath), white = LoadTexture(whitePath);
        try
        {
            if (black.width != white.width || black.height != white.height) throw new IOException("the two captures differ in size");
            float sx = black.width / (float)Screen.width, sy = black.height / (float)Screen.height;
            int x = Mathf.Clamp(Mathf.FloorToInt((crop.x - 4f) * sx), 0, black.width - 1);
            int w = Mathf.Clamp(Mathf.CeilToInt((crop.width + 8f) * sx), 1, black.width - x);
            int top = Mathf.Clamp(Mathf.FloorToInt((crop.y - 4f) * sy), 0, black.height - 1);
            int h = Mathf.Clamp(Mathf.CeilToInt((crop.height + 8f) * sy), 1, black.height - top);
            int y = black.height - top - h;

            var overBlack = black.GetPixels(x, y, w, h);
            var overWhite = white.GetPixels(x, y, w, h);
            var pixels = new Color[w * h];
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color b = overBlack[i], c = overWhite[i];
                float a = Mathf.Clamp01(1f - ((c.r - b.r) + (c.g - b.g) + (c.b - b.b)) / 3f);
                if (a < 0.5f / 255f) continue;
                pixels[i] = new Color(Mathf.Clamp01(b.r / a), Mathf.Clamp01(b.g / a), Mathf.Clamp01(b.b / a), a);
                int col = i % w, row = i / w;
                minX = Math.Min(minX, col); maxX = Math.Max(maxX, col);
                minY = Math.Min(minY, row); maxY = Math.Max(maxY, row);
            }
            if (maxX < 0) throw new IOException("nothing was drawn there");

            int tw = maxX - minX + 1, th = maxY - minY + 1;
            var trimmed = new Color[tw * th];
            for (int row = 0; row < th; row++) Array.Copy(pixels, (minY + row) * w + minX, trimmed, row * tw, tw);

            var cut = new Texture2D(tw, th, TextureFormat.RGBA32, false);
            cut.SetPixels(trimmed);
            cut.Apply();
            File.WriteAllBytes(output, ImageConversion.EncodeToPNG(cut).ToArray());
            UnityEngine.Object.Destroy(cut);
            return new Vector2Int(tw, th);
        }
        finally
        {
            UnityEngine.Object.Destroy(black);
            UnityEngine.Object.Destroy(white);
        }
    }

    private static Texture2D LoadTexture(string path)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path))) throw new IOException("could not decode the screenshot");
        return texture;
    }

    private static string SafeName(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length > 0 ? new string(chars) : "shot";
    }
}
