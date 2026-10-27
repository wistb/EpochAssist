using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Dev;

internal static partial class DevCommands
{
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
            Read("timeScale", () => $"{Time.timeScale:F2}, focused {Application.isFocused}, runInBackground {Application.runInBackground}"),
            Read("input:", () => $"{VirtualInput.Describe()}, focused real {InputState.RealFocused}"),
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
}
