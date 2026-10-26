namespace EpochAssist.Core;

// The kinds the macro suite adds (contracts/condition-kinds.md). Ids and arg names are permanent; each reads the
// player model, the owner's tally, game data or the engine's records only, and says unknown when its read is missing.
internal static partial class ConditionKinds
{
    public const string NotOnBarNote = "not on the bar";

    /// <summary>A skill's name: from game data, else as seen on the bar this session, else "Skill id".</summary>
    public static string SkillName(string id)
    {
        if (id.Length == 0) return "a skill";
        return GameData.Ability(id).Name ?? PlayerModel.SeenSkillName(id) ?? $"Skill {id}";
    }

    // The bar slot of the entry's skill in the current form: unknown when the bar can't be read, a known "not on the
    // bar" fail when it isn't there.
    private static Slot? SkillSlot(ArgValues a, out ConditionResult other)
    {
        other = default;
        if (!FrameHasSlots())
        {
            other = ConditionResult.Unknown(PlayerReason("skill bar"));
            return null;
        }
        int number = PlayerModel.SlotOf(a.Text("skill"));
        if (number == PlayerModel.BarUnknown)
        {
            other = ConditionResult.Unknown("bar not read");
            return null;
        }
        if (number == PlayerModel.NotOnBar)
        {
            other = ConditionResult.Fail(note: NotOnBarNote);
            return null;
        }
        var slot = FrameSlot(number);
        if (slot == null) other = ConditionResult.Unknown("slot not read");
        return slot;
    }

    /// <summary>When a library macro (by id) last fired (negative infinity for never); set by the Macros engine.</summary>
    public static Func<string, float>? MacroFiredAt { get; set; }

    /// <summary>Extra skill ids the pickers offer besides the bar and the seen ones (the library's, the class's).</summary>
    public static Func<IEnumerable<string>>? MoreSkills { get; set; }

