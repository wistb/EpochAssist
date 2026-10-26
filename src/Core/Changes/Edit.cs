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
        Owner.Record.Edits.Remove(this);
        Edits.Removed(this);
        Edits.Run(Owner, "edit " + Target.Name, () => _stack.Take(this));
    }
}
