using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MelonLoader.Utils;

namespace EpochAssist.Core;

/// <summary>
/// Where a game value lives and how to change it: Find gives the objects it covers now (a singleton, every root
/// canvas, an actor's renderers), Read and Write a plain value, or Apply and Restore for a change that isn't one value
/// (a component added, a property block). The registry calls Find each pass and never keeps objects across scenes.
/// </summary>
internal sealed class EditTarget<T>
{
    /// <summary>Stable id, shown in Diagnostics and kept in edits.json; edits on targets of the same name share one stack.</summary>
    public string Name { get; init; } = string.Empty;
    public Func<IEnumerable<UnityEngine.Object>> Find { get; init; } = null!;
    /// <summary>The object's value now; the original on first sight. Optional with Apply/Restore (no drift check then).</summary>
    public Func<UnityEngine.Object, T>? Read { get; init; }
    public Action<UnityEngine.Object, T>? Write { get; init; }
    /// <summary>With Restore, instead of Write.</summary>
    public Action<UnityEngine.Object, T>? Apply { get; init; }
    public Action<UnityEngine.Object, T>? Restore { get; init; }
    /// <summary>Equality for the drift check: Equals by default, floats within 1e-4.</summary>
    public Func<T, T, bool>? Same { get; init; }
    /// <summary>How a value is shown in Diagnostics and the dev command; invariant text by default.</summary>
    public Func<T, string>? Show { get; init; }
    /// <summary>The game saves the value: originals go to edits.json until restored. Only targets in EditTargets.</summary>
    public bool Saved { get; init; }
    /// <summary>The objects belong to a scene and are rebuilt with it; false for DontDestroyOnLoad objects.</summary>
    public bool Scene { get; init; } = true;
}

/// <summary>One owner's request to hold a target at a value. Removed with its owner (stop, fault, unavailable), on
/// reload and on quit; the owner never restores by hand.</summary>
internal abstract class Edit
{
    private protected Edit(Feature owner) => Owner = owner;

    public Feature Owner { get; }
    public abstract string TargetName { get; }
    /// <summary>This edit's value is the one written (top of the target's stack).</summary>
    public abstract bool OnTop { get; }
    /// <summary>Any object's value was changed by something else since we wrote it.</summary>
    public abstract bool Drifted { get; }
    public abstract int ObjectCount { get; }
    /// <summary>The first object's original, for display; "-" before any object was seen.</summary>
    public abstract string OriginalText { get; }
    public abstract string ValueText { get; }
    /// <summary>The first object's value at the last pass.</summary>
    public abstract string CurrentText { get; }
    /// <summary>"stack 2: crashtest=400, dev=300" when more than one edit holds the target, else empty.</summary>
    public abstract string StackText { get; }
    public bool Removed { get; private protected set; }

    /// <summary>Restores (or writes the next edit's value when this one is on top); then the edit does nothing.</summary>
    public abstract void Remove();

    // Shutdown: the stack restores its objects itself.
    internal void MarkRemoved() => Removed = true;

    /// <summary>One line, as Diagnostics and the dev command show it.</summary>
    public string Describe()
    {
        string objects = ObjectCount == 1 ? "1 object" : $"{ObjectCount} objects";
        string line = $"{Owner.Id,-10} {TargetName,-24} {OriginalText} -> {ValueText} (now {CurrentText})  {objects}";
        if (StackText.Length > 0) line += "  " + StackText;
        if (Drifted) line += $"  drifted (now {CurrentText})";
        return line;
    }
}

internal sealed class Edit<T> : Edit
{
    private readonly EditStack<T> _stack;

    internal Edit(Feature owner, EditTarget<T> target, T value, EditStack<T> stack) : base(owner)
    {
        Target = target;
        Value = value;
        _stack = stack;
    }

    public EditTarget<T> Target { get; }
    public T Value { get; private set; }

