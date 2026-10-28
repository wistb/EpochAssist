using EpochAssist.Core;

namespace EpochAssist.Dev;

// Sessions shaped like play, for the dev `sessions fill realistic` and the dashboard's Sample data switch: town and
// echo zones, picks with rewards and corruption, XP, gold earned and spent, drops of every rarity (some shown by the
// filter), deaths with killers, favour, reputation and rank-ups in a guild, memory amber, level-ups and a reload gap.
// Records carry "dev": true. Nothing here reads or writes the game.
internal static class SessionSamples
{
    private static readonly string[] s_rarities = { "Normal", "Normal", "Normal", "Magic", "Magic", "Rare", "Exalted", "Unique", "Set", "Legendary" };
    // As the game names them, from play: monolith modifiers, echo rewards and echo zones (scene, name).
    private static readonly string?[] s_modifiers =
        { "enrages at half health", "Damage And Crit Monolith", "All Resistances Monolith", "Inc Damage And Health Monolith", "Rare Cooldown Monolith", null };
    private static readonly string[] s_rewards = { "Piles of Gold", "Runes", "Glyphs", "Exalted Relics", "Idols", "Rare Rings", "Unique Belts", "Set Item" };
    private static readonly (string Scene, string Name)[] s_echoes =
    {
        ("M_E20", "Waste Pools"), ("M_C20", "Jagged Outskirts"), ("M_M100", "Twisted Mire"), ("M_M200", "Sinking City"), ("M_M460", "Serpent Valley"),
        ("M_M220", "Imperial Plaza"),
    };
    private static readonly (string Killer, string[] Types)[] s_killers = { ("Void Horror", new[] { "void" }), ("Lagon's Tentacle", new[] { "cold", "physical" }), ("Ash Wraith", new[] { "fire" }) };

    /// <summary>One session's records (oldest first) and totals, until more says to stop (given the time and the records
    /// so far); the open zone is closed at the end with a town zone.</summary>
    public static (List<SessionRecord> Records, SessionTotals Totals, double End) Generate(Random random, int startLevel, Func<double, List<SessionRecord>, bool> more)
    {
        var records = new List<SessionRecord>();
        var totals = new SessionTotals { StartLevel = startLevel };
        double t = 0d;
        long xp = 0, gold = 1000, favour = 0, amber = 0, reputation = 0;
        int level = startLevel, rank = random.Next(1, 5), corruption = random.Next(0, 3) * 50;
        void Add(string kind, params (string Name, object? Value)[] fields)
        {
            var map = new Dictionary<string, object> { ["dev"] = true };
            foreach (var (name, value) in fields)
                if (value != null) map[name] = value;
            var record = new SessionRecord(t, kind, map);
            totals.Add(record);
            records.Add(record);
        }
        Add("zone", ("scene", "EoT"), ("kind", "town"), ("monolith", false), ("name", "The End of Time"));
        double rate = 0.6 + random.NextDouble();
        bool gapped = false;
        while (more(t, records))
        {
            t += 20d + random.Next(0, 40);
            corruption += random.Next(0, 3) == 0 ? 10 : 0;
            Add("pick", ("modifier", s_modifiers[random.Next(s_modifiers.Length)]), ("stability", (long)random.Next(10, 60)), ("hex", (long)random.Next(0, 40)),
                ("reward", s_rewards[random.Next(s_rewards.Length)]), ("corruption", (long)corruption));
            var (scene, name) = s_echoes[random.Next(s_echoes.Length)];
            Add("zone", ("scene", scene), ("kind", "echo"), ("monolith", true), ("name", name));
            double end = t + 120d + random.Next(0, 240);
            bool complete = random.Next(0, 6) > 0;
            while (t < end)
            {
                t += 3d + random.Next(0, 6);
                long gain = (long)(random.Next(200, 900) * rate);
                xp += gain;
                Add("xp", ("amount", gain), ("total", xp));
                if (random.Next(0, 4) == 0)
                {
                    int g = random.Next(20, 300);
                    gold += g;
                    Add("gold", ("amount", (long)g), ("total", gold));
                }
                if (random.Next(0, 3) == 0)
                {
                    string rarity = s_rarities[random.Next(s_rarities.Length)];
                    Add("drop", ("name", $"{rarity} item"), ("base", "Base item"), ("rarity", (long)Array.IndexOf(s_rarities, rarity)), ("rarityName", rarity),
                        ("id", (long)random.Next()), ("shown", rarity != "Normal" && random.Next(0, 2) == 0));
                }
                if (random.Next(0, 10) == 0)
                {
                    int f = random.Next(10, 80);
                    favour += f;
                    Add("favour", ("faction", "MerchantsGuild"), ("amount", (long)f), ("total", favour));
                    reputation += f * 4;
                    Add("rep", ("faction", "MerchantsGuild"), ("amount", (long)(f * 4)), ("total", reputation));
                    if (random.Next(0, 30) == 0) Add("rank", ("faction", "MerchantsGuild"), ("rank", (long)++rank));
                }
                if (random.Next(0, 14) == 0)
                {
                    int a = random.Next(5, 60);
                    amber += a;
                    Add("favour", ("faction", SessionStats.AmberFaction), ("amount", (long)a), ("total", amber));
                }
                if (random.Next(0, 300) == 0)
                {
                    var (killer, types) = s_killers[random.Next(s_killers.Length)];
                    Add("died");
                    Add("death", ("killer", killer), ("types", types));
                }
                if (random.Next(0, 250) == 0) Add("level", ("level", (long)++level));
            }
            if (complete) Add("echo", ("scene", scene));
            t += 10d + random.Next(0, 20);
            Add("zone", ("scene", "M_Rest"), ("kind", "town"), ("monolith", false), ("name", "Traveler's Rest"));
            if (random.Next(0, 5) == 0)
            {
                gold -= 500;
                Add("gold", ("amount", -500L), ("total", gold));
            }
            if (random.Next(0, 6) == 0 && amber > 100)
            {
                amber -= 100;
                Add("favour", ("faction", SessionStats.AmberFaction), ("amount", -100L), ("total", amber));
            }
            // One reload part way, as a dev reload leaves.
            if (!gapped && t > 1800d)
            {
                gapped = true;
                double from = t;
                t += 45d;
                Add("gap", ("from", from), ("to", t), ("reason", "reload"));
            }
        }
        return (records, totals, t);
    }

