using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The release commands that read the UI or the game layer: state, status, hotkeys and theme (owner host).
internal static class UiCommands
{
    private const int SettleFrames = 2;

    public static void Register()
    {
        Add(new Command("state", "state", "Shows the build, scene, mode, player, movement, theme and every feature's state.", CommandMark.Release, "host", State));
        Add(new Command("status", "status", "Shows what each feature is doing, with every pause rule for automation.", CommandMark.Release, "host",
            _ => CommandResult.Ok("status: " + string.Join(" | ", FeatureHost.All.Select(FeatureStatusText)))));
        Add(new Command("hotkeys", "hotkeys", "Lists every key binding with its conflicts, and the slot-toggle chords.", CommandMark.Release, "host",
            _ => CommandResult.Ok("hotkeys: " + HotkeysText())));
        Add(new Command("theme", "theme list|reload|problems", "Lists the themes, reads theme files again, or shows their problems.", CommandMark.Release, "host",
            call => new CommandResult(ThemeCommand(call.Lower(1))) { Settle = SettleFrames })
        {
            DevVerbs = new[] { "set", "check" },
        });
        MenuCommands.Register();
    }

    private static void Add(Command command) => Commands.Register(command);

    private static CommandResult State(CommandCall call) => CommandResult.Ok(
        $"state: build {Runtime.BuildId}, scene '{GameState.Scene}' {GameState.Kind}, in game {GameState.InGame}, online {GameState.Online?.ToString() ?? "?"}, "
        + $"alive {GameState.PlayerAlive} (health {(GameState.Health is { } h ? $"{h.Current:F0}/{h.Max:F0}" : "none")}), typing {GameState.Typing}, "
        + $"loading {(GameState.CanReadLoading ? GameState.Loading.ToString() : "?")}, gameMenu {(GameState.CanReadPauseMenu ? GameState.GamePaused.ToString() : "?")}; "
        + MovementState() + $"; theme {ThemeText()}; features "
        + string.Join(", ", FeatureHost.All.Select(f => $"{f.Id}={f.State}/{f.Status.State.ToString().ToLowerInvariant()}" + (f.LastError != null ? $" ({f.LastError})" : string.Empty))));

    private static string ThemeText()
    {
        var theme = Themes.Resolve(LookSettings.Theme.Value);
        return theme.Fallback ? $"{LookSettings.Theme.Value} (fallback {theme.Name})" : theme.Name;
    }

    // theme list | reload | problems. The dev verbs set and check are in src/Dev; the registry answers them as unknown
    // outside the dev loader, and release builds don't have them.
    private static string ThemeCommand(string verb)
    {
        switch (verb)
        {
            case "list":
                string selected = Themes.Resolve(LookSettings.Theme.Value).Name;
                return "ok themes: " + string.Join(", ", Themes.All.Select(t =>
                    $"{t.Name} ({(t.IsPreset ? "preset" : "file")}{(t.Name == selected ? ", selected" : string.Empty)}{(t.Problems.Count > 0 ? $", problems {t.Problems.Count}" : string.Empty)})"));
            case "reload":
                Themes.Rescan();
                return $"ok themes reloaded: {Themes.FileCount} files, {Themes.Problems.Count} problems";
            case "problems":
                return "ok theme problems: " + (Themes.Problems.Count > 0 ? string.Join("; ", Themes.Problems) : "none");
            default:
                return Runtime.Hosted ? "error usage: theme list|set <name>|reload|problems" : "error usage: theme list|reload|problems";
        }
    }