    public override string TargetName => Target.Name;
    public override bool OnTop => !Removed && _stack.Top == this;
    public override bool Drifted => OnTop && _stack.AnyDrifted;
    public override int ObjectCount => Removed ? 0 : _stack.LiveCount;
    public override string OriginalText => Removed ? "-" : _stack.FirstOriginal;
    public override string ValueText => _stack.Show(Value);
    public override string CurrentText => Removed ? "-" : _stack.FirstCurrent;
    public override string StackText => Removed ? string.Empty : _stack.Describe();

    /// <summary>Changes our value; written at once when this edit is on top.</summary>
    public void Set(T value)
    {
        if (Removed) return;
        Value = value;
        if (OnTop) Edits.Run(Owner, "edit " + Target.Name, () => _stack.WriteAll(value));
    }

    public override void Remove()
    {
        if (Removed) return;
        Removed = true;
        Owner.Edits.Remove(this);
        Edits.Removed(this);
        Edits.Run(Owner, "edit " + Target.Name, () => _stack.Take(this));
    }
}

/// <summary>The edits on one target name, in apply order, and the objects they hold: originals are kept here, so the
/// top's value is written over the object's own value, and removing the last edit restores that.</summary>
internal abstract class EditStack
{
    public abstract string Name { get; }
    public abstract bool Empty { get; }
    public abstract Edit? TopEdit { get; }
    internal abstract void Pass(bool sceneChanged);
    internal abstract void RestoreAll();
}

internal sealed class EditStack<T> : EditStack
{
    private sealed class Entry
    {
        public UnityEngine.Object Obj = null!;
        public IntPtr Ptr;
        public T Original = default!;
        public T Written = default!;
        public T Current = default!;
        public bool Drifted;
    }

    private readonly List<Edit<T>> _edits = new();
    private readonly List<Entry> _entries = new();
    private readonly string _name;
    private readonly bool _saved;
    private bool _driftLogged;
    private bool _sceneChanged;
    // Made once, so a pass allocates no delegate.
    internal readonly Action PassAction;

    public EditStack(EditTarget<T> first)
    {
        _name = first.Name;
        _saved = first.Saved;
        PassAction = RunPass;
    }

    public override string Name => _name;
    public override bool Empty => _edits.Count == 0;
    public override Edit? TopEdit => Top;
    public Edit<T>? Top => _edits.Count > 0 ? _edits[^1] : null;
    private EditTarget<T> Target => Top!.Target;

    public bool AnyDrifted => _entries.Exists(e => e.Drifted);
    public int LiveCount => _entries.Count(e => Game.Alive(e.Obj));
    // Apply/Restore targets without Read have no value of their own to show.
    public string FirstOriginal => _entries.Count > 0 && Top?.Target.Read != null ? Show(_entries[0].Original) : "-";
    public string FirstCurrent => _entries.Count > 0 && Top?.Target.Read != null ? Show(_entries[0].Current) : Top != null && _entries.Count > 0 ? Show(Top.Value) : "-";