    private static SessionStats? s_stats;

    /// <summary>
    /// The Sample data dashboard: an hour and a half of generated play, made once, with the figures only a live session
    /// has (recent rates, level progress, faction rank progress) filled in, so every tile, chart and list shows.
    /// </summary>
    public static SessionStats Stats()
    {
        if (s_stats != null) return s_stats;
        var random = new Random(23);
        var (records, totals, end) = Generate(random, 72, (t, _) => t < 5400d);
        var header = new SessionHeader
        {
            Id = "sample", Process = "sample", Start = DateTimeOffset.Now.AddSeconds(-end), Character = new SessionCharacter("offline", 1, null, null),
            ModVersion = BuildInfo.Version, Build = Runtime.BuildId, Captured = Sessions.Captured, StartLevel = 72, Test = true,
        };
        var closed = totals.At(end);
        var view = new SessionView(header, records, () => closed, () => "open", () => false);
        var s = SessionStats.For(view);
        s.Live = true;
        s.XpRates = s.XpRates with { Recent = s.XpRates.Session * 1.12 };
        s.GoldRates = s.GoldRates with { Recent = s.GoldRates.Session * 0.9 };
        s.DropRates = s.DropRates with { Recent = s.DropRates.Session * 1.05 };
        s.DeathRates = s.DeathRates with { Recent = 0d };
        s.Level = s.Level with { XpInLevel = 1_240_000, Threshold = 2_000_000, Percent = 62d, TimeToNext = 1450d };
        foreach (var f in s.Factions)
        {
            f.Rank ??= f.IsAmber ? 5 : 4;
            f.Member = true;
            f.PercentToNext = f.IsAmber ? 77d : 55d;
            f.TimeToNext = f.IsAmber ? null : 2100d;
        }
        return s_stats = s;
    }

    /// <summary>Forgets the sample (the dev bridge stops).</summary>
    public static void Reset() => s_stats = null;
}
