using System.Globalization;
using EpochAssist.Core;
using Il2CppInterop.Runtime;

namespace EpochAssist.Dev;

// edits, look, tooltip, gameui, ui: the client-side layers driven by hand. Everything they change is owned by the crash
// test feature (or the dev bridge with "as dev"), so `crash fault`, a restart and a reload show the restore paths.
internal static class ClientCommands
{
    public const string EditsUsage = "edits [list] | edits test <name> <value> [as dev] | edits remove <name>|all [as dev] | edits drift <name> <value>";
    public const string LookUsage = "look [list] | look outline <n> <group> [walls] | look tint <n> <r,g,b> | look effects <m> <r,g,b> | look colour <group> <r,g,b> | look off";
    public const string TooltipUsage = "tooltip test on|off";
    public const string GameUiUsage = "gameui hide|show";
    public const string UiUsage = "ui confirm [kind] | ui decline | ui respawn | ui quickmove <x> <y> [n] (n over a 4-wide block) | ui prompt";

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    // The owner of what these commands change: the crash test feature, or the dev bridge with "as dev".
    private static Feature? Owner(bool asDev) => FeatureHost.Find(asDev ? "dev" : "crashtest") is { State: FeatureState.Running } f ? f : null;

    private static string OwnerMissing(bool asDev) =>
        $"the {(asDev ? "dev bridge" : "crash test feature")} isn't running ({FeatureHost.Find(asDev ? "dev" : "crashtest")?.State.ToString().ToLowerInvariant() ?? "not registered"})";

    // ---- edits ----

    /// <summary>A test target: how to apply a typed value from text, and how to write it past the registry (drift).</summary>
    private sealed class TestTarget
    {
        public string Key = string.Empty;
        public Func<Feature, string, Edit?> Apply = null!;
        public Func<string, string?> Drift = null!;
    }

    private static readonly Dictionary<(string Key, string Owner), Edit> s_tests = new();

    private static IEnumerable<UnityEngine.Object> One(UnityEngine.Object? obj)
    {
        if (Game.Alive(obj)) yield return obj!;
    }

    internal static readonly EditTarget<float> RevealRadius = new()
    {
        Name = "minimap: reveal radius",
        Find = () => One(Game.Minimap),
        Read = o => o.Cast<Il2CppLE.UI.Minimap.Minimap>().RevealRadius,
        Write = (o, v) => o.Cast<Il2CppLE.UI.Minimap.Minimap>().RevealRadius = v,
    };

    internal static readonly EditTarget<float> ZoomMax = new()
    {
        Name = "minimap: zoom max",
        Find = () => One(Game.MinimapZoom),
        Read = o => o.Cast<Il2Cpp.DMMapZoom>().maxMinimapZoom,
        Write = (o, v) => o.Cast<Il2Cpp.DMMapZoom>().maxMinimapZoom = v,
    };

    internal static readonly EditTarget<float> CameraZoomMin = new()
    {
        Name = "camera: zoom min",
        Find = () => One(Game.Camera),
        Read = o => o.Cast<Il2Cpp.CameraManager>().zoomMin,
        Write = (o, v) => o.Cast<Il2Cpp.CameraManager>().zoomMin = v,
    };

    private static TestTarget FloatTest(string key, EditTarget<float> target) => new()
    {
        Key = key,
        Apply = (owner, text) => float.TryParse(text, NumberStyles.Float, s_inv, out float v) ? Edits.Apply(owner, target, v) : null,
        Drift = text =>
        {
            if (!float.TryParse(text, NumberStyles.Float, s_inv, out float v)) return "not a number";
            int written = 0;
            foreach (var obj in target.Find())
            {
                target.Write!(obj, v);
                written++;
            }
            return written > 0 ? null : "no object found";
        },
    };

    private static TestTarget BoolTest(string key, EditTarget<bool> target) => new()
    {
        Key = key,
        Apply = (owner, text) => bool.TryParse(text, out bool v) ? Edits.Apply(owner, target, v) : null,
        Drift = text =>
        {
            if (!bool.TryParse(text, out bool v)) return "not true or false";
            int written = 0;
            foreach (var obj in target.Find())
            {
                target.Write!(obj, v);
                written++;
            }
            return written > 0 ? null : "no object found";
        },
    };

