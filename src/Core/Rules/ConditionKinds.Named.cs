namespace EpochAssist.Core;

/// <summary>
/// What a named-condition line asks of the named conditions: a result by id (true, false, or null with why not), the
/// name, the trees (for the enemy scan) and, for a failing line, its first failing inner line. The Macros feature
/// installs one while it runs.
/// </summary>
internal interface INamedResolver
{
    /// <summary>The named condition's result this frame, or null with the reason (missing, a loop).</summary>
    bool? Result(string id, out string? reason);
    /// <summary>The name, or null when there is no named condition with this id.</summary>
    string? NameOf(string id);
    /// <summary>A failing line's words: "Worth fighting false: weight 3 within 12 m (weight 3)", or "In danger true".</summary>
    string FailText(string id, bool wanted);
    /// <summary>The start tree and the ends tree (null when it has none), for walks that follow lines into them.</summary>
    ConditionList? StartOf(string id);
    ConditionList? EndsOf(string id);
    /// <summary>The named conditions a tree may add, in page order: all of them in a macro's or slot's tree; in a named
    /// condition's own trees, those that don't lead back to it (editing is its id).</summary>
    IReadOnlyList<(string Id, string Name)> Offered(string? editing);
    /// <summary>Whether the named condition from is, or uses through any others, the one with id to.</summary>
    bool Reaches(string from, string to);
}

// The named-condition line (contracts/named-format.md): "Worth fighting is true", read from the resolver; unknown when
// the named condition is missing, loops back, or the Macros feature isn't running.
internal static partial class ConditionKinds
{
    public const string NamedKind = "named.is";
    public const string MissingNamed = "missing named condition";
    public const string MacrosOff = "macros off";

    /// <summary>The named conditions' resolver; set by the Macros feature while it runs.</summary>
    public static INamedResolver? Named { get; set; }

    // Constant notes so a check allocates nothing.
    private const string TrueNote = "true", FalseNote = "false";

    /// <summary>A named condition's name as lines show it: its name, or "missing named condition".</summary>
    public static string NamedName(string id) => Named?.NameOf(id) ?? MissingNamed;

    /// <summary>The named condition a line names, or null when it isn't a named-condition line.</summary>
    public static string? NamedIdOf(ConditionEntry entry) =>
        entry.Kind == NamedKind && entry.Args["named"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? id) && id.Length > 0 ? id : null;

    private static void AddNamedKinds()
    {
        Add(new ConditionKind(NamedKind, "named", "Named condition")
        {
            Args = { ArgSpec.Named("named"), ArgSpec.Choice("is", "true", "true", "false") },
            Check = (_, a) =>
            {
                if (Named is not { } resolver) return ConditionResult.Unknown(MacrosOff);
                if (resolver.Result(a.Text("named"), out string? reason) is not bool value) return ConditionResult.Unknown(reason ?? MissingNamed);
                return ConditionResult.Of(value == (a.Text("is") == "true"), note: value ? TrueNote : FalseNote);
            },
            Words = a => Named?.NameOf(a.Text("named")) is { } name ? $"{name} is {a.Text("is")}" : MissingNamed,
            FailText = a => Named?.FailText(a.Text("named"), a.Text("is") == "true") ?? MissingNamed,
            FailTextExplains = true,
        });
    }
}