    public string Show(T value)
    {
        var show = Top?.Target.Show;
        if (show != null) return show(value);
        return value switch
        {
            null => "null",
            float f => f.ToString("0.####", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null",
        };
    }

    public string Describe() =>
        _edits.Count < 2 ? string.Empty : $"stack {_edits.Count}: {string.Join(", ", _edits.Select(e => $"{e.Owner.Id}={Show(e.Value)}"))}";

    internal void Push(Edit<T> edit) => _edits.Add(edit);

    internal override void Pass(bool sceneChanged)
    {
        if (Top is not { } top) return;
        _sceneChanged = sceneChanged;
        Edits.Run(top.Owner, "edits " + _name, PassAction);
    }

    private static bool Same(EditTarget<T> target, T a, T b)
    {
        if (target.Same is { } same) return same(a, b);
        if (a is float fa && b is float fb) return Math.Abs(fa - fb) <= 1e-4f;
        return EqualityComparer<T>.Default.Equals(a, b);
    }

    // Gone objects are forgotten first (and, after a scene change, objects of a scene target that aren't in a loaded
    // scene), so nothing is kept across scenes; then Find's objects are matched by pointer, which can't be reused by
    // another object while the entry's reference keeps this one from being collected.
    private void RunPass()
    {
        var target = Target;
        var top = Top!;
        bool sceneTarget = target.Scene && _sceneChanged;
        _entries.RemoveAll(e => !Game.Alive(e.Obj) || (sceneTarget && !InLoadedScene(e.Obj)));
        int known = _entries.Count;
        foreach (var obj in target.Find())
        {
            if (!Game.Alive(obj)) continue;
            IntPtr ptr = obj.Pointer;
            if (_entries.Exists(e => e.Ptr == ptr)) continue;
            T original = target.Read != null ? target.Read(obj) : default!;
            WriteOne(target, obj, original, top.Value, fresh: true);
            _entries.Add(new Entry { Obj = obj, Ptr = ptr, Original = original, Written = top.Value, Current = top.Value });
            if (_saved) EditsFile.Record(_name, Show(original), Show(top.Value));
        }
        // Objects seen before are checked for drift, also those Find no longer lists (a canvas we disabled).
        for (int i = 0; i < known; i++) CheckDrift(target, _entries[i]);
    }

    private void CheckDrift(EditTarget<T> target, Entry entry)
    {
        if (entry.Drifted || target.Read == null) return;
        T now = target.Read(entry.Obj);
        entry.Current = now;
        if (Same(target, now, entry.Written)) return;
        entry.Drifted = true;
        if (_driftLogged) return;
        _driftLogged = true;
        Log.WarnThrottled($"edits:drift:{_name}", $"Edit '{_name}' ({Top?.Owner.Id}): '{entry.Obj.name}' changed to {Show(now)} outside the registry; it is no longer written or restored", 30);
    }

    private static bool InLoadedScene(UnityEngine.Object obj)
    {
        var go = obj.TryCast<UnityEngine.GameObject>() ?? obj.TryCast<UnityEngine.Component>()?.gameObject;
        return go == null || go.scene.isLoaded;
    }

    // Apply targets change value by restoring the original first.
    private static void WriteOne(EditTarget<T> target, UnityEngine.Object obj, T original, T value, bool fresh)
    {
        if (target.Write != null)
        {
            target.Write(obj, value);
            return;
        }
        if (!fresh) target.Restore!(obj, original);
        target.Apply!(obj, value);
    }

    internal void WriteAll(T value)
    {
        var target = Target;
        foreach (var entry in _entries)
        {
            if (entry.Drifted || !Game.Alive(entry.Obj) || StillOurs(target, entry) == false) continue;
            WriteOne(target, entry.Obj, entry.Original, value, fresh: false);
            entry.Written = entry.Current = value;
        }
    }

    // A last read before writing over a value: false when something else changed it since the last pass.
    private bool? StillOurs(EditTarget<T> target, Entry entry)
    {
        if (target.Read == null) return null;
        T now = target.Read(entry.Obj);
        if (Same(target, now, entry.Written)) return true;
        entry.Drifted = true;
        entry.Current = now;
        return false;
    }

    // Removes an edit: on top, the next one's value is written, or with none left every object's original.
    internal void Take(Edit<T> edit)
    {
        bool wasTop = Top == edit;
        var target = edit.Target;
        _edits.Remove(edit);
        if (!wasTop) return;
        if (Top is { } next)
        {
            WriteAll(next.Value);
            return;
        }
        try
        {
            RestoreEntries(target);
        }
        finally
        {
            _entries.Clear();
            Edits.Forget(this);
        }
    }

    private void RestoreEntries(EditTarget<T> target)
    {
        foreach (var entry in _entries)
        {
            if (entry.Drifted || !Game.Alive(entry.Obj) || StillOurs(target, entry) == false) continue;
            if (target.Write != null) target.Write(entry.Obj, entry.Original);
            else target.Restore!(entry.Obj, entry.Original);
        }
        if (_saved) EditsFile.Remove(_name);
    }

    // Shutdown: every edit goes at once, the objects back to their originals.
    internal override void RestoreAll()
    {
        if (_edits.Count == 0) return;
        var target = Target;
        foreach (var edit in _edits.ToList()) edit.MarkRemoved();
        _edits.Clear();
        try
        {
            RestoreEntries(target);
        }
        finally
        {
            _entries.Clear();
        }
    }
}

/// <summary>
/// The registry of game edits, run by the host like the scheduler so restores work whatever state the features
/// are in. Passes re-apply edits to rebuilt objects and check drift; the host removes an owner's edits wherever it
/// drops its jobs, and restores everything at shutdown.
/// </summary>
internal static class Edits
{
    private const float PassSeconds = 0.5f;