    private static readonly Dictionary<string, TestTarget> s_targets = new()
    {
        ["reveal"] = FloatTest("reveal", RevealRadius),
        ["zoommax"] = FloatTest("zoommax", ZoomMax),
        ["camzoom"] = FloatTest("camzoom", CameraZoomMin),
        ["zoom"] = FloatTest("zoom", EditTargets.MinimapZoom),
        ["ui"] = BoolTest("ui", GameUi.Target),
    };

    public static CommandResult EditsCommand(CommandCall call)
    {
        // The host removes an owner's edits when it stops, faults or reloads; those are forgotten here.
        foreach (var key in s_tests.Where(t => t.Value.Removed).Select(t => t.Key).ToList()) s_tests.Remove(key);
        bool asDev = call.Lower(call.Args.Length - 1) == "as" && call.Lower(call.Args.Length) == "dev";
        switch (call.Lower(1))
        {
            case "" or "list":
                return EditsList();
            case "test" when call.Args.Length >= 3:
            {
                if (!s_targets.TryGetValue(call.Lower(2), out var test)) return CommandResult.Error($"edits test: unknown target '{call.Lower(2)}' (one of {string.Join(", ", s_targets.Keys)})");
                if (Owner(asDev) is not { } owner) return CommandResult.Error($"edits test: {OwnerMissing(asDev)}");
                if (FeatureHost.Find("crashtest") is { } crash && !crash.Has(CrashTestFeature.EditsGroup))
                    return CommandResult.Error($"edits test: unavailable, '{CrashTestFeature.EditsGroup}' is missing from the game");
                if (s_tests.TryGetValue((test.Key, owner.Id), out var old) && !old.Removed)
                {
                    old.Remove();
                }
                var edit = test.Apply(owner, call.Lower(3));
                if (edit == null) return CommandResult.Error($"edits test {test.Key}: bad value '{call.Lower(3)}'");
                s_tests[(test.Key, owner.Id)] = edit;
                return CommandResult.Ok($"edits test {test.Key}: {edit.Describe()}") with { Settle = 2 };
            }
            case "remove" when call.Args.Length >= 2:
            {
                if (call.Lower(2) == "all")
                {
                    int removed = 0;
                    foreach (var entry in s_tests.Where(t => asDev ? t.Key.Owner == "dev" : t.Key.Owner == "crashtest").ToList())
                    {
                        if (!entry.Value.Removed)
                        {
                            entry.Value.Remove();
                            removed++;
                        }
                        s_tests.Remove(entry.Key);
                    }
                    return CommandResult.Ok($"edits remove all: {removed} removed, {Edits.Count} active") with { Settle = 2 };
                }
                string owner = asDev ? "dev" : "crashtest";
                if (!s_tests.TryGetValue((call.Lower(2), owner), out var edit) || edit.Removed) return CommandResult.Error($"edits remove: no test edit '{call.Lower(2)}' owned by {owner}");
                edit.Remove();
                s_tests.Remove((call.Lower(2), owner));
                return CommandResult.Ok($"edits remove {call.Lower(2)}: removed, {Edits.Count} active") with { Settle = 2 };
            }
            case "drift" when call.Args.Length >= 3:
            {
                if (!s_targets.TryGetValue(call.Lower(2), out var test)) return CommandResult.Error($"edits drift: unknown target '{call.Lower(2)}'");
                // Written past the registry on purpose, as another mod would.
                return test.Drift(call.Lower(3)) is { } error ? CommandResult.Error($"edits drift {test.Key}: {error}") : CommandResult.Ok($"edits drift {test.Key}: wrote {call.Lower(3)} directly");
            }
            default:
                return CommandResult.Error($"edits: usage {EditsUsage}");
        }
    }

    // ---- look ----

    private static readonly List<Edit<UnityEngine.Color>> s_colours = new();

