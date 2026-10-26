using System.Globalization;

namespace EpochAssist.Core;

/// <summary>
/// Every declared condition kind, by id (contracts/condition-kinds.md). Ids and arg names are permanent. A later
/// feature adds its own with Add; a list entry whose kind isn't here reads as invalid and is kept.
/// </summary>
internal static class ConditionKinds
{
    private static readonly List<ConditionKind> s_kinds = new();
    private static readonly Dictionary<string, ConditionKind> s_byId = new();

    /// <summary>Subjects in the order "+ Add condition" groups them, with their headings.</summary>
    public static readonly (string Subject, string Heading)[] Subjects =
    {
        ("player", "Player"), ("buff", "Buffs"), ("slot", "Skills"), ("enemies", "Enemies"), ("input", "Keys"),
    };

    public static IReadOnlyList<ConditionKind> All => s_kinds;

    static ConditionKinds()
    {
        AddPlayerKinds();
        AddBuffKinds();
        AddSlotKinds();
        AddEnemyKinds();
        AddInputKinds();
    }

    public static void Add(ConditionKind kind)
    {
        if (s_byId.ContainsKey(kind.Id)) throw new ArgumentException($"condition kind {kind.Id} is declared twice");
        s_kinds.Add(kind);
        s_byId[kind.Id] = kind;
    }

    /// <summary>The kind with this id, or null when this build doesn't have it.</summary>
    public static ConditionKind? Find(string id) => s_byId.TryGetValue(id, out var kind) ? kind : null;

    /// <summary>Kinds grouped by subject, in "+ Add condition" order: (heading, kinds).</summary>
    public static IEnumerable<(string Heading, IReadOnlyList<ConditionKind> Kinds)> BySubject()
    {
        foreach (var (subject, heading) in Subjects)
        {
            var kinds = s_kinds.Where(k => k.Subject == subject).ToList();
            if (kinds.Count > 0) yield return (heading, kinds);
        }
        // Subjects a later feature adds go after, under their own name.
        foreach (var group in s_kinds.Where(k => Subjects.All(s => s.Subject != k.Subject)).GroupBy(k => k.Subject))
        {
            yield return (char.ToUpperInvariant(group.Key[0]) + group.Key[1..], group.ToList());
        }
    }

    // ---- This frame's model reads, once per frame however many entries ask ----

    private static int s_frame = -1;
    private static bool s_haveVitals, s_haveBuffs, s_haveSlots;
    private static Vitals? s_vitals;
    private static IReadOnlyList<Buff>? s_buffs;
    private static IReadOnlyList<Slot?>? s_slots;

    private static void NewFrame()
    {
        int frame = UnityEngine.Time.frameCount;
        if (frame == s_frame) return;
        s_frame = frame;
        s_haveVitals = s_haveBuffs = s_haveSlots = false;
    }

    /// <summary>Forgets this frame's reads (the dev benchmark, to time each pass as a frame's first).</summary>
    internal static void ResetFrame() => s_frame = -1;

    private static Vitals? FrameVitals()
    {
        NewFrame();
        if (!s_haveVitals) (s_vitals, s_haveVitals) = (PlayerModel.Vitals, true);
        return s_vitals;
    }

    private static IReadOnlyList<Buff>? FrameBuffs()
    {
        NewFrame();
        if (!s_haveBuffs) (s_buffs, s_haveBuffs) = (PlayerModel.Buffs, true);
        return s_buffs;
    }

    private static Slot? FrameSlot(int number)
    {
        NewFrame();
        if (!s_haveSlots) (s_slots, s_haveSlots) = (PlayerModel.Slots, true);
        return s_slots != null && number >= 1 && number <= s_slots.Count ? s_slots[number - 1] : null;
    }

    private static bool FrameHasSlots()
    {
        FrameSlot(0);
        return s_slots != null;
    }

    // ---- Unknown reasons, made once each so a check allocates nothing ----

    private static readonly Dictionary<string, string> s_missing = new();
    private static readonly Dictionary<string, string> s_failed = new();
    public const string NotInGame = "not in game";
    public const string NotCounted = "not counted yet";

    // Why a player part has no value: no player, its group missing in this mode, or its read failing this frame.
    private static string PlayerReason(string part)
    {
        if (!PlayerModel.Exists) return NotInGame;
        if (PlayerModel.MissingGroup(part) is { } group) return Missing(group);
        if (!s_failed.TryGetValue(part, out string? failed)) s_failed[part] = failed = $"player: {part} read failed";
        return PlayerModel.Failed(part) ? failed : Missing($"player: {part}");
    }

