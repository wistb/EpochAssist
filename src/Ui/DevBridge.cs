using EpochAssist.Core;
using MelonLoader.Utils;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// Dev builds only: runs commands written to UserData/EpochAssist/dev/commands.txt (one per line, one per frame)
/// and reports to result.txt, ending with "done". tools/shot.sh drives it.
///   menu open [feature id with a page]|settings [tab]   menu close   menu search [text]
///   hud panel open|close   set [setting id] [value]   state   facts   compat   probe [type] [filter]   find [text]   fonts   wait [frames]   shot full|menu|hud [name]
///   actions   run [action]   status   hotkeys
///   theme list|set [name]|reload|problems|check   resources   toast [text]   toasts   travel [scene]   focus   draw test on|off
/// Screenshots: Unity saves the full screen, then the mod crops it and writes shots/[name].png.
/// </summary>
internal static class DevBridge
{
    private const float PollSeconds = 0.25f;
    private const float ShotTimeoutSeconds = 5f;
    private const int SettleFrames = 2;

    private static readonly Queue<string> s_queue = new();
    private static readonly List<string> s_results = new();
    private static float s_nextPollAt;
    private static int s_waitFrames;
    private static Capture? s_capture;
    private static bool s_batchActive;
    private static bool s_drawTest;

    private sealed class Capture
    {
        public string Name = string.Empty;
        public string FullPath = string.Empty;
        public Rect? Crop;
        public float Deadline;
        public long LastSize = -1;
    }

