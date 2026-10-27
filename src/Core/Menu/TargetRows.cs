namespace EpochAssist.Core;

/// <summary>
/// The rows of one target (contracts/target-words.md), the same for a cast step, the shared slot target and a slot
/// macro: Target (at cursor, the enemy picks, the area picks; a slot's own adds "same as shared target" first), Within, Radius (area
/// picks), At least, Not with, Only with, When nothing qualifies, and the live pick. Rows other than Target show only
/// for an enemy or area pick. Every change goes through set, so the owner's own change path (and its undo) records it.
/// </summary>
internal static class TargetRows
{
    private static readonly string[] s_rarityLabels = { "any", "magic or better", "rare or better", "boss" };
    private static readonly List<string> s_labels = TargetSpec.Choices.Select(TargetSpec.Label).ToList();
    private static readonly List<string> s_sameLabels = new[] { "same as shared target" }.Concat(s_labels).ToList();
    private static readonly (string, int)[] s_sameGroups = TargetSpec.ChoiceGroups.Select(g => (g.Label, g.First + 1)).ToArray();

    /// <summary>How a target is read and changed by its owner.</summary>
    internal sealed class Access
    {
        /// <summary>Key prefix of the rows ("step.2.", "aiming.", "slot3.").</summary>
        public string Prefix { get; init; } = string.Empty;
        public TargetOwner Owner { get; init; } = TargetOwner.Step;
        /// <summary>The stored target; null for a slot that follows the shared one.</summary>
        public Func<TargetSpec?> Get { get; init; } = () => TargetSpec.Cursor;
        public Action<TargetSpec?> Set { get; init; } = _ => { };
        /// <summary>A slot's own target: "same as shared target" first, reading this target meanwhile.</summary>
        public Func<TargetSpec>? SameAs { get; init; }
        /// <summary>The range an at-cursor target takes when it gets an enemy pick (the slot target starts from Aim range).</summary>
        public Func<float>? StartRange { get; init; }
        public Func<string?>? Blocked { get; init; }
    }

    public static IEnumerable<MenuRow> Rows(Access access)
    {
        string prefix = access.Prefix;
        var owner = access.Owner;
        bool same = access.SameAs != null;
        // The target the rows show: the stored one, or what a following slot reads.
        TargetSpec Shown() => access.Get() ?? access.SameAs?.Invoke() ?? TargetSpec.Cursor;
        bool Own() => access.Get() != null;
        bool Enemy() => Own() && access.Get() is { Pick: not TargetPick.Cursor, Newer: null };
        void Change(Func<TargetSpec, TargetSpec> change)
        {
            if (access.Blocked?.Invoke() != null || access.Get() is not { Newer: null } now) return;
            access.Set(change(now));
        }

        yield return new ValueChoice("Target", () =>
        {
            if (access.Get() is not { } target) return same ? 0 : -1;
            if (target.Newer != null) return -1;
            return (int)target.Pick + (same ? 1 : 0);
        }, i =>
        {
            if (access.Blocked?.Invoke() != null) return;
            if (same && i == 0)
            {
                access.Set(null);
                return;
            }
            int index = i - (same ? 1 : 0);
            if (index < 0 || index >= TargetSpec.Choices.Length) return;
            var pick = TargetSpec.Choices[index];
            var from = access.Get() is { Newer: null } stored ? stored : Shown();
            if (from.Newer != null) from = TargetSpec.Cursor;
            var made = from.WithPick(pick, owner);
            if (from.Pick == TargetPick.Cursor && pick != TargetPick.Cursor && access.StartRange != null) made = made.WithRange(access.StartRange());
            access.Set(made);
        }, () => same ? s_sameLabels : s_labels)
        {
            Key = prefix + "target",
            Width = 230f,
            Groups = () => same ? s_sameGroups : TargetSpec.ChoiceGroups,
            Shown = () => access.Get() is { Newer: not null } newer ? newer.Words(owner) : null,
        };
        yield return new ValueNumber("Within", () => access.Get()?.Range ?? TargetSpec.DefaultRange, v => Change(t => t.WithRange(v)),
            TargetSpec.MinRange, TargetSpec.MaxRange, 1f, " m") { Key = prefix + "within", VisibleWhen = Enemy };
        yield return new ValueNumber("Radius", () => access.Get()?.Radius ?? TargetSpec.DefaultRadius, v => Change(t => t.WithRadius(v)),
            TargetSpec.MinRadius, TargetSpec.MaxRadius, TargetSpec.RadiusStep, " m") { Key = prefix + "radius", VisibleWhen = () => Enemy() && access.Get()!.IsArea };
        yield return new ValueChoice("At least", () => (int)(access.Get()?.MinRarity ?? Rarity.Normal), i =>
        {
            if (i >= 0 && i < s_rarityLabels.Length) Change(t => t.WithMinRarity((Rarity)i));
        }, () => s_rarityLabels) { Key = prefix + "atleast", Width = 150f, VisibleWhen = Enemy };
        yield return LimitPicker("Not with", prefix + "notwith", "no mod limit", mods: true, () => access.Get()?.WithoutMod, id => Change(t => t.WithoutModLimit(id)), Enemy);
        yield return LimitPicker("Only with", prefix + "onlywith", "no ailment limit", mods: false, () => access.Get()?.WithAilment, id => Change(t => t.WithAilmentLimit(id)), Enemy);
        var fallbacks = TargetSpec.ElseFor(owner);
        var fallbackLabels = fallbacks.Select(TargetSpec.ElseLabel).ToList();
        yield return new ValueChoice("When nothing qualifies", () => Array.IndexOf(fallbacks, access.Get()?.Else ?? "cursor"), i =>
        {
            if (i >= 0 && i < fallbacks.Length) Change(t => t.WithElse(fallbacks[i]));
        }, () => fallbackLabels) { Key = prefix + "else", Width = 160f, VisibleWhen = Enemy };
        yield return new ValueReadout("Picks now", () =>
        {
            var target = Shown();
            if (target.Newer != null) return (target.Words(owner), null);
            if (Targets.PickWords(target) is not { } words) return ("the cursor point", StatusState.Off);
            return (words, words.StartsWith("nothing", StringComparison.Ordinal) ? StatusState.Holding
                : words.StartsWith("no live", StringComparison.Ordinal) || words.StartsWith("not picked", StringComparison.Ordinal) ? StatusState.Waiting : StatusState.Active);
        }) { Key = prefix + "pick", VisibleWhen = () => Shown().Pick != TargetPick.Cursor };
    }

    // A mod or ailment picker with "no … limit" first; the names are the ones condition lines use.
    private static ValueChoice LimitPicker(string label, string key, string none, bool mods, Func<int?> get, Action<int?> set, Func<bool> visible)
    {
        IReadOnlyList<int> Ids() => mods ? GameData.AllMonsterMods() : GameData.AllAilments();
        string Name(int id) => mods ? GameData.MonsterMod(id).Name ?? $"Mod {id}" : ConditionKinds.BuffName(id);
        return new ValueChoice(label, () => get() is int id ? IndexOf(Ids(), id) + 1 : 0, i =>
        {
            if (i == 0)
            {
                set(null);
                return;
            }
            var ids = Ids();
            if (i - 1 < ids.Count) set(ids[i - 1]);
        }, () => new[] { none }.Concat(Ids().Select(Name)).ToList())
        {
            Key = key,
            Width = 190f,
            VisibleWhen = visible,
            Shown = () => get() is int id ? Name(id) : none,
        };
    }

    private static int IndexOf(IReadOnlyList<int> ids, int id)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] == id) return i;
        }
        return -2;
    }
}