    private static string Missing(string group)
    {
        if (!s_missing.TryGetValue(group, out string? reason)) s_missing[group] = reason = group + " missing";
        return reason;
    }

    private static string Pct(float value) => value.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Seconds(float value) => value.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string N(float value) => ArgSpec.Format(value);

    // ---- player ----

    private static void AddPlayerKinds()
    {
        Add(Vital("player.health", "Health", "health", v => (v.Health, v.MaxHealth)));
        Add(Vital("player.mana", "Mana", "mana", v => (v.Mana, v.MaxMana)));
        Add(new ConditionKind("player.ward", "player", "Ward")
        {
            Args = { ArgSpec.Choice("compare", "below", "below", "above"), ArgSpec.Number("percent", 50, 0, 300, 5, "%") },
            Parts = { new PartNeed("player", "vitals") },
            Check = (_, a) =>
            {
                var vitals = FrameVitals();
                if (vitals == null) return ConditionResult.Unknown(PlayerReason("vitals"));
                if (vitals.Ward is not float ward) return ConditionResult.Unknown(Missing("player: ward"));
                if (vitals.MaxHealth <= 0f) return ConditionResult.Unknown("max health is 0");
                return Compare(ward / vitals.MaxHealth * 100f, a);
            },
            Words = a => $"Ward {a.Text("compare")} {N(a.Number("percent"))}% of health",
            FailText = a => $"ward not {a.Text("compare")} {N(a.Number("percent"))}% of health",
            ValueText = r => Pct(r.Number),
        });
    }

    private static ConditionKind Vital(string id, string label, string word, Func<Vitals, (float Value, float Max)> read) => new(id, "player", label)
    {
        Args = { ArgSpec.Choice("compare", "below", "below", "above"), ArgSpec.Number("percent", 50, 1, 100, 1, "%") },
        Parts = { new PartNeed("player", "vitals") },
        Check = (_, a) =>
        {
            var vitals = FrameVitals();
            if (vitals == null) return ConditionResult.Unknown(PlayerReason("vitals"));
            var (value, max) = read(vitals);
            if (max <= 0f) return ConditionResult.Unknown($"max {word} is 0");
            return Compare(value / max * 100f, a);
        },
        Words = a => $"{label} {a.Text("compare")} {N(a.Number("percent"))}%",
        FailText = a => $"{word} not {a.Text("compare")} {N(a.Number("percent"))}%",
        ValueText = r => Pct(r.Number),
    };

    private static ConditionResult Compare(float percent, ArgValues a)
    {
        float limit = a.Number("percent");
        bool pass = a.Text("compare") == "above" ? percent > limit : percent < limit;
        return ConditionResult.Of(pass, percent);
    }

    // ---- buffs ----

    public const string BuffMissingNote = "missing";
    public const string NoTimerNote = "no timer";
    public const string UpNote = "up";

    /// <summary>A buff's name from game data, or "Buff #id" when it can't name it.</summary>
    public static string BuffName(int id) => GameData.Buff(id).Name ?? $"Buff #{id}";

    private static Buff? FindBuff(IReadOnlyList<Buff> buffs, int id)
    {
        for (int i = 0; i < buffs.Count; i++)
        {
            if (buffs[i].Id == id) return buffs[i];
        }
        return null;
    }

    private static void AddBuffKinds()
    {
        Add(new ConditionKind("buff.missing", "buff", "Buff missing")
        {
            Args = { ArgSpec.Buff("buff") },
            Parts = { new PartNeed("player", "buffs") },
            Check = (_, a) =>
            {
                var buffs = FrameBuffs();
                if (buffs == null) return ConditionResult.Unknown(PlayerReason("buffs"));
                var buff = FindBuff(buffs, a.Int("buff"));
                if (buff == null) return ConditionResult.Pass(note: BuffMissingNote);
                return buff.TimeLeft is float left ? ConditionResult.Fail(left) : ConditionResult.Fail(note: UpNote);
            },
            Words = a => $"{BuffName(a.Int("buff"))} missing",
            FailText = a => $"{BuffName(a.Int("buff"))} is up",
            ValueText = r => Seconds(r.Number),
        });
        Add(new ConditionKind("buff.ending", "buff", "Buff ending")
        {
            Args = { ArgSpec.Buff("buff"), ArgSpec.Number("seconds", 2, 0.5f, 60, 0.5f, " s") },
            Parts = { new PartNeed("player", "buffs") },
            Check = (_, a) =>
            {
                var buffs = FrameBuffs();
                if (buffs == null) return ConditionResult.Unknown(PlayerReason("buffs"));
                var buff = FindBuff(buffs, a.Int("buff"));
                if (buff == null) return ConditionResult.Pass(note: BuffMissingNote);
                // A buff with no timer never ends by itself, so it doesn't pass.
                if (buff.TimeLeft is not float left) return ConditionResult.Fail(note: NoTimerNote);
                return ConditionResult.Of(left < a.Number("seconds"), left);
            },
            Words = a => $"{BuffName(a.Int("buff"))} under {N(a.Number("seconds"))} s",
            FailText = a => $"{BuffName(a.Int("buff"))} has {N(a.Number("seconds"))} s or more",
            ValueText = r => Seconds(r.Number),
        });
    }