    private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "dev");
    private static string ShotDir => Path.Combine(Dir, "shots");

    public static void Update()
    {
        if (s_capture != null)
        {
            ContinueCapture(s_capture);
            return;
        }
        if (s_waitFrames > 0)
        {
            s_waitFrames--;
            return;
        }
        if (s_queue.Count > 0)
        {
            string command = s_queue.Dequeue();
            try
            {
                Execute(command);
            }
            catch (Exception e)
            {
                Report($"error {command}: {e.Message}");
            }
            return;
        }
        // The batch is over once its last command, capture and settle frames are all through.
        if (s_batchActive)
        {
            Finish();
            return;
        }
        if (Time.unscaledTime < s_nextPollAt) return;
        s_nextPollAt = Time.unscaledTime + PollSeconds;
        Poll();
    }

    public static void Reset()
    {
        s_queue.Clear();
        s_results.Clear();
        s_capture = null;
        s_waitFrames = 0;
        s_batchActive = false;
        s_drawTest = false;
    }

    /// <summary>The draw test pattern around the player: a circle, a line, a label and a marker.</summary>
    public static void OnGUI()
    {
        if (!s_drawTest || Event.current.type != EventType.Repaint || Player.Transform is not Transform t) return;
        Vector3 p = t.position;
        Draw.WorldCircle(p, 3f, Theme.Accent, 2f);
        Draw.WorldLine(p, p + new Vector3(3f, 0f, 0f), Theme.Pause, 2f);
        Draw.WorldMarker(p + new Vector3(0f, 2.5f, 0f), Theme.Good, 14f);
        Draw.WorldLabel(p + new Vector3(0f, 3.5f, 0f), "Draw test", Theme.Text);
    }

    private static void Poll()
    {
        string file = Path.Combine(Dir, "commands.txt");
        if (!File.Exists(file)) return;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
            File.Delete(file);
        }
        catch (IOException)
        {
            return;
        }
        s_results.Clear();
        foreach (string line in lines)
        {
            if (line.Trim().Length > 0) s_queue.Enqueue(line.Trim());
        }
        s_batchActive = true;
    }

    private static void Execute(string command)
    {
        string[] p = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = p[0].ToLowerInvariant();
        string Arg(int i) => i < p.Length ? p[i].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            case "menu" when Arg(1) == "open":
                if (!Menu.PageNames.Contains(Arg(2)))
                {
                    Report($"error unknown page {Arg(2)}");
                    break;
                }
                string opened = Actions.Run($"menu.page.{Arg(2)}", "dev bridge");
                if (Arg(2) == Menu.SettingsPage && int.TryParse(Arg(3), out int tab)) Menu.SetSettingsTab(tab);
                s_waitFrames = SettleFrames;
                Report(opened.StartsWith("ok") ? $"ok {command}" : opened);
                break;
            case "menu" when Arg(1) == "close":
                string closed = Actions.Run("menu.close", "dev bridge");
                s_waitFrames = SettleFrames;
                Report(closed.StartsWith("ok") ? $"ok {command}" : closed);
                break;
            case "menu" when Arg(1) == "search":
                Menu.SetSearch(string.Join(' ', p.Skip(2)));
                s_waitFrames = SettleFrames;
                Report($"ok {command}");
                break;
            case "hud" when Arg(1) == "panel":
                string panel = Hud.PanelOpen == (Arg(2) == "open") ? "ok" : Actions.Run("hud.togglePanel", "dev bridge");
                s_waitFrames = SettleFrames;
                Report(panel.StartsWith("ok") ? $"ok {command}" : panel);
                break;
            case "set":
                Report(Set(Arg(1), string.Join(' ', p.Skip(2))) ? $"ok {command}" : $"error {command}: no such setting or bad value");
                s_waitFrames = SettleFrames;
                break;
            case "actions":
                Report("ok actions: " + string.Join(", ", Actions.All.Select(a =>
                    $"{a.Name} ({(a.IsToggle ? "toggle" : "run")}{(Actions.StateOf(a) is bool on ? on ? ", on" : ", off" : string.Empty)})")));
                break;
            case "run":
                // Action names keep their case (hud.togglePanel), so the argument isn't lowered.
                Report(Actions.Run(p.Length > 1 ? p[1] : string.Empty, "dev bridge"));
                s_waitFrames = SettleFrames;
                break;
            case "probe":
                Report(Probe(p.Length > 1 ? p[1] : string.Empty, p.Length > 2 ? p[2] : null));
                break;
            case "find":
                Report(Find(p.Length > 1 ? p[1] : string.Empty));
                break;
            case "state":
                Report($"ok state: build {Runtime.BuildId}, scene '{GameState.Scene}' {GameState.Kind}, in game {GameState.InGame}, online {GameState.Online?.ToString() ?? "?"}, "
                    + $"alive {GameState.PlayerAlive} (health {(GameState.Health is { } h ? $"{h.Current:F0}/{h.Max:F0}" : "none")}), typing {GameState.Typing}, "
                    + $"loading {(GameState.CanReadLoading ? GameState.Loading.ToString() : "?")}, gameMenu {(GameState.CanReadPauseMenu ? GameState.GamePaused.ToString() : "?")}; "
                    + MovementState() + $"; theme {ThemeText()}; features "
                    + string.Join(", ", FeatureHost.All.Select(f => $"{f.Id}={f.State}/{f.Status.State.ToString().ToLowerInvariant()}" + (f.LastError != null ? $" ({f.LastError})" : string.Empty))));
                break;
            case "hotkeys":
                Report("ok hotkeys: " + HotkeysText());
                break;
            case "theme":
                Report(ThemeCommand(Arg(1), string.Join(' ', p.Skip(2))));
                s_waitFrames = SettleFrames;
                break;
            case "draw" when Arg(1) == "test":
                s_drawTest = Arg(2) == "on";
                s_waitFrames = SettleFrames;
                Report($"ok draw test {(s_drawTest ? "on" : "off")}");
                break;
            case "focus":
                string? focused = Focus.Current;
                Report("ok focus: " + (focused == null ? "none" : focused.Contains('/') ? focused : $"{Menu.Current}/{focused}"));
                break;
            case "toast":
                // The text keeps its case.
                Toasts.Show("dev", string.Join(' ', p.Skip(1)), ToastKind.Info);
                Report("ok toast");
                break;
            case "toasts":
                var shown = Toasts.Visible;
                Report($"ok toasts: {shown.Count} shown" + (shown.Count > 0 ? ": " : string.Empty)
                    + string.Join(", ", shown.Select(t => $"\"{t.Text}\" ({Time.unscaledTime - t.ShownAt:F1} s)")));
                break;
            case "resources":
                // All textures in the game too, so textures leaked by an earlier load show up across reloads.
                Report($"ok resources: theme textures {Theme.TextureCount}, draw textures {Draw.ResourceCount}, "
                    + $"unity textures {Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Texture2D>()).Length}");
                break;
            case "status":
                Report("ok status: " + string.Join(" | ", FeatureHost.All.Select(FeatureStatusText)));
                break;
            case "facts":
                Report("ok facts: " + Facts());
                break;
            case "compat":
                var report = FeatureHost.All.Select(f => (f.Id, Missing: Compat.Missing(f))).ToList();
                report.Add(("selftest", Compat.Missing(new CompatProbeFeature())));
                Report("ok compat: " + string.Join("; ", report.Select(r => $"{r.Id}: {(r.Missing.Count == 0 ? "ok" : string.Join(", ", r.Missing.Select(m => m.Group == null ? m.Text : $"{m.Text} [optional {m.Group}]")))}")));
                break;
            case "move":
                // move <x> <y> [held]: feeds the game a keyboard movement direction, as a held key would.
                var mpm = Game.Input?.movementProcessor;
                if (!Game.Alive(mpm) || p.Length < 3
                    || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mx)
                    || !float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float my))
                {
                    Report($"error {command}: usage move <x> <y> [true|false]");
                    break;
                }
                mpm!.ProcessKeyboardMovement(new Vector2(mx, my), Arg(3) != "false");
                Report($"ok {command}");
                break;
            case "stopmove":
                var mps = Game.Input?.movementProcessor;
                if (Game.Alive(mps)) mps!.StopActiveMovement();
                Report(Game.Alive(mps) ? $"ok {command}" : $"error {command}: no movement processor");
                break;
            case "travel":
                Report(Travel(p.Length > 1 ? p[1] : string.Empty));
                break;
            case "fonts":
                var fonts = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Font>());
                Report($"ok fonts: {string.Join(", ", fonts.Select(f => f.name).Distinct())}");
                break;
            case "wait":
                s_waitFrames = int.TryParse(Arg(1), out int frames) ? frames : 30;
                Report($"ok {command}");
                break;
            case "shot":
                StartCapture(Arg(1), p.Length > 2 ? p[2] : Arg(1));
                break;
            default:
                Report($"error unknown command: {command}");
                break;
        }
    }

    private static string ThemeText()
    {
        var theme = Themes.Resolve(Config.Theme.Value);
        return theme.Fallback ? $"{Config.Theme.Value} (fallback {theme.Name})" : theme.Name;
    }

    // theme list | set <name> | reload | problems; a name keeps its case and may have spaces.
    private static string ThemeCommand(string verb, string name)
    {
        switch (verb)
        {
            case "list":
                string selected = Themes.Resolve(Config.Theme.Value).Name;
                return "ok themes: " + string.Join(", ", Themes.All.Select(t =>
                    $"{t.Name} ({(t.IsPreset ? "preset" : "file")}{(t.Name == selected ? ", selected" : string.Empty)}{(t.Problems.Count > 0 ? $", problems {t.Problems.Count}" : string.Empty)})"));
            case "set":
                if (Themes.Find(name) is not { } theme) return $"error unknown theme {name}";
                Config.Theme.Value = theme.Name;
                return $"ok theme {theme.Name}";
            case "reload":
                Themes.Rescan();
                return $"ok themes reloaded: {Themes.FileCount} files, {Themes.Problems.Count} problems";
            case "problems":
                return "ok theme problems: " + (Themes.Problems.Count > 0 ? string.Join("; ", Themes.Problems) : "none");
            case "check":
                return "ok theme check: " + string.Join(" | ", ThemeValues.Presets.Select(p => ThemeCheck(p.Name, p.Values)));
            default:
                return "error usage: theme list|set <name>|reload|problems";
        }
    }

    // For one theme: each state colour's lightness (CIE L*), Active's smallest distance to the others (>= 15),
    // each state colour's contrast on the card (>= 3:1, Off >= 1.6:1) and text and dim text on the card (>= 4.5:1).
    // A see-through card is taken over the window colour.
    private static string ThemeCheck(string name, ThemeValues v)
    {
        Color card = Color.Lerp(v.Window, new Color(v.Card.r, v.Card.g, v.Card.b, 1f), v.Card.a);
        var states = new (string Name, Color Color)[]
        {
            ("active", v.StateActive), ("waiting", v.StateWaiting), ("holding", v.StateHolding), ("paused", v.StatePaused), ("off", v.StateOff),
        };
        var failures = new List<string>();
        float active = Lightness(v.StateActive);
        float gap = states.Skip(1).Min(s => Mathf.Abs(Lightness(s.Color) - active));
        if (gap < 15f) failures.Add($"active gap {gap:F0}");
        foreach (var (state, color) in states)
        {
            float needed = state == "off" ? 1.6f : 3f;
            if (Contrast(color, card) < needed) failures.Add($"{state} contrast {Contrast(color, card):F1}");
        }
        if (Contrast(v.Text, card) < 4.5f) failures.Add($"text contrast {Contrast(v.Text, card):F1}");
        if (Contrast(v.Dim, card) < 4.5f) failures.Add($"dim contrast {Contrast(v.Dim, card):F1}");
        string lightness = string.Join(" ", states.Select(s => $"{s.Name} {Lightness(s.Color):F0}"));
        string contrasts = string.Join(" ", states.Select(s => $"{s.Name} {Contrast(s.Color, card):F1}"));
        return $"{name} {(failures.Count == 0 ? "pass" : "fail (" + string.Join(", ", failures) + ")")}: L* {lightness}; active gap {gap:F0}; "
            + $"contrast {contrasts}, text {Contrast(v.Text, card):F1}, dim {Contrast(v.Dim, card):F1}";
    }

    private static float Luminance(Color c)
    {
        static float Linear(float x) => x <= 0.04045f ? x / 12.92f : Mathf.Pow((x + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * Linear(c.r) + 0.7152f * Linear(c.g) + 0.0722f * Linear(c.b);
    }

    private static float Lightness(Color c)
    {
        float y = Luminance(c);
        return y > 216f / 24389f ? 116f * Mathf.Pow(y, 1f / 3f) - 16f : y * 24389f / 27f;
    }

    private static float Contrast(Color a, Color b)
    {
        float la = Luminance(a) + 0.05f, lb = Luminance(b) + 0.05f;
        return Mathf.Max(la, lb) / Mathf.Min(la, lb);
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
        string slotText = string.Join(", ", slots), ignored = string.Join(",", Hotkeys.Ignored);
        return $"{string.Join(", ", bindings)}; slot chords {(slotText.Length > 0 ? slotText : "none")}; "
            + $"capture {Hotkeys.Capturing?.Id ?? "none"}; ignored {(ignored.Length > 0 ? ignored : "none")}";
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
        Type? type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName) ?? a.GetType("Il2Cpp." + typeName))
            .FirstOrDefault(t => t != null);
        if (type == null) return $"error probe: no type {typeName}";

        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        var members = type.GetMethods(all).Where(m => !m.IsSpecialName)
            .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name))})")
            .Concat(type.GetProperties(all).Select(x => $"prop {x.PropertyType.Name} {x.Name}"))
            .Where(m => filter == null || m.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct().OrderBy(m => m).ToList();

        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, "probe.txt"), members.Prepend(type.FullName ?? typeName));
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
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, "find.txt"), hits.Distinct().OrderBy(h => h));
        return $"ok find {text}: {hits.Count} methods in find.txt";
    }

    // set <id> <value>, where id is a setting's Id, optionally prefixed by its group id and a dot.
    private static bool Set(string id, string value)
    {
        var setting = SettingsRegistry.Groups.SelectMany(g => g.All)
            .FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || $"{s.Section.Group.Id}.{s.Id}".Equals(id, StringComparison.OrdinalIgnoreCase));
        switch (setting)
        {
            case Toggle t when bool.TryParse(value, out bool b): t.Value = b; return true;
            case Number n when float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f): n.Value = n.Snap(f); return true;
            case Choice<int> c when int.TryParse(value, out int i) && c.Options.Any(o => o.Value == i): c.Value = i; return true;
            case Key k when Enum.TryParse(value, true, out KeyCode key): k.Value = key; return true;
            case Hotkey h when Chord.TryParse(value, h.Kind, out var chord) && (h.Clearable || !chord.IsEmpty): h.Value = chord.Text; return true;
            case Choice<KeyCode> c when Enum.TryParse(value, true, out KeyCode key) && c.Options.Any(o => o.Value == key): c.Value = key; return true;
            case Pick pick when value.Length > 0: pick.Value = value; return true;
            case ColorSetting color when value.Length == 0 || ColorSetting.TryParse(value.ToUpperInvariant(), out _): color.Value = value.ToUpperInvariant(); return true;
            default: return false;
        }
    }

    private static void StartCapture(string target, string name)
    {
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
        ScreenCapture.CaptureScreenshot(full, 1);
        s_capture = new Capture { Name = SafeName(name), FullPath = full, Crop = crop, Deadline = Time.unscaledTime + ShotTimeoutSeconds };
    }

    // Unity writes the screenshot at the end of a later frame; wait until the file exists and stops growing.
    private static void ContinueCapture(Capture capture)
    {
        if (Time.unscaledTime > capture.Deadline)
        {
            s_capture = null;
            Report($"error shot {capture.Name}: the screenshot was not written");
            return;
        }
        if (!File.Exists(capture.FullPath)) return;
        long size = new FileInfo(capture.FullPath).Length;
        if (size == 0 || size != capture.LastSize)
        {
            capture.LastSize = size;
            return;
        }

        s_capture = null;
        try
        {
            string output = Path.Combine(ShotDir, capture.Name + ".png");
            Vector2Int dims = capture.Crop is Rect crop ? CropTo(capture.FullPath, crop, output) : CopyFull(capture.FullPath, output);
            Report($"shot {capture.Name}.png {dims.x}x{dims.y}");
        }
        catch (Exception e)
        {
            Report($"error shot {capture.Name}: {e.Message}");
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

    private static void Report(string line)
    {
        s_results.Add(line);
        Log.Info("dev: " + line);
    }

    private static void Finish()
    {
        s_batchActive = false;
        s_results.Add("done");
        try
        {
            File.WriteAllLines(Path.Combine(Dir, "result.txt"), s_results);
        }
        catch (IOException e)
        {
            Log.Warn($"dev: could not write result.txt: {e.Message}");
        }
        s_results.Clear();
    }
}

internal sealed class DevBridgeFeature : Feature
{
    public override string Id => "dev";
    public override string Name => "Dev bridge";
    // The draw test pattern draws in the world layer.
    public override GuiLayer Layer => GuiLayer.World;

    public override void Update() => DevBridge.Update();
    public override void OnGUI() => DevBridge.OnGUI();
    public override void Shutdown() => DevBridge.Reset();
}

// Requires things the game doesn't have, so `compat` shows that missing members and patch targets are caught.
internal sealed class CompatProbeFeature : Feature
{
    public override string Id => "selftest";
    public override string Name => "Compat self-test";
    public override Requirement[] Requirements => new[]
    {
        Need<Il2Cpp.CameraManager>("noSuchMember"),
        Need<Il2Cpp.CameraManager>(nameof(Il2Cpp.CameraManager.targetZoom), "interop field"),
        Need<Il2Cpp.CameraManager>("noSuchField", "optional"),
    };
    public override Type[] PatchClasses => new[] { typeof(MissingTargetPatch), typeof(MissingOptionalPatch) };
    public override IReadOnlyDictionary<Type, string> PatchGroups { get; } =
        new Dictionary<Type, string> { [typeof(MissingOptionalPatch)] = "optional" };

    [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.CameraManager), "NoSuchMethod")]
    private static class MissingTargetPatch
    {
        private static void Postfix() { }
    }

    [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.CameraManager), "NoSuchOptionalMethod")]
    private static class MissingOptionalPatch
    {
        private static void Postfix() { }
    }
}