    private static readonly List<EditStack> s_stacks = new();
    private static readonly List<Edit> s_active = new();
    private static Cooldown s_nextPass;
    private static int s_sceneStamp = int.MinValue;
    private static bool s_added;

    /// <summary>Active edits in apply order.</summary>
    public static IReadOnlyList<Edit> Active => s_active;
    public static int Count => s_active.Count;

    /// <summary>
    /// Puts the target at the value for the owner, on top of the target's stack, and runs that target's pass now (in the
    /// owner's guard). Objects not there yet are picked up by later passes. A target with the name of one holding another
    /// type, or without Find and Write or Apply+Restore, throws.
    /// </summary>
    public static Edit<T> Apply<T>(Feature owner, EditTarget<T> target, T value)
    {
        if (target.Name.Length == 0 || target.Find == null || (target.Write == null && (target.Apply == null || target.Restore == null)))
            throw new ArgumentException($"edit target '{target.Name}' needs a name, Find, and Write or Apply and Restore");
        var stack = s_stacks.Find(s => s.Name == target.Name);
        if (stack != null && stack is not EditStack<T>)
            throw new ArgumentException($"edit target '{target.Name}' already holds another type of value");
        if (target.Saved && (target.Read == null || EditTargets.Get(target.Name) == null))
        {
            Log.Error($"Edit target '{target.Name}' is marked saved but isn't declared in EditTargets (or has no Read); its original isn't kept on disk");
            target = new EditTarget<T>
            {
                Name = target.Name, Find = target.Find, Read = target.Read, Write = target.Write, Apply = target.Apply, Restore = target.Restore,
                Same = target.Same, Show = target.Show, Scene = target.Scene,
            };
        }
        // A value left from a killed game goes back before it could be taken for the original.
        if (target.Saved) EditsFile.RecoverNow(target.Name);
        var typed = (EditStack<T>?)stack;
        if (typed == null)
        {
            typed = new EditStack<T>(target);
            s_stacks.Add(typed);
        }
        var edit = new Edit<T>(owner, target, value, typed);
        typed.Push(edit);
        owner.Edits.Add(edit);
        s_active.Add(edit);
        s_added = true;
        if (typed.Top == edit && typed.LiveCount > 0) Run(owner, "edit " + target.Name, () => typed.WriteAll(value));
        typed.Pass(sceneChanged: false);
        return edit;
    }

    /// <summary>Each frame after every feature's LateUpdate: a pass over every target after a scene change, every 0.5 s
    /// and after an edit was added; never while loading. Nothing at all while no edit exists.</summary>
    internal static void Update()
    {
        if (EditsFile.Pending) EditsFile.Recover();
        if (s_stacks.Count == 0) return;
        int stamp = GameState.SceneChangedAt;
        bool sceneChanged = stamp != s_sceneStamp;
        if (!sceneChanged && !s_added && !s_nextPass.Ready) return;
        if (GameState.Loading) return;
        s_sceneStamp = stamp;
        s_added = false;
        s_nextPass.Start(PassSeconds);
        for (int i = 0; i < s_stacks.Count; i++) s_stacks[i].Pass(sceneChanged);
    }

    /// <summary>Removes every edit of the owner (the host, with Scheduler.RemoveOwner: stop, fault, unavailable).</summary>
    internal static void RemoveOwner(Feature owner)
    {
        // Latest first, so an owner's own stacked edits unwind in order.
        for (int i = owner.Edits.Count - 1; i >= 0; i--)
        {
            if (i < owner.Edits.Count) owner.Edits[i].Remove();
        }
        owner.Edits.Clear();
    }