    // ---- skill slots ----

    private static string SlotWords(int arg, bool capital) => arg == 0 ? (capital ? "This slot" : "this slot") : $"{(capital ? "Slot" : "slot")} {arg}";

    private static void AddSlotKinds()
    {
        Add(new ConditionKind("slot.ready", "slot", "Skill ready")
        {
            Args = { ArgSpec.Slot("slot") },
            Parts = { new PartNeed("player", "skill bar") },
            Check = (ctx, a) =>
            {
                int number = ctx.SlotNumber(a.Int("slot"));
                if (number == 0) return ConditionResult.Unknown("no slot to read");
                if (!FrameHasSlots()) return ConditionResult.Unknown(PlayerReason("skill bar"));
                var slot = FrameSlot(number);
                if (slot?.Ready is not bool ready) return ConditionResult.Unknown(slot == null ? "slot not read" : "readiness not read");
                return ConditionResult.Of(ready, note: ready ? "ready" : "not ready");
            },
            Words = a => $"{SlotWords(a.Int("slot"), true)} ready",
            FailText = a => $"{SlotWords(a.Int("slot"), false)} not ready",
        });
        Add(new ConditionKind("slot.charges", "slot", "Skill charges")
        {
            Args = { ArgSpec.Slot("slot"), ArgSpec.Number("count", 1, 1, 10, 1) },
            Parts = { new PartNeed("player", "skill bar") },
            Check = (ctx, a) =>
            {
                int number = ctx.SlotNumber(a.Int("slot"));
                if (number == 0) return ConditionResult.Unknown("no slot to read");
                if (!FrameHasSlots()) return ConditionResult.Unknown(PlayerReason("skill bar"));
                var slot = FrameSlot(number);
                if (slot == null) return ConditionResult.Unknown("slot not read");
                // A skill without charges always has its one use when ready; online charges are fractional while
                // regaining, so whole charges count.
                if (slot.MaxCharges is int max && max <= 0) return ConditionResult.Pass(note: "no charges");
                if (slot.Charges is not float charges) return ConditionResult.Unknown("charges not read");
                float whole = MathF.Floor(charges + 1e-4f);
                return ConditionResult.Of(whole >= a.Number("count"), whole);
            },
            Words = a => $"{SlotWords(a.Int("slot"), true)} has {N(a.Number("count"))} charge{(a.Int("count") == 1 ? string.Empty : "s")}",
            FailText = a => $"{SlotWords(a.Int("slot"), false)} has fewer than {N(a.Number("count"))} charge{(a.Int("count") == 1 ? string.Empty : "s")}",
            ValueText = r => $"{N(r.Number)} charge{(r.Number == 1f ? string.Empty : "s")}",
        });
    }

    // ---- enemies (read from the owner's tally) ----

    private static readonly Func<ArgValues, float> s_range = a => a.Number("range");

    // The tally's numbers for the entry's range; unknown when it hasn't counted it.
    private static bool Tallied(ConditionContext ctx, ArgValues a, out int count, out float weight, out Rarity? max, out ConditionResult unknown)
    {
        count = 0;
        weight = 0f;
        max = null;
        unknown = default;
        var tally = ctx.Tally;
        if (tally == null || tally.RefreshedAt < 0f)
        {
            unknown = ConditionResult.Unknown(NotCounted);
            return false;
        }
        if (tally.Failed != null)
        {
            unknown = ConditionResult.Unknown(tally.Failed);
            return false;
        }
        if (!tally.TryGet(a.Number("range"), out count, out weight, out max))
        {
            unknown = ConditionResult.Unknown(NotCounted);
            return false;
        }
        return true;
    }

    public static int RarityRank(string name) => name switch { "boss" => (int)Rarity.Boss, "rare" => (int)Rarity.Rare, _ => (int)Rarity.Magic };

    private static string RarityWords(string name) => name switch { "boss" => "boss", "rare" => "rare or boss", _ => "magic, rare or boss" };

