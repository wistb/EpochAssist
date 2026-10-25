using System.Globalization;

namespace EpochAssist.Core;

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