    /// <summary>Shutdown (reload and quit), after every feature stopped: whatever is left goes back; errors are logged.</summary>
    internal static void RestoreAll()
    {
        foreach (var stack in s_stacks.ToList())
        {
            try
            {
                stack.RestoreAll();
            }
            catch (Exception e)
            {
                Log.Error($"Edit '{stack.Name}' restore failed: {e}");
            }
        }
        s_stacks.Clear();
        foreach (var edit in s_active) edit.Owner.Edits.Clear();
        s_active.Clear();
        EditsFile.Reset();
    }

    /// <summary>Init, after Config.Init: originals of saved values left by a killed game become recovery work.</summary>
    internal static void RecoverSaved() => EditsFile.Load();

    internal static void Forget(EditStack stack) => s_stacks.Remove(stack);

    internal static void Removed(Edit edit) => s_active.Remove(edit);

    // A running owner's guard counts the time and errors; otherwise (the host removing a stopped or faulted owner's
    // edits) errors are only logged.
    internal static void Run(Feature owner, string phase, Action action)
    {
        if (owner.State == FeatureState.Running)
        {
            FeatureHost.Guarded(owner, phase, action);
            return;
        }
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"edits:{phase}", $"{phase} for {owner.Name} failed: {e}", 30);
        }
    }

    /// <summary>The Diagnostics block: "Edits: N" and one line per edit.</summary>
    public static IEnumerable<string> Lines() => s_active.Select(e => e.Describe());
}

/// <summary>
/// Targets of values the game saves, declared here so they exist without their feature: recovery after a killed game
/// needs them before any feature runs. No release target is saved yet.
/// </summary>
internal static class EditTargets
{
    private static readonly Dictionary<string, object> s_saved = new();
    private static readonly Dictionary<string, Func<string, string, bool, bool?>> s_recover = new();

#if DEV
    /// <summary>
    /// Dev only, to show recovery after a killed game: the minimap zoom as the game saves it in PlayerPrefs on every zoom
    /// click (the minimap's live zoom is rewritten by the game each frame, so only the saved value is changed).
    /// </summary>
    public static readonly EditTarget<float> MinimapZoom = new()
    {
        Name = "minimap: zoom",
        Find = () => Game.Minimap is { } map ? new UnityEngine.Object[] { map } : Array.Empty<UnityEngine.Object>(),
        Read = _ => UnityEngine.PlayerPrefs.GetFloat(Il2Cpp.DMMapZoom.minimapZoomKey, -1f),
        Write = (_, v) =>
        {
            UnityEngine.PlayerPrefs.SetFloat(Il2Cpp.DMMapZoom.minimapZoomKey, v);
            UnityEngine.PlayerPrefs.Save();
        },
        Saved = true,
        Scene = false,
    };

    static EditTargets() => Declare(MinimapZoom);
#endif

    /// <summary>Declares a saved target by its name (dev code declares its test target).</summary>
    public static void Declare<T>(EditTarget<T> target)
    {
        s_saved[target.Name] = target;
        s_recover[target.Name] = (original, written, force) => EditsFile.RecoverTarget(target, original, written, force);
    }

    public static object? Get(string name) => s_saved.TryGetValue(name, out var target) ? target : null;

    /// <summary>Null while the target's object isn't there; true when restored, false when left as it is.</summary>
    internal static bool? Recover(string name, string original, string written, bool force) =>
        s_recover.TryGetValue(name, out var recover) ? recover(original, written, force) : false;
}

/// <summary>UserData/EpochAssist/edits.json: originals of saved values while our value is applied, and their recovery.</summary>
internal static class EditsFile
{
    private sealed record Saved(string Target, string Scene, string Original, string Written, string At);

    private static readonly List<Saved> s_entries = new();
    // Left from the last session: restored when the target's object exists and still holds our value.
    private static readonly List<Saved> s_recover = new();
    private static Cooldown s_nextTry;