    // Every binding with its kind, scope and conflicts, the slot-toggle chords, capture and focus-ignored keys.
    private static string HotkeysText()
    {
        var bindings = Hotkeys.Bindings.Select(b =>
        {
            var conflicts = HotkeyConflicts.For(b);
            return $"{b.Id}={b.Value} ({b.Kind.ToString().ToLowerInvariant()}, {b.Scope.ToString().ToLowerInvariant()})"
                + (conflicts.Count > 0 ? $" [{string.Join("; ", conflicts)}]" : string.Empty);
        });
        var slots = Hotkeys.SlotChords().Select(c => $"slot{c.Slot + 1}={c.Chord.Text}");
        // Keys that condition entries in the profile in use read (held, or toggled by a press).
        var conditionKeys = ConditionKeys.All.Select(k => $"{k.Label}={(k.Chord.Length > 0 ? k.Chord : "none")} ({(k.Toggle ? "toggle" : "held")}"
            + (k.Toggle && ConditionToggles.IsOn(k.Chord) ? ", on" : string.Empty) + ")");
        string slotText = string.Join(", ", slots), conditionText = string.Join(", ", conditionKeys), ignored = string.Join(",", Hotkeys.Ignored);
        string capture = Hotkeys.Capturing?.Id ?? Hotkeys.CapturingTarget?.Label ?? "none";
        return $"{string.Join(", ", bindings)}; slot chords {(slotText.Length > 0 ? slotText : "none")}; "
            + $"condition keys {(conditionText.Length > 0 ? conditionText : "none")}; capture {capture}; ignored {(ignored.Length > 0 ? ignored : "none")}";
    }

    // <id>: <state> (<detail>); for automation clients also every rule, the held slots and press times.
    private static string FeatureStatusText(Feature feature)
    {
        FeatureStatus status = feature.Status;
        string text = $"{feature.Id}: {status.State.ToString().ToLowerInvariant()}" + (status.Detail.Length > 0 ? $" ({status.Detail})" : string.Empty);
        var client = Dispatcher.Clients.FirstOrDefault(c => c.Feature == feature);
        if (client == null) return text;

        static string Time(float at) => at > 0f ? $"{at:F2}s" : "-";
        var held = Enumerable.Range(0, Dispatcher.SlotCount).Where(client.Holds).Select(i => (i + 1).ToString()).ToList();
        string rules = string.Join(", ", PauseRules.States(client).Select(r => $"{r.Rule.Name}={(r.Active is bool on ? on ? "active" : "inactive" : "unknown")}"));
        string presses = string.Join(", ", Enumerable.Range(0, Dispatcher.SlotCount)
            .Select(i => $"{i + 1}@{Time(Dispatcher.LastPressAt[i])}/f{Dispatcher.LastPressFrame[i]}"));
        string manual = string.Join(", ", Enumerable.Range(0, Dispatcher.SlotCount).Select(i => $"{i + 1}@{Time(Dispatcher.LastManualPressAt[i])}"));
        string slots = status.Slots is { } on ? string.Concat(on.Select(b => b ? '1' : '0')) : "-";
        return $"{text}; rules {rules}; slots {slots}; held {(held.Count > 0 ? string.Join(",", held) : "none")}; "
            + $"pressed {presses}; manual {manual}; now {UnityEngine.Time.unscaledTime:F2}s/f{UnityEngine.Time.frameCount}";
    }

    // Outside a game scene the movement processor can be alive while its getters throw; report that, not the error.
    private static string MovementState()
    {
        try
        {
            return MovementStateUnsafe();
        }
        catch (Exception e)
        {
            return $"movement: unreadable ({e.GetType().Name})";
        }
    }

    private static string MovementStateUnsafe()
    {
        var input = Game.Input;
        if (input == null) return "movement: no input manager";
        var mp = input.movementProcessor;
        string pos = Player.Transform is Transform t ? $"pos ({t.position.x:F2}, {t.position.z:F2}), " : string.Empty;
        return $"movement: {pos}focused {Application.isFocused}, ours {InputGate.Blocking}, blocked {input.forceDisableInput}, mouseMoveBlocked {input.blockMouseMovementInput}, input {input.input}, lastInput {input.lastInput}, kbHeld {input.isKeyboardMovementButtonHeld}, "
            + (Game.Alive(mp) ? $"moving {mp.IsMoving}, kbActive {mp.IsKeyboardMovementActive}" : "no movement processor");
    }
}
