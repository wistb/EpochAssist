namespace EpochAssist.Core;

// Condition lines about a target (contracts/target-format.md, target-words.md): the one-enemy kinds' "target" and "cast"
// subjects, "Enemy rarity" and "Has a target". A target line reads the same pick as a cast with an equal target; the
// owner supplies the cast target in the check's context.
internal static partial class ConditionKinds
{
    /// <summary>The single-enemy picks a line's target offers; area picks only for "Has a target".</summary>
    private static readonly string[] s_enemyPicks = { "rarest", "mostHealth", "maxHealth", "lowHealth", "nearest", "hovered" };
    private static readonly string[] s_anyPicks = s_enemyPicks.Concat(new[] { "areaHealth", "areaWeight", "areaCount" }).ToArray();

    private static ArgSpec TargetPickArg(bool areas = false) =>
        ArgSpec.Choice("pick", "rarest", areas ? s_anyPicks : s_enemyPicks).NewerWhenUnknown().AsOptional();
    private static ArgSpec WithoutMod() => ArgSpec.Mod("withoutMod").AsOptional();
    private static ArgSpec WithAilment() => ArgSpec.Ailment("withAilment").AsOptional();
    private static ArgSpec Radius() => ArgSpec.Number("radius", TargetSpec.DefaultRadius, TargetSpec.MinRadius, TargetSpec.MaxRadius, TargetSpec.RadiusStep, " m").AsOptional();

    /// <summary>A "target" subject's key from a line's args: its pick, range, radius (area picks) and limits.</summary>
    public static TargetKey TargetKeyOf(ArgValues a)
    {
        var pick = (TargetPick)Math.Max(0, Array.IndexOf(TargetSpec.PickNames, a.Text("pick")));
        bool area = pick >= TargetPick.AreaHealth;
        return new TargetKey(pick, a.Number("range"), area ? a.Number("radius") : 0f, RarityOf(a.Text("minRarity")),
            a.Has("withoutMod") ? a.Int("withoutMod") : null, a.Has("withAilment") ? a.Int("withAilment") : null);
    }

    /// <summary>
    /// The pick a line's subject asks the targets for: a "target" subject's own key, or the cast target's (false when
    /// the line has another subject, or a cast subject with no cast target or one at the cursor).
    /// </summary>
    public static bool SubjectTarget(ConditionEntry entry, TargetSpec? castTarget, out TargetKey key)
    {
        key = default;
        if (entry.Invalid != null || entry.KindDef == null || !entry.KindDef.Args.Any(arg => arg.Name == "subject")) return false;
        var a = entry.Values;
        switch (a.Text("subject"))
        {
            case "target":
                key = TargetKeyOf(a);
                return true;
            case "cast" when castTarget is { Pick: not TargetPick.Cursor, Newer: null }:
                key = castTarget.Key;
                return true;
        }
        return false;
    }

    // A target line's read: the cast target or the line's own; not counted, nothing qualifying (unknown when a read is
    // missing, else a "no target" fail), or the enemy.
    private static bool PickedTarget(ConditionContext ctx, ArgValues a, out WorldEntry? enemy, out ConditionResult other)
    {
        enemy = null;
        if (!TargetRead(ctx, a, out var result, out other)) return false;
        if (result.Entry is { } picked)
        {
            enemy = picked;
            return true;
        }
        other = result.Unreadable ? ConditionResult.Unknown(result.Reason ?? "can't be read") : ConditionResult.Fail(note: "no target");
        return false;
    }

    // The target's result for a line, or the line's answer when there is none to read: a cast line with no cast step is
    // invalid, one whose cast aims at the cursor fails, a pick not counted yet is unknown.
    private static bool TargetRead(ConditionContext ctx, ArgValues a, out TargetResult result, out ConditionResult other)
    {
        result = default;
        other = default;
        TargetKey key;
        if (a.Text("subject") == "cast")
        {
            if (ctx.CastTarget is not { } cast)
            {
                other = ctx.Named ? ConditionResult.Unknown(OnlyInCaller) : ConditionResult.Invalid("no cast step to take the target from");
                return false;
            }
            if (cast.Pick == TargetPick.Cursor)
            {
                other = ConditionResult.Fail(note: "aims at the cursor");
                return false;
            }
            if (cast.Newer != null)
            {
                other = ConditionResult.Invalid(TargetSpec.NewerVersion);
                return false;
            }
            key = cast.Key;
        }
        else
        {
            key = TargetKeyOf(a);
        }
        if (!Targets.Read(key, out result))
        {
            other = ConditionResult.Unknown(NotCounted);
            return false;
        }
        return true;
    }

    private static void AddTargetKinds()
    {
        Add(new ConditionKind("enemy.rarity", "enemy", "Enemy rarity")
        {
            Args = { Subject(), MinRarity(), SubjectRange(), TargetPickArg(), WithoutMod(), WithAilment(), ArgSpec.Choice("atLeast", "rare", "magic", "rare", "boss") },
            Parts = { new PartNeed("world", "enemies") },
            Picks = s_subject,
            Check = (ctx, a) =>
            {
                if (!Picked(ctx, a, out var enemy, out var other)) return other;
                var rarity = enemy!.Rarity ?? Rarity.Normal;
                return ConditionResult.Of(rarity >= RarityOf(a.Text("atLeast")), note: Targets.RarityName(rarity));
            },
            Words = a => $"{SubjectWords(a)} {RarityIs(a.Text("atLeast"))}",
            FailText = a => $"{SubjectWords(a)} isn't {(a.Text("atLeast") == "boss" ? "a boss" : $"at least {a.Text("atLeast")}")}",
        });
        Add(new ConditionKind("target.exists", "enemy", "Has a target")
        {
            Args = { Subject(), MinRarity(), SubjectRange(), TargetPickArg(areas: true), Radius(), WithoutMod(), WithAilment() },
            Parts = { new PartNeed("world", "enemies") },
            Picks = s_subject,
            Check = (ctx, a) =>
            {
                if (a.Text("subject") is "nearest" or "hovered")
                {
                    if (!Picked(ctx, a, out var enemy, out var picked)) return picked;
                    return ConditionResult.Pass(note: Targets.EnemyWords(enemy!));
                }
                if (!TargetRead(ctx, a, out var result, out var other)) return other;
                if (result.Found) return ConditionResult.Pass(note: result.Entry is { } e ? Targets.EnemyWords(e) : $"{result.Members} enem{(result.Members == 1 ? "y" : "ies")}");
                return result.Unreadable ? ConditionResult.Unknown(result.Reason ?? "can't be read") : ConditionResult.Fail(note: result.Reason ?? "no target");
            },
            Validate = a => a.Text("subject") == "target" || PickOf(a) < TargetPick.AreaHealth ? null : "area picks need the subject \"a target\"",
            Words = a => $"Has a target: {(a.Text("subject") == "cast" ? "the cast target" : char.ToLowerInvariant(SubjectWords(a)[0]) + SubjectWords(a)[1..])}",
            FailText = a => $"no target: {SubjectWords(a)}",
        });
    }

    private static TargetPick PickOf(ArgValues a) => (TargetPick)Math.Max(0, Array.IndexOf(TargetSpec.PickNames, a.Text("pick")));

    // "is a boss", "is at least rare".
    private static string RarityIs(string atLeast) => atLeast == "boss" ? "is a boss" : $"is at least {atLeast}";
}