    private static string FilePath => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "edits.json");

    public static bool Pending => s_recover.Count > 0;

    internal static void Load()
    {
        s_entries.Clear();
        s_recover.Clear();
        try
        {
            if (!File.Exists(FilePath)) return;
            var root = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            if (root?["entries"] is not JsonArray entries) return;
            foreach (var node in entries)
            {
                if (node is not JsonObject o) continue;
                string? target = (string?)o["target"], original = (string?)o["original"], written = (string?)o["written"];
                if (target == null || original == null || written == null) continue;
                var saved = new Saved(target, (string?)o["scene"] ?? string.Empty, original, written, (string?)o["at"] ?? string.Empty);
                s_entries.Add(saved);
                s_recover.Add(saved);
            }
            if (s_recover.Count > 0) Log.Info($"edits: {s_recover.Count} saved value{(s_recover.Count == 1 ? string.Empty : "s")} left by the last session, restored when found");
        }
        catch (Exception e)
        {
            Log.Error($"edits.json could not be read: {e.Message}");
        }
    }

    internal static void Reset()
    {
        s_recover.Clear();
        s_entries.Clear();
    }

    public static void Record(string target, string original, string written)
    {
        s_entries.RemoveAll(e => e.Target == target);
        s_entries.Add(new Saved(target, GameState.Scene, original, written, DateTime.Now.ToString("s", CultureInfo.InvariantCulture)));
        Save();
    }

    public static void Remove(string target)
    {
        if (s_entries.RemoveAll(e => e.Target == target) > 0) Save();
    }

    // Twice a second while something is left: an entry goes once its object is found (restored if it still holds
    // our value); unknown targets are dropped, entries never found stay for the next session.
    internal static void Recover()
    {
        if (!s_nextTry.Ready || GameState.Loading) return;
        s_nextTry.Start(0.5f);
        foreach (var saved in s_recover.ToList()) TryRecover(saved);
    }

    public static void RecoverNow(string target)
    {
        foreach (var saved in s_recover.Where(s => s.Target == target).ToList()) TryRecover(saved, force: true);
    }

    private static void TryRecover(Saved saved, bool force = false)
    {
        bool? done;
        try
        {
            done = EditTargets.Recover(saved.Target, saved.Original, saved.Written, force);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"edits:recover:{saved.Target}", $"edits: recovering '{saved.Target}' failed: {e.Message}", 30);
            done = false;
        }
        if (done == null) return;
        s_recover.Remove(saved);
        s_entries.Remove(saved);
        Save();
    }

    // Null while the object isn't there; true when restored, false when someone changed the value since.
    internal static bool? RecoverTarget<T>(EditTarget<T> target, string savedOriginal, string savedWritten, bool force)
    {
        var obj = target.Find().FirstOrDefault(o => Game.Alive(o));
        if (obj == null) return force ? false : null;
        T now = target.Read!(obj);
        T written = Parse<T>(savedWritten), original = Parse<T>(savedOriginal);
        bool ours = target.Same?.Invoke(now, written) ?? (now is float a && written is float b ? Math.Abs(a - b) <= 1e-4f : EqualityComparer<T>.Default.Equals(now, written));
        if (!ours)
        {
            Log.Info($"edits: '{target.Name}' was changed since the last session; left as it is");
            return false;
        }
        target.Write?.Invoke(obj, original);
        Log.Info($"edits: '{target.Name}' restored to {savedOriginal} (left at {savedWritten} by the last session)");
        return true;
    }

    private static T Parse<T>(string text) => (T)Convert.ChangeType(text, typeof(T), CultureInfo.InvariantCulture);

    private static void Save()
    {
        try
        {
            string path = FilePath;
            if (s_entries.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var entries = new JsonArray();
            foreach (var e in s_entries)
                entries.Add(new JsonObject { ["target"] = e.Target, ["scene"] = e.Scene, ["original"] = e.Original, ["written"] = e.Written, ["at"] = e.At });
            var root = new JsonObject { ["version"] = 1, ["entries"] = entries };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, true);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("edits:file", $"edits.json could not be written: {e.Message}", 30);
        }
    }
}
