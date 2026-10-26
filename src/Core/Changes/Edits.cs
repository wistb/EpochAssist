namespace EpochAssist.Core;

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
        owner.Record.Edits.Add(edit);
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
        for (int i = owner.Record.Edits.Count - 1; i >= 0; i--)
        {
            if (i < owner.Record.Edits.Count) owner.Record.Edits[i].Remove();
        }
        owner.Record.Edits.Clear();
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
        foreach (var edit in s_active) edit.Owner.Record.Edits.Clear();
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