    private static void AddEnemyKinds()
    {
        Add(new ConditionKind("enemies.weight", "enemies", "Enemy weight")
        {
            Args = { ArgSpec.Number("range", 15, 2, 60, 1, " m"), ArgSpec.Number("weight", 1, 1, 50, 1) },
            Parts = { new PartNeed("world", "enemies") },
            Scanned = true,
            Range = s_range,
            Check = (ctx, a) => Tallied(ctx, a, out _, out float weight, out _, out var unknown)
                ? ConditionResult.Of(weight >= a.Number("weight") - 1e-4f, weight) : unknown,
            Words = a => $"Enemy weight {N(a.Number("weight"))} within {N(a.Number("range"))} m",
            LabelledWords = a => $"weight {N(a.Number("weight"))} within {N(a.Number("range"))} m",
            // The old enemy gate's reason, word for word.
            FailText = _ => "not enough enemies in range",
            ValueText = r => $"weight {N(r.Number)}",
        });
        Add(new ConditionKind("enemies.rarity", "enemies", "Enemy rarity")
        {
            Args = { ArgSpec.Number("range", 12, 2, 60, 1, " m"), ArgSpec.Rarity("rarity", "rare") },
            Parts = { new PartNeed("world", "enemies") },
            Scanned = true,
            Range = s_range,
            Check = (ctx, a) =>
            {
                if (!Tallied(ctx, a, out _, out _, out var max, out var unknown)) return unknown;
                bool pass = max is Rarity rarity && (int)rarity >= RarityRank(a.Text("rarity"));
                return ConditionResult.Of(pass, note: RarityNote(max));
            },
            Words = a => $"A {RarityWords(a.Text("rarity"))} within {N(a.Number("range"))} m",
            FailText = a => $"no {RarityWords(a.Text("rarity"))} within {N(a.Number("range"))} m",
        });
        Add(new ConditionKind("enemies.count", "enemies", "Enemy count")
        {
            Args = { ArgSpec.Number("range", 10, 2, 60, 1, " m"), ArgSpec.Number("count", 3, 1, 50, 1) },
            Parts = { new PartNeed("world", "enemies") },
            Scanned = true,
            Range = s_range,
            Check = (ctx, a) => Tallied(ctx, a, out int count, out _, out _, out var unknown)
                ? ConditionResult.Of(count >= a.Number("count"), count) : unknown,
            Words = a => $"{N(a.Number("count"))} enem{(a.Int("count") == 1 ? "y" : "ies")} within {N(a.Number("range"))} m",
            FailText = a => $"fewer than {N(a.Number("count"))} enem{(a.Int("count") == 1 ? "y" : "ies")} within {N(a.Number("range"))} m",
            ValueText = r => $"{N(r.Number)} enem{(r.Number == 1f ? "y" : "ies")}",
        });
    }

    // Constant notes so a check allocates nothing.
    private static string RarityNote(Rarity? rarity) => rarity switch
    {
        Rarity.Boss => "boss", Rarity.Rare => "rare", Rarity.Magic => "magic", Rarity.Normal => "normal", _ => "none",
    };

    // ---- keys ----

    private static readonly Dictionary<string, Chord> s_chords = new();

    /// <summary>Chord text as a chord, parsed once per text.</summary>
    public static Chord ChordOf(string text)
    {
        if (!s_chords.TryGetValue(text, out var chord))
        {
            s_chords[text] = chord = Chord.TryParse(text, HotkeyKind.Hold, out var parsed) ? parsed : default;
        }
        return chord;
    }

    public static string KeyName(string text) => ChordOf(text) is { IsEmpty: false } chord ? chord.Display : "Not set";

    private static void AddInputKinds()
    {
        // A closed gate (typing, unfocused, a capture) reads "not held", never unknown.
        Add(new ConditionKind("input.held", "input", "Key held")
        {
            Args = { ArgSpec.Key("key") },
            Check = (_, a) =>
            {
                bool held = Hotkeys.Held(ChordOf(a.Text("key")), HotkeyScope.Gameplay);
                return ConditionResult.Of(held, note: held ? "held" : "not held");
            },
            Words = a => $"{KeyName(a.Text("key"))} held",
            FailText = a => $"{KeyName(a.Text("key"))} not held",
        });
        Add(new ConditionKind("input.toggle", "input", "Toggle key")
        {
            Args = { ArgSpec.Key("key") },
            Check = (_, a) =>
            {
                bool on = ConditionToggles.IsOn(a.Text("key"));
                return ConditionResult.Of(on, note: on ? "on" : "off");
            },
            Words = a => $"Toggle {KeyName(a.Text("key"))} on",
            FailText = a => $"toggle {KeyName(a.Text("key"))} off",
        });
    }
}