    private static bool TryColour(string text, out UnityEngine.Color colour)
    {
        colour = default;
        string[] parts = text.Split(',');
        if (parts.Length != 3) return false;
        var v = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, s_inv, out v[i])) return false;
        }
        colour = new UnityEngine.Color(v[0], v[1], v[2], 1f);
        return true;
    }

    private static List<WorldEntry> NearestEnemies(int count)
    {
        var list = new List<WorldEntry>();
        World.WakeFor(5f);
        World.InRange(new WorldQuery { Kind = WorldKind.Enemy, Active = true }, list);
        return list.OrderBy(e => e.Distance).Take(count).ToList();
    }

    public static CommandResult LookCommand(CommandCall call)
    {
        if (call.Lower(1) is "" or "list") return LookList();
        if (Owner(false) is not { } owner) return CommandResult.Error($"look: {OwnerMissing(false)}");
        switch (call.Lower(1))
        {
            case "outline" when int.TryParse(call.Lower(2), out int n) && n > 0 && int.TryParse(call.Lower(3), out int group):
            {
                bool walls = call.Lower(4) == "walls";
                int done = NearestEnemies(n).Count(e => Highlights.Outline(owner, e, group, walls) != null);
                return done > 0 ? CommandResult.Ok($"look outline: {done} enemies, group {group}{(walls ? ", through walls" : string.Empty)}") with { Settle = 2 }
                    : CommandResult.Error("look outline: unavailable (no active enemy, or visuals: outlines missing)");
            }
            case "tint" when int.TryParse(call.Lower(2), out int n) && n > 0 && TryColour(call.Lower(3), out var colour):
            {
                int done = NearestEnemies(n).Count(e => Highlights.Tint(owner, e, colour) != null);
                return done > 0 ? CommandResult.Ok($"look tint: {done} enemies") with { Settle = 2 } : CommandResult.Error("look tint: unavailable (no active enemy, or visuals: tints missing)");
            }
            case "effects" when float.TryParse(call.Lower(2), NumberStyles.Float, s_inv, out float range) && TryColour(call.Lower(3), out var colour):
            {
                var roots = EffectRoots(range);
                int done = roots.Count(r => Highlights.Effect(owner, r, colour) != null);
                return CommandResult.Ok($"look effects: {done} effect roots within {range.ToString("F0", s_inv)} m: {string.Join(", ", roots.Select(r => r.name).Distinct().Take(8))}") with { Settle = 2 };
            }
            case "colour" when int.TryParse(call.Lower(2), out int group) && TryColour(call.Lower(3), out var colour):
            {
                var edit = Highlights.OutlineColour(owner, group, colour);
                if (edit == null) return CommandResult.Error("look colour: unavailable (group 0-2, or visuals: outline colours missing)");
                s_colours.Add(edit);
                return CommandResult.Ok($"look colour: {edit.Describe()}") with { Settle = 2 };
            }
            case "off":
            {
                int n = 0;
                foreach (var h in Highlights.All.Where(h => h.Owner == owner).ToList())
                {
                    h.Remove();
                    n++;
                }
                foreach (var edit in s_colours) edit.Remove();
                int colours = s_colours.Count;
                s_colours.Clear();
                return CommandResult.Ok($"look off: {n} highlights and {colours} colour edits removed") with { Settle = 2 };
            }
            default:
                return CommandResult.Error($"look: usage {LookUsage}");
        }
    }

    private static CommandResult LookList()
    {
        var lines = new List<string> { $"ok look: {Highlights.Count} highlights" };
        foreach (var h in Highlights.All)
        {
            string where = h.Actor is { } a ? $"{a.Rarity?.ToString().ToLowerInvariant() ?? "-"}  {a.Distance.ToString("F1", s_inv)} m{(a.Dead ? " dead" : string.Empty)}{(a.Gone ? " gone" : string.Empty)}" : "effect";
            lines.Add($"{h.Kind.ToString().ToLowerInvariant(),-8} {h.Name,-28} {h.Look,-24} {where}  {h.Owner.Id}{(h.Active ? string.Empty : " (ended)")}");
        }
        return new CommandResult(DevOutput.Reply("look", lines));
    }

    // Active particle effects near the player, not its own: the highest "(Clone)" ancestor of each renderer.
    private static List<UnityEngine.GameObject> EffectRoots(float range)
    {
        var roots = new List<UnityEngine.GameObject>();
        if (Player.Transform is not UnityEngine.Transform player) return roots;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UnityEngine.ParticleSystemRenderer>()))
        {
            var renderer = obj.TryCast<UnityEngine.ParticleSystemRenderer>();
            if (!Game.Alive(renderer) || !renderer!.gameObject.activeInHierarchy || renderer.transform.IsChildOf(player.root)) continue;
            if (UnityEngine.Vector3.Distance(renderer.transform.position, player.position) > range) continue;
            UnityEngine.Transform root = renderer.transform;
            for (var t = renderer.transform; t != null; t = t.parent)
            {
                if (t.name.EndsWith("(Clone)", StringComparison.Ordinal)) root = t;
            }
            if (!roots.Exists(r => r.Pointer == root.gameObject.Pointer)) roots.Add(root.gameObject);
        }
        return roots;
    }

    // ---- tooltip, gameui ----

    private static Edit<bool>? s_hidden;

    public static CommandResult TooltipCommand(CommandCall call)
    {
        if (call.Lower(1) != "test" || call.Lower(2) is not ("on" or "off")) return CommandResult.Error($"tooltip: usage {TooltipUsage}");
        if (Owner(false) is not { } owner) return CommandResult.Error($"tooltip: {OwnerMissing(false)}");
        TooltipLines.Remove(owner);
        if (call.Lower(2) == "on")
            TooltipLines.Add(owner, item => new[] { $"EA test: {item.Name ?? "?"} · {(item.Affixes.Count > 0 && item.Affixes[0].ShownTier is int tier ? $"T{tier}" : "no affix")}" });
        return CommandResult.Ok($"tooltip test {call.Lower(2)}: {TooltipLines.Count} providers, patch {(TooltipLines.Patched ? "on" : "off")}") with { Settle = 2 };
    }

    public static CommandResult GameUiCommand(CommandCall call)
    {
        if (Owner(false) is not { } owner) return CommandResult.Error($"gameui: {OwnerMissing(false)}");
        switch (call.Lower(1))
        {
            case "hide":
                if (s_hidden is { Removed: false }) return CommandResult.Ok($"gameui hide: already hidden ({s_hidden.Describe()})");
                s_hidden = GameUi.Hide(owner);
                return s_hidden != null ? CommandResult.Ok($"gameui hide: {s_hidden.Describe()}") with { Settle = 2 } : CommandResult.Error("gameui hide: unavailable (gameui: hide missing)");
            case "show":
                bool was = s_hidden is { Removed: false };
                s_hidden?.Remove();
                s_hidden = null;
                return CommandResult.Ok(was ? "gameui show: shown" : "gameui show: wasn't hidden") with { Settle = 2 };
            default:
                return CommandResult.Error($"gameui: usage {GameUiUsage}");
        }
    }

    // ---- ui ----

    private static Il2CppCysharp.Threading.Tasks.UniTask<bool>? s_devPrompt;

    public static CommandResult UiCommand(CommandCall call)
    {
        if (GameState.Online != false) return CommandResult.Error("ui: offline only");
        if (CrashTestFeature.UiClient is not { } client) return CommandResult.Error($"ui: {OwnerMissing(false)}");
        switch (call.Lower(1))
        {
            case "prompt" when call.Lower(2) == "open":
                return OpenDevPrompt();
            case "prompt":
            {
                string last = DevPromptAnswer() is { } answer ? $"; dev prompt {answer}" : string.Empty;
                return PromptKinds.Open is { } kind ? CommandResult.Ok($"ui prompt: kind {kind.Id}, panel {kind.Panel}, title \"{kind.Title}\", confirmable {kind.Confirmable}{last}")
                    : CommandResult.Ok($"ui prompt: none open{last}");
            }
            case "confirm":
                return Await(client, new UiAction.Confirm(new[] { call.Args.Length > 1 ? call.Lower(2) : "dev-test" }), call.Line);
            case "decline":
                return Await(client, new UiAction.Decline(), call.Line);
            case "respawn":
                return Await(client, new UiAction.Respawn(), call.Line);
            case "quickmove" when int.TryParse(call.Lower(2), out int x) && int.TryParse(call.Lower(3), out int y):
            {
                int n = call.Args.Length > 3 && int.TryParse(call.Lower(4), out int count) ? Math.Clamp(count, 1, 200) : 1;
                return QuickMoves(client, x, y, n);
            }
            default:
                return CommandResult.Error($"ui: usage {UiUsage} | ui prompt open");
        }
    }

    private static string? s_devAnswer;

    // The game's own answer to the last dev prompt; a finished UniTask can be read only once.
    private static string? DevPromptAnswer()
    {
        if (s_devPrompt is not { } task) return s_devAnswer;
        if (task.Status == Il2CppCysharp.Threading.Tasks.UniTaskStatus.Pending) return "pending";
        s_devAnswer = task.Status == Il2CppCysharp.Threading.Tasks.UniTaskStatus.Succeeded ? $"answered {(task.GetAwaiter().GetResult() ? "yes" : "no")}" : task.Status.ToString().ToLowerInvariant();
        s_devPrompt = null;
        return s_devAnswer;
    }

    // The dev test prompt: a game confirmation modal with the dev title, which changes nothing either way.
    private static CommandResult OpenDevPrompt()
    {
        var panels = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<Il2CppLE.UI.PanelSystem.PanelSystem>())?.TryCast<Il2CppLE.UI.PanelSystem.PanelSystem>();
        if (!Game.Alive(panels)) return CommandResult.Error("ui prompt open: no panel system");
        s_devAnswer = null;
        s_devPrompt = Il2CppLE.UI.PanelSystem.PanelSystemExtensions.ConfirmAsync(panels!, PromptKinds.DevTestTitle, "A test prompt from the dev build. Nothing happens either way.", "Yes", "No",
            new Il2CppSystem.Nullable<Il2CppLE.UI.PanelSystem.PanelLocation>(Il2CppLE.UI.PanelSystem.PanelLocation.Center), null!, null!,
            new Il2CppSystem.Nullable<UnityEngine.Color>(), new Il2CppSystem.Nullable<Il2CppLE.UI.PanelSystem.ButtonColor>(), false);
        return CommandResult.Ok("ui prompt open: dev test prompt opened") with { Settle = 30 };
    }

    // The reply waits for the request's result (a few frames).
    private static CommandResult Await(DispatchClient client, UiAction action, string line)
    {
        Dispatcher.RequestUi(client, action);
        int frames = 0;
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                if (Dispatcher.LastUiResult(client) is { } result) return $"ok {line}: {result}";
                return ++frames > 300 ? $"error {line}: no result after 300 frames" : null;
            },
        };
    }

    // n moves over a 4-wide block of slots from x, y (row by row), one request at a time; then the presses per second.
    private static CommandResult QuickMoves(DispatchClient client, int x, int y, int n)
    {
        var results = new List<string>();
        var times = new List<float>();
        int i = 0;
        bool waiting = false;
        int frames = 0;
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                if (++frames > 60 * n + 300) return $"error ui quickmove: timed out after {results.Count} of {n}";
                if (waiting)
                {
                    if (Dispatcher.LastUiResult(client) is not { } result) return null;
                    results.Add(result.ToString());
                    if (result.Pressed) times.Add(Clock.Now);
                    waiting = false;
                    if (++i >= n) return Summary();
                }
                Dispatcher.RequestUi(client, new UiAction.QuickMove("INVENTORY", new UnityEngine.Vector2Int(x + i % 4, y + i / 4)));
                waiting = true;
                return null;
            },
        };

        string Summary()
        {
            int peak = 0;
            for (int a = 0; a < times.Count; a++) peak = Math.Max(peak, times.Count(t => t >= times[a] && t < times[a] + 1f));
            float span = times.Count > 1 ? times[^1] - times[0] : 0f;
            return $"ok ui quickmove {x} {y} {n}: {times.Count} pressed, peak {peak}/s, over {span.ToString("F2", s_inv)} s; "
                + string.Join(", ", results.GroupBy(r => r).Select(g => $"{g.Key} x{g.Count()}"));
        }
    }

    private static CommandResult EditsList()
    {
        var lines = new List<string> { $"ok edits: {Edits.Count} active" };
        lines.AddRange(Edits.Lines());
        return new CommandResult(DevOutput.Reply("edits", lines));
    }
}