    /// <summary>
    /// The skills a picker offers, without repeats: the current bar first, then skills seen this session, then the
    /// extra ones (library, class list), then the current value if it is none of those.
    /// </summary>
    public static List<(string Id, string Name)> SkillOptions(string? current)
    {
        var options = new List<(string, string)>();
        var seen = new HashSet<string>();
        void Offer(string? id)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) return;
            options.Add((id, SkillName(id)));
        }
        var slots = PlayerModel.Slots;
        for (int i = 0; slots != null && i < Dispatcher.SlotCount && i < slots.Count; i++) Offer(slots[i]?.AbilityId);
        foreach (var (id, _) in PlayerModel.SeenSkills) Offer(id);
        if (MoreSkills != null)
        {
            foreach (string id in MoreSkills()) Offer(id);
        }
        Offer(current);
        return options;
    }

    private static string SkillWords(ArgValues a) => SkillName(a.Text("skill"));

    private static void AddMacroKinds()
    {
        Add(new ConditionKind("skill.ready", "skill", "Skill ready")
        {
            Args = { ArgSpec.Skill("skill") },
            Parts = { new PartNeed("player", "skill bar") },
            Check = (_, a) =>
            {
                if (SkillSlot(a, out var other) is not { } slot) return other;
                if (slot.Ready is not bool ready) return ConditionResult.Unknown("readiness not read");
                return ConditionResult.Of(ready, note: ready ? "ready" : "not ready");
            },
            Words = a => $"{SkillWords(a)} ready",
            FailText = a => $"{SkillWords(a)} not ready",
        });
        Add(new ConditionKind("skill.cooldown", "skill", "Skill cooldown")
        {
            Args = { ArgSpec.Skill("skill"), ArgSpec.Compare("below", "below", "above"), ArgSpec.Number("seconds", 1, 0, 120, 0.1f, " s"), To() },
            Parts = { new PartNeed("player", "skill bar") },
            Check = (_, a) =>
            {
                if (SkillSlot(a, out var other) is not { } slot) return other;
                // Seconds until every charge is back: (max − charges) × one charge's time; no cooldown reads 0.
                if (slot.MaxCharges is int max && max <= 0) return ConditionResult.Of(a.Compare(0f, "seconds"), 0f);
                if (slot.Charges is not float charges || slot.MaxCharges is not int maxCharges) return ConditionResult.Unknown("no cooldown data");
                float left = maxCharges - charges <= 0f ? 0f : slot.RechargeTime is float one ? (maxCharges - charges) * one : float.NaN;
                if (float.IsNaN(left)) return ConditionResult.Unknown("no cooldown data");
                return ConditionResult.Of(a.Compare(left, "seconds"), left);
            },
            Validate = a => a.BetweenInvalid("seconds"),
            Words = a => $"{SkillWords(a)} cooldown {CompareText(a, "seconds", " s")}",
            FailText = a => $"{SkillWords(a)} cooldown not {CompareText(a, "seconds", " s")}",
            ValueText = r => Seconds(r.Number),
        });
        Add(new ConditionKind("skill.charges", "skill", "Skill charges")
        {
            Args = { ArgSpec.Skill("skill"), ArgSpec.Compare("atLeast", "atLeast", "atMost", "below", "above"), ArgSpec.Number("count", 1, 0, 10, 1), To() },
            Parts = { new PartNeed("player", "skill bar") },
            Check = (_, a) =>
            {
                if (SkillSlot(a, out var other) is not { } slot) return other;
                if (slot.MaxCharges is int max && max <= 0) return ConditionResult.Pass(note: "no charges");
                if (slot.Charges is not float charges) return ConditionResult.Unknown("charges not read");
                // Online charges are fractional while regaining (GF-63): whole charges count.
                float whole = MathF.Floor(charges + 1e-4f);
                return ConditionResult.Of(a.Compare(whole, "count"), whole);
            },
            Validate = a => a.BetweenInvalid("count"),
            Words = a => $"{SkillWords(a)} charges {CompareText(a, "count", string.Empty)}",
            FailText = a => $"{SkillWords(a)} charges not {CompareText(a, "count", string.Empty)}",
            ValueText = r => $"{N(r.Number)} charge{(r.Number == 1f ? string.Empty : "s")}",
        });
        AddMoreMacroKinds();
    }

    private const string Never = "never";

    private static ArgSpec CountCompare() => ArgSpec.Compare("atLeast", "atLeast", "atMost", "below", "above");

    private static void AddMoreMacroKinds()
    {
        // ---- potions ----
        Add(new ConditionKind("potions.available", "potions", "Potions left")
        {
            Args = { CountCompare(), ArgSpec.Number("count", 1, 0, 20, 1), To() },
            Parts = { new PartNeed("player", "potions") },
            Check = (_, a) => PlayerModel.Potions?.Charges is int charges
                ? ConditionResult.Of(a.Compare(charges, "count"), charges) : ConditionResult.Unknown(PlayerReason("potions")),
            Validate = a => a.BetweenInvalid("count"),
            Words = a => $"Potions {CompareText(a, "count", string.Empty)}",
            FailText = a => $"potions not {CompareText(a, "count", string.Empty)}",
        });
        Add(new ConditionKind("potions.missing", "potions", "Potions missing")
        {
            Args = { CountCompare(), ArgSpec.Number("count", 1, 0, 20, 1), To() },
            Parts = { new PartNeed("player", "potions") },
            Check = (_, a) => PlayerModel.Potions is { Charges: int charges, MaxCharges: int max }
                ? ConditionResult.Of(a.Compare(max - charges, "count"), max - charges) : ConditionResult.Unknown(PlayerReason("potions")),
            Validate = a => a.BetweenInvalid("count"),
            Words = a => $"Potions missing {CompareText(a, "count", string.Empty)}",
            FailText = a => $"potions missing not {CompareText(a, "count", string.Empty)}",
        });

        // ---- buffs ----
        Add(new ConditionKind("buff.stacks", "buff", "Buff stacks")
        {
            Args = { ArgSpec.Buff("buff"), CountCompare(), ArgSpec.Number("count", 2, 0, 50, 1), To() },
            Parts = { new PartNeed("player", "buffs") },
            Check = (_, a) =>
            {
                var buffs = FrameBuffs();
                if (buffs == null) return ConditionResult.Unknown(PlayerReason("buffs"));
                int stacks = FindBuff(buffs, a.Int("buff"))?.Stacks ?? 0;
                return ConditionResult.Of(a.Compare(stacks, "count"), stacks);
            },
            Validate = a => a.BetweenInvalid("count"),
            Words = a => $"{BuffName(a.Int("buff"))} stacks {CompareText(a, "count", string.Empty)}",
            FailText = a => $"{BuffName(a.Int("buff"))} stacks not {CompareText(a, "count", string.Empty)}",
            ValueText = r => $"×{N(r.Number)}",
        });

        // ---- time since ----
        Add(new ConditionKind("skill.sinceCast", "skill", "Time since cast")
        {
            Args = { ArgSpec.Skill("skill"), ArgSpec.Compare("above", "above", "below"), ArgSpec.Number("seconds", 5, 0, 600, 0.1f, " s"), To() },
            Parts = { new PartNeed("player", "skill bar") },
            Check = (_, a) =>
            {
                if (!PlayerModel.CastSignal) return ConditionResult.Unknown("no cast signal");
                float at = PlayerModel.LastCastOf(a.Text("skill"));
                float since = float.IsNegativeInfinity(at) ? float.PositiveInfinity : Clock.Now - at;
                return ConditionResult.Of(a.Compare(since, "seconds"), since, float.IsPositiveInfinity(since) ? Never : null);
            },
            Validate = a => a.BetweenInvalid("seconds"),
            Words = a => $"{SkillWords(a)} cast {CompareText(a, "seconds", " s")} ago",
            FailText = a => $"{SkillWords(a)} cast not {CompareText(a, "seconds", " s")} ago",
            ValueText = r => r.Note == Never ? Never : Seconds(r.Number),
        });
        Add(new ConditionKind("macro.sinceFired", "macro", "Time since fired")
        {
            Args = { ArgSpec.Compare("above", "above", "below"), ArgSpec.Number("seconds", 5, 0, 600, 0.1f, " s"), To() },
            Check = (ctx, a) =>
            {
                if (ctx.Macro is not { } id || MacroFiredAt is not { } firedAt) return ConditionResult.Unknown("only in a library macro");
                float at = firedAt(id);
                // Never fired reads as infinitely long ago.
                float since = float.IsNegativeInfinity(at) ? float.PositiveInfinity : Clock.Now - at;
                return ConditionResult.Of(a.Compare(since, "seconds"), since, float.IsPositiveInfinity(since) ? Never : null);
            },
            Validate = a => a.BetweenInvalid("seconds"),
            Words = a => $"Fired {CompareText(a, "seconds", " s")} ago",
            FailText = a => $"fired not {CompareText(a, "seconds", " s")} ago",
            ValueText = r => r.Note == Never ? Never : Seconds(r.Number),
        });

        // ---- one enemy: the hovered one, or the nearest of at least a rarity within a range ----
        Add(Enemy("enemy.distance", "Enemy distance", Distance, a => $"{EnemyWords(a)} distance {CompareText(a, "metres", " m")}",
            ArgSpec.Compare("below", "below", "above"), ArgSpec.Number("metres", 5, 0, 60, 0.5f, " m")));
        Add(Enemy("enemy.health", "Enemy health", Health, a => $"{EnemyWords(a)} health {CompareText(a, "percent", "%")}",
            ArgSpec.Compare("below", "below", "above"), ArgSpec.Number("percent", 50, 0, 100, 1, "%")));
        Add(Enemy("enemy.ward", "Enemy ward", EnemyWard, a => $"{EnemyWords(a)} ward {CompareText(a, "value", string.Empty)}",
            ArgSpec.Compare("above", "above", "below"), ArgSpec.Number("value", 1, 0, 1000000, 1)));
        Add(new ConditionKind("enemy.mod", "enemy", "Enemy mod")
        {
            Args = { ArgSpec.Mod("mod"), Subject(), MinRarity(), SubjectRange() },
            Parts = { new PartNeed("world", "enemies") },
            Picks = s_subject,
            Check = (ctx, a) =>
            {
                if (!Picked(ctx, a, out var enemy, out var other)) return other;
                if (!World.Has("monster mods")) return ConditionResult.Unknown(Missing(World.MonsterMods));
                string? name = GameData.MonsterMod(a.Int("mod")).Name;
                foreach (var mod in enemy!.Mods)
                {
                    if (mod.Key is int key && key == a.Int("mod") || (name != null && mod.Name == name)) return ConditionResult.Pass(note: "has it");
                }
                return ConditionResult.Fail(note: "hasn't");
            },
            Words = a => $"{EnemyWords(a)} has {GameData.MonsterMod(a.Int("mod")).Name ?? $"mod {a.Int("mod")}"}",
            FailText = a => $"{EnemyWords(a)} hasn't {GameData.MonsterMod(a.Int("mod")).Name ?? $"mod {a.Int("mod")}"}",
        });
        Add(new ConditionKind("enemy.ailment", "enemy", "Enemy ailment")
        {
            Args = { ArgSpec.Ailment("ailment"), Subject(), MinRarity(), SubjectRange() },
            Parts = { new PartNeed("world", "enemies") },
            Picks = s_subject,
            Check = (ctx, a) =>
            {
                if (!Picked(ctx, a, out var enemy, out var other)) return other;
                if (World.AilmentStacks(enemy!, a.Int("ailment")) is not int stacks) return ConditionResult.Unknown(Missing(World.EnemyAilments));
                return ConditionResult.Of(stacks > 0, stacks, stacks > 0 ? null : "none");
            },
            Words = a => $"{EnemyWords(a)} has {BuffName(a.Int("ailment"))}",
            FailText = a => $"{EnemyWords(a)} hasn't {BuffName(a.Int("ailment"))}",
            ValueText = r => r.Note ?? $"×{N(r.Number)}",
        });

        // ---- cursor and players ----
        Add(new ConditionKind("cursor.distance", "cursor", "Cursor distance")
        {
            Args = { ArgSpec.Compare("below", "below", "above"), ArgSpec.Number("metres", 5, 0, 60, 0.5f, " m"), To() },
            Check = (_, a) =>
            {
                if (CursorPoint?.Invoke() is not UnityEngine.Vector3 point || Player.Transform is not UnityEngine.Transform hero) return ConditionResult.Unknown("no cursor point");
                float d = UnityEngine.Vector3.Distance(hero.position, point);
                return ConditionResult.Of(a.Compare(d, "metres"), d);
            },
            Validate = a => a.BetweenInvalid("metres"),
            Words = a => $"Cursor {CompareText(a, "metres", " m")} away",
            FailText = a => $"cursor not {CompareText(a, "metres", " m")} away",
            ValueText = r => $"{N(r.Number)} m",
        });
        Add(new ConditionKind("players.nearby", "players", "Players nearby")
        {
            Args = { CountCompare(), ArgSpec.Number("count", 1, 0, 10, 1), ArgSpec.Number("range", 30, 1, 100, 1, " m"), To() },
            Check = (_, a) =>
            {
                // Offline nobody else is there: a known 0.
                if (PlayerModel.Online == false) return ConditionResult.Of(a.Compare(0, "count"), 0);
                if (!World.Has("players")) return ConditionResult.Unknown(Missing(World.PlayersOnline));
                int n = World.PlayersWithin(a.Number("range"));
                return ConditionResult.Of(a.Compare(n, "count"), n);
            },
            Validate = a => a.BetweenInvalid("count"),
            Words = a => $"Players {CompareText(a, "count", string.Empty)} within {N(a.Number("range"))} m",
            FailText = a => $"players not {CompareText(a, "count", string.Empty)} within {N(a.Number("range"))} m",
        });

        // ---- zone ----
        Add(new ConditionKind("zone.kind", "zone", "Zone kind")
        {
            Args = { ArgSpec.Choice("is", "echo", "town", "echo", "dungeon", "campaign"), ArgSpec.Bool("not", false) },
            Check = (_, a) =>
            {
                if (ZoneKind() is not { } kind) return ConditionResult.Unknown("no scene");
                return ConditionResult.Of((kind == a.Text("is")) != a.Flag("not"), note: kind);
            },
            Words = a => $"Zone {(a.Flag("not") ? "isn't" : "is")} {a.Text("is")}",
            FailText = a => $"zone {(a.Flag("not") ? "is" : "isn't")} {a.Text("is")}",
        });
        Add(new ConditionKind("zone.level", "zone", "Echo corruption")
        {
            Args = { CountCompare(), ArgSpec.Number("value", 100, 0, 5000, 1), To() },
            Check = (_, a) =>
            {
                // Outside an echo there is no corruption: a known fail.
                if (ZoneKind() != "echo") return ConditionResult.Fail(note: "not in an echo");
                if (GameState.EchoCorruption is not int value) return ConditionResult.Unknown(Missing(GameStateFeature.EchoCorruption));
                return ConditionResult.Of(a.Compare(value, "value"), value);
            },
            Validate = a => a.BetweenInvalid("value"),
            Words = a => $"Corruption {CompareText(a, "value", string.Empty)}",
            FailText = a => $"corruption not {CompareText(a, "value", string.Empty)}",
        });

        // ---- form and movement ----
        Add(new ConditionKind("player.form", "player", "Form")
        {
            Args = { ArgSpec.Choice("is", "human", "human", "transformed", "reaper"), ArgSpec.Bool("not", false) },
            Parts = { new PartNeed("player", "form") },
            Check = (_, a) =>
            {
                if (PlayerModel.Form is not { } form) return ConditionResult.Unknown(PlayerReason("form"));
                string now = form.Reaper == true ? "reaper" : form.TransformId != null ? "transformed" : "human";
                return ConditionResult.Of((now == a.Text("is")) != a.Flag("not"), note: now);
            },
            Words = a => $"Form {(a.Flag("not") ? "isn't" : "is")} {a.Text("is")}",
            FailText = a => $"form {(a.Flag("not") ? "is" : "isn't")} {a.Text("is")}",
        });
        Add(new ConditionKind("player.moving", "player", "Moving")
        {
            Args = { ArgSpec.Choice("is", "moving", "moving", "still") },
            Check = (_, a) =>
            {
                if (PlayerModel.Moving is not bool moving) return ConditionResult.Unknown(PlayerModel.Exists ? "not sampled yet" : NotInGame);
                return ConditionResult.Of(moving == (a.Text("is") == "moving"), note: moving ? "moving" : "still");
            },
            Words = a => a.Text("is") == "moving" ? "Moving" : "Standing still",
            FailText = a => a.Text("is") == "moving" ? "not moving" : "moving",
        });
    }

    /// <summary>The cursor point skills aim at, once a frame (the owner sets it); null without one.</summary>
    public static Func<UnityEngine.Vector3?>? CursorPoint { get; set; }

    // The zone's kind as the session code tells it (GF-67), its "other" read as campaign; null without a scene.
    private static string? ZoneKind()
    {
        if (GameState.Scene.Length == 0) return null;
        string kind = Sessions.ZoneKind(GameState.Scene, GameState.Kind, false);
        return kind switch { "other" => "campaign", "menu" => null, _ => kind };
    }

    // ---- enemy subject args ----

    private static ArgSpec Subject() => ArgSpec.Choice("subject", "nearest", "nearest", "hovered");
    private static ArgSpec MinRarity() => ArgSpec.Choice("minRarity", "normal", "normal", "magic", "rare", "boss").AsOptional();
    private static ArgSpec SubjectRange() => ArgSpec.Number("range", 15, 1, 60, 1, " m").AsOptional();

    private static Rarity RarityOf(string name) => name switch { "boss" => Rarity.Boss, "rare" => Rarity.Rare, "magic" => Rarity.Magic, _ => Rarity.Normal };

    private static readonly Func<ArgValues, SubjectKey> s_subject = a => a.Text("subject") == "hovered"
        ? new SubjectKey(true, Rarity.Normal, 0f) : new SubjectKey(false, RarityOf(a.Text("minRarity")), a.Number("range"));

    private static string EnemyWords(ArgValues a)
    {
        if (a.Text("subject") == "hovered") return "Hovered enemy";
        string rarity = a.Text("minRarity") switch { "magic" => "magic or better ", "rare" => "rare or boss ", "boss" => "boss ", _ => string.Empty };
        return $"Nearest {rarity}enemy within {N(a.Number("range"))} m:";
    }

    // The entry's enemy as the owner's scan picked it: unknown when not picked, a known "no enemy" fail when none.
    private static bool Picked(ConditionContext ctx, ArgValues a, out WorldEntry? enemy, out ConditionResult other)
    {
        enemy = null;
        other = default;
        var tally = ctx.Tally;
        if (tally == null || tally.RefreshedAt < 0f || !tally.TryGetSubject(s_subject(a), out enemy))
        {
            other = ConditionResult.Unknown(NotCounted);
            return false;
        }
        if (enemy == null)
        {
            other = ConditionResult.Fail(note: "no enemy");
            return false;
        }
        return true;
    }

    private static ConditionKind Enemy(string id, string label, Func<WorldEntry, ConditionResult?> read, Func<ArgValues, string> words, ArgSpec compare, ArgSpec limit)
    {
        string limitName = limit.Name;
        return new ConditionKind(id, "enemy", label)
        {
            Args = { Subject(), MinRarity(), SubjectRange(), compare, limit, To() },
            Parts = { new PartNeed("world", "enemies") },
            Picks = s_subject,
            Check = (ctx, a) =>
            {
                if (!Picked(ctx, a, out var enemy, out var other)) return other;
                if (read(enemy!) is not { } value) return ConditionResult.Unknown("not read");
                if (value.State != ConditionState.Pass) return value;
                return ConditionResult.Of(a.Compare(value.Number, limitName), value.Number);
            },
            Validate = a => a.BetweenInvalid(limitName),
            Words = words,
            FailText = a => $"not: {words(a)}",
            ValueText = r => limitName switch { "metres" => $"{N(r.Number)} m", "percent" => Pct(r.Number), _ => N(r.Number) },
        };
    }

    private static ConditionResult? Distance(WorldEntry e) => ConditionResult.Pass(e.Distance);

    private static ConditionResult? Health(WorldEntry e) =>
        e.HealthFraction is float f ? ConditionResult.Pass(f * 100f) : ConditionResult.Unknown(Missing("world: enemy health"));

    private static ConditionResult? EnemyWard(WorldEntry e) => e.Ward is float w ? ConditionResult.Pass(w) : ConditionResult.Unknown(Missing("world: enemy ward"));
}
