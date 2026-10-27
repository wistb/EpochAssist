using UnityEngine;

namespace EpochAssist.Core;

/// <summary>A target's answer at a send: the point to aim at and what is there, or nothing qualifying and why.</summary>
internal readonly record struct TargetResult(bool Found, Vector3 Point, WorldEntry? Entry, int Members, float Score, string? Reason, bool Unreadable)
{
    public static TargetResult None(string reason, bool unreadable = false) => new(false, default, null, 0, 0f, reason, unreadable);
}

/// <summary>
/// The target picker (contracts in specs/015-macro-targeting): one steady pick per distinct target in use (TargetKey),
/// so a cast step, a slot macro and a condition with equal targets agree on the enemy. Owners collect the keys they use
/// each enemy scan; Refresh makes one pass over the world's enemies and picks each key from it; Resolve re-checks a pick
/// just before a send. Nearest and hovered are read at the send, not kept. Owned by the Macros feature: cleared on zone
/// change, death and the feature stopping. Nothing is allocated per frame once the pools have grown.
/// </summary>
internal static partial class Targets
{
    public const int MaxKeys = 48;
    // A kept pick may drift this far past its range before it is let go (never used for a new pick).
    private const float KeepMargin = 2f;
    // Picks one refresh may take before the rest wait for the next frames.
    private const double RefreshSliceMs = 0.25;
    // How much better a challenger must be to replace a kept pick.
    private const float HealthMargin = 1.2f, FractionMargin = 0.10f, AreaKeep = 0.8f;

    /// <summary>Enemies left out by each limit, in this order.</summary>
    public static readonly string[] LeftOutNames = { "range", "rarity", "mod", "ailment", "unknown health" };

    /// <summary>One key's pick: the enemy or the spot, since when, what last changed it, and what each limit left out.</summary>
    internal sealed class Pick
    {
        public TargetKey Key;
        public WorldEntry? Entry;
        public Vector3? Spot;
        public int Members;
        public float Score;
        public float Since;
        public string Changed = "first pick";
        public readonly int[] LeftOut = new int[5];
        public bool Unreadable;
        public string? Reason;
        public int LastWanted;
        public float RefreshedAt = float.NegativeInfinity;
        public readonly List<string> Owners = new();

        public bool Has => Entry != null || Spot != null;

        public void Reset(TargetKey key, int scan)
        {
            Key = key;
            Entry = null;
            Spot = null;
            Members = 0;
            Score = 0f;
            Since = Clock.Now;
            Changed = "first pick";
            Array.Clear(LeftOut);
            Unreadable = false;
            Reason = null;
            LastWanted = scan;
            RefreshedAt = float.NegativeInfinity;
            Owners.Clear();
        }
    }

    private static readonly Pick[] s_picks = new Pick[MaxKeys];
    private static int s_count, s_scan, s_scene = int.MinValue, s_next;
    private static bool s_pending;

    // The candidates of the last pass: alive, active enemies within the world's reach.
    private static WorldEntry[] s_entries = new WorldEntry[64];
    private static float[] s_x = new float[64], s_y = new float[64], s_z = new float[64], s_distance = new float[64];
    private static float[] s_health = new float[64], s_max = new float[64], s_fraction = new float[64];
    private static int s_candidates;
    private static float s_heroX, s_heroZ;
    // One key's qualifying candidates (indices) and their area scores.
    private static int[] s_keep = new int[64];
    private static float[] s_ax = new float[64], s_ay = new float[64], s_az = new float[64], s_score = new float[64];

    /// <summary>The owner's per-rarity enemy weight (the Enemies card's weights); set by the feature.</summary>
    public static Func<Rarity, float>? Weight { get; set; }

    /// <summary>Whether owners should name themselves when they collect (the dev verb asked lately).</summary>
    public static bool WantOwners => Time.unscaledTime < s_ownersWantedUntil;
    private static float s_ownersWantedUntil = float.NegativeInfinity;

    /// <summary>Refresh time: the last refresh and the mean of the last 20, in ms; keys dropped past the limit.</summary>
    public static double LastMs { get; private set; }
    public static double MeanMs { get; private set; }
    public static int Dropped { get; private set; }

    /// <summary>Starts an enemy scan's collection.</summary>
    public static void Begin()
    {
        s_scan++;
        CheckScene();
        if (!WantOwners) return;
        for (int i = 0; i < s_count; i++) s_picks[i].Owners.Clear();
    }

    /// <summary>An owner uses a sticky target this scan (owner names only while WantOwners).</summary>
    public static void Collect(TargetKey key, string? owner = null)
    {
        var pick = Find(key) ?? Add(key);
        pick.LastWanted = s_scan;
        if (owner != null && !pick.Owners.Contains(owner)) pick.Owners.Add(owner);
    }

    private static Pick? Find(in TargetKey key)
    {
        for (int i = 0; i < s_count; i++)
        {
            if (s_picks[i].Key.Equals(key)) return s_picks[i];
        }
        return null;
    }

    // A new key's pick; past the limit the least recently wanted one makes room.
    private static Pick Add(in TargetKey key)
    {
        Pick pick;
        if (s_count < MaxKeys)
        {
            pick = s_picks[s_count] ??= new Pick();
            s_count++;
        }
        else
        {
            int oldest = 0;
            for (int i = 1; i < s_count; i++)
            {
                if (s_picks[i].LastWanted < s_picks[oldest].LastWanted) oldest = i;
            }
            pick = s_picks[oldest];
            Dropped++;
            Log.WarnThrottled("targets:full", $"More than {MaxKeys} different targets are in use; the least recently used ones are picked again when asked");
        }
        pick.Reset(key, s_scan);
        return pick;
    }

    /// <summary>Drops every pick (zone change, death, the feature stopping).</summary>
    public static void Clear()
    {
        s_count = 0;
        s_next = 0;
        s_pending = false;
        s_candidates = 0;
        ClearHovered();
    }

    private static void CheckScene()
    {
        int scene = World.SceneHandle;
        if (scene == s_scene) return;
        s_scene = scene;
        Clear();
    }

    /// <summary>
    /// After an enemy scan's collection: drops keys no owner collected in the last 2 scans, makes the candidate pass and
    /// picks each key. Over its time slice the remaining keys go on in the next frames (Continue).
    /// </summary>
    public static void Refresh()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = s_count - 1; i >= 0; i--)
        {
            if (s_scan - s_picks[i].LastWanted < 2) continue;
            // Swap the dropped pick to the end so the pool keeps it for reuse.
            (s_picks[i], s_picks[s_count - 1]) = (s_picks[s_count - 1], s_picks[i]);
            s_count--;
        }
        if (s_count == 0)
        {
            s_pending = false;
            return;
        }
        FillCandidates();
        s_next = 0;
        s_pending = true;
        RunSlice(start);
    }

    /// <summary>Goes on with a refresh spread over frames; nothing to do once it is done.</summary>
    public static void Continue()
    {
        if (!s_pending) return;
        // Positions move every frame: the pass is made again for the keys still to pick.
        FillCandidates();
        RunSlice(System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private static void RunSlice(long start)
    {
        float now = Clock.Now;
        while (s_next < s_count)
        {
            var pick = s_picks[s_next++];
            try
            {
                Repick(pick, now);
            }
            catch (Exception e)
            {
                pick.Entry = null;
                pick.Spot = null;
                pick.Reason = "pick failed";
                Log.ErrorThrottled("targets:refresh", $"Target pick failed: {e.Message}", 30);
            }
            if (FrameTimes.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - start) > RefreshSliceMs) break;
        }
        s_pending = s_next < s_count;
        double ms = FrameTimes.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        LastMs = ms;
        MeanMs = MeanMs * 0.95 + ms * 0.05;
    }

    // One pass over the world's enemies: alive, active and within its reach, with the reads the picks need.
    private static void FillCandidates()
    {
        s_candidates = 0;
        if (Player.Transform is Transform hero)
        {
            var at = hero.position;
            s_heroX = at.x;
            s_heroZ = at.z;
        }
        if (!World.Running) return;
        World.Want(WorldKind.Enemy);
        var entries = World.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!IsLiveEnemy(e) || e.Distance > World.NearRadius) continue;
            if (s_candidates == s_entries.Length) GrowCandidates();
            int c = s_candidates++;
            s_entries[c] = e;
            var p = e.Position;
            s_x[c] = p.x;
            s_y[c] = p.y;
            s_z[c] = p.z;
            s_distance[c] = e.Distance;
            s_health[c] = e.Health ?? float.NaN;
            s_max[c] = e.MaxHealth ?? float.NaN;
            s_fraction[c] = e.HealthFraction ?? float.NaN;
        }
    }

    private static void GrowCandidates()
    {
        int size = s_entries.Length * 2;
        Array.Resize(ref s_entries, size);
        Array.Resize(ref s_x, size);
        Array.Resize(ref s_y, size);
        Array.Resize(ref s_z, size);
        Array.Resize(ref s_distance, size);
        Array.Resize(ref s_health, size);
        Array.Resize(ref s_max, size);
        Array.Resize(ref s_fraction, size);
        Array.Resize(ref s_keep, size);
        Array.Resize(ref s_ax, size);
        Array.Resize(ref s_ay, size);
        Array.Resize(ref s_az, size);
        Array.Resize(ref s_score, size);
    }

    // ---- limits ----

    // Why a key's limit reads can't be made (the mods or ailments group missing), or null.
    private static string? LimitUnreadable(in TargetKey key)
    {
        if (key.WithoutMod != null && !World.Has("monster mods")) return "mods can't be read";
        if (key.WithAilment != null && !World.Has("enemy ailments")) return "ailments can't be read";
        return null;
    }

    // Which limit leaves an enemy out (0 range … 3 ailment), or -1 when it qualifies; range is the key's own or, for a
    // kept pick, with the margin.
    private static int LeftOutBy(in TargetKey key, WorldEntry e, float distance, float range)
    {
        if (distance > range) return 0;
        if ((e.Rarity ?? Rarity.Normal) < key.MinRarity) return 1;
        if (key.WithoutMod is int mod && HasMod(e, mod)) return 2;
        if (key.WithAilment is int ailment && !(World.AilmentStacks(e, ailment) > 0)) return 3;
        return -1;
    }

    private static bool HasMod(WorldEntry e, int mod)
    {
        string? name = GameData.MonsterMod(mod).Name;
        var mods = e.Mods;
        for (int i = 0; i < mods.Count; i++)
        {
            if (mods[i].Key is int key && key == mod || (name != null && mods[i].Name == name)) return true;
        }
        return false;
    }

    // ---- picking ----

    // A key's pick from the candidate pass: keep the old one unless it went or a challenger is clearly better.
    private static void Repick(Pick pick, float now)
    {
        var key = pick.Key;
        pick.RefreshedAt = now;
        Array.Clear(pick.LeftOut);
        pick.Unreadable = false;
        if (LimitUnreadable(key) is { } unreadable)
        {
            Lose(pick, unreadable, now, unreadable: true);
            return;
        }
        if (key.IsArea) RepickArea(pick, now);
        else RepickEnemy(pick, now);
    }

    private static void Lose(Pick pick, string reason, float now, bool unreadable = false)
    {
        if (pick.Entry is { } lost) Change(pick, unreadable ? reason : WhyLost(pick.Key, lost), now);
        else if (pick.Spot != null) Change(pick, unreadable ? reason : "pack gone", now);
        pick.Entry = null;
        pick.Spot = null;
        pick.Members = 0;
        pick.Score = 0f;
        pick.Reason = reason;
        pick.Unreadable = unreadable;
    }

    private static void Change(Pick pick, string why, float now)
    {
        pick.Changed = why;
        pick.Since = now;
    }

    // The single-enemy picks: rarest, most health, highest max health, lowest health %.
    private static void RepickEnemy(Pick pick, float now)
    {
        var key = pick.Key;
        int best = -1, kept = -1;
        bool anyHealth = false, anyQualifies = false;
        for (int c = 0; c < s_candidates; c++)
        {
            var e = s_entries[c];
            bool previous = ReferenceEquals(e, pick.Entry);
            int by = LeftOutBy(key, e, s_distance[c], key.Range);
            if (by >= 0)
            {
                pick.LeftOut[by]++;
                // The kept enemy may stay up to the margin past its range.
                if (previous && by == 0 && LeftOutBy(key, e, s_distance[c], key.Range + KeepMargin) < 0) kept = c;
                continue;
            }
            anyQualifies = true;
            if (previous) kept = c;
            float value = Measure(key.Pick, c);
            if (float.IsNaN(value))
            {
                pick.LeftOut[4]++;
                continue;
            }
            anyHealth = true;
            if (best < 0 || Better(key.Pick, value, s_distance[c], Measure(key.Pick, best), s_distance[best])) best = c;
        }
        if (best < 0)
        {
            // Qualifying enemies without a health read rank last; with no read at all nothing qualifies.
            if (anyQualifies && !anyHealth && key.Pick != TargetPick.Rarest)
            {
                Lose(pick, "health can't be read", now, unreadable: true);
                return;
            }
            Lose(pick, NoneReason(key), now);
            return;
        }
        if (pick.Entry != null && kept >= 0 && !float.IsNaN(Measure(key.Pick, kept)) && !ClearlyBetter(key.Pick, Measure(key.Pick, best), Measure(key.Pick, kept)))
        {
            Set(pick, kept);
            return;
        }
        if (pick.Entry != null)
        {
            string why = kept < 0 ? WhyLost(key, pick.Entry) : key.Pick switch
            {
                TargetPick.Rarest => "higher rarity",
                TargetPick.LowHealth => "10 points lower health",
                _ => "20% more health",
            };
            if (kept >= 0 || !ReferenceEquals(s_entries[best], pick.Entry)) Change(pick, why, now);
        }
        else if (pick.Reason != null)
        {
            Change(pick, "new pick", now);
        }
        Set(pick, best);
    }

    private static void Set(Pick pick, int c)
    {
        pick.Entry = s_entries[c];
        pick.Spot = null;
        pick.Members = 1;
        pick.Score = Measure(pick.Key.Pick, c);
        pick.Reason = null;
    }

    // Why a kept enemy was let go.
    private static string WhyLost(in TargetKey key, WorldEntry e)
    {
        if (e.Gone) return "gone";
        if (e.Dead) return "died";
        if (!e.Active) return "went inactive";
        if (e.Distance > key.Range + KeepMargin) return "left range";
        return "stopped qualifying";
    }

    // The pick's measure for a candidate: rarity tier, health, max health or health fraction; NaN when unread.
    private static float Measure(TargetPick pick, int c) => pick switch
    {
        TargetPick.Rarest => (float)(s_entries[c].Rarity ?? Rarity.Normal),
        TargetPick.MostHealth => s_health[c],
        TargetPick.MaxHealth => s_max[c],
        TargetPick.LowHealth => s_fraction[c],
        _ => 0f,
    };

    // Whether a candidate ranks above the best so far: higher (lower for health %), then nearer.
    private static bool Better(TargetPick pick, float value, float distance, float best, float bestDistance)
    {
        if (pick == TargetPick.LowHealth ? value < best - 1e-6f : value > best + 1e-6f) return true;
        return MathF.Abs(value - best) <= 1e-6f && distance < bestDistance;
    }

    // Whether the best is clearly better than the kept pick: a higher tier, 20% more health, or 10 points lower health %.
    private static bool ClearlyBetter(TargetPick pick, float best, float kept) => pick switch
    {
        TargetPick.Rarest => best > kept + 0.5f,
        TargetPick.LowHealth => best <= kept - FractionMargin,
        _ => best >= kept * HealthMargin && best > kept,
    };

    // "no enemy within 20 m", "none rare or better within 20 m".
    private static string NoneReason(in TargetKey key)
    {
        string within = $"within {ArgSpec.Format(key.Range)} m";
        string rarity = TargetSpec.RarityWords(key.MinRarity);
        if (rarity.Length > 0) return $"none {rarity} {within}";
        return key.WithoutMod != null || key.WithAilment != null ? $"none qualifies {within}" : $"no enemy {within}";
    }

    // The area picks: the spot whose circle holds the most enemy health, weight or count.
    private static void RepickArea(Pick pick, float now)
    {
        var key = pick.Key;
        int n = Qualifying(pick, key.Range);
        if (n == 0)
        {
            Lose(pick, NoneReason(key), now);
            return;
        }
        var best = TargetArea.Best(s_ax, s_ay, s_az, s_score, n, key.Radius, key.Range, s_heroX, s_heroZ);
        if (pick.Spot is Vector3 old)
        {
            var moved = TargetArea.Recentre(s_ax, s_ay, s_az, s_score, n, key.Radius, key.Range, s_heroX, s_heroZ, old.x, old.z);
            // The kept spot follows its pack while it holds at least 80% of the best and anyone at all.
            if (moved.Members > 0 && moved.Score >= best.Score * AreaKeep)
            {
                // Following the pack keeps the spot: "kept for" goes on.
                if ((moved.Centre - old).sqrMagnitude > 0.01f) pick.Changed = "re-centred";
                SetSpot(pick, moved);
                return;
            }
            Change(pick, moved.Members == 0 ? "pack gone" : "better spot", now);
        }
        else if (pick.Reason != null)
        {
            Change(pick, "new pick", now);
        }
        SetSpot(pick, best);
    }

    private static void SetSpot(Pick pick, TargetArea.Spot spot)
    {
        pick.Entry = null;
        pick.Spot = spot.Centre;
        pick.Members = spot.Members;
        pick.Score = spot.Score;
        pick.Reason = null;
    }

    // Fills the area arrays with a key's qualifying candidates and their scores; counts what each limit left out.
    private static int Qualifying(Pick pick, float range)
    {
        var key = pick.Key;
        int n = 0;
        for (int c = 0; c < s_candidates; c++)
        {
            int by = LeftOutBy(key, s_entries[c], s_distance[c], range);
            if (by >= 0)
            {
                pick.LeftOut[by]++;
                continue;
            }
            float score = key.Pick switch
            {
                TargetPick.AreaHealth => float.IsNaN(s_health[c]) ? 0f : s_health[c],
                TargetPick.AreaWeight => Weight?.Invoke(s_entries[c].Rarity ?? Rarity.Normal) ?? 1f,
                _ => 1f,
            };
            if (key.Pick == TargetPick.AreaHealth && float.IsNaN(s_health[c])) pick.LeftOut[4]++;
            s_ax[n] = s_x[c];
            s_ay[n] = s_y[c];
            s_az[n] = s_z[c];
            s_score[n] = score;
            n++;
        }
        return n;
    }

    // ---- at the send ----

    /// <summary>
    /// A target's answer now, just before a send. Cursor gives none (the caller aims at the cursor). Nearest and hovered
    /// are read now with the target's limits. A kept pick is checked again (alive, active, within its range and margin,
    /// its limits) and picked again at once when it fails; an area spot is moved onto its members' current places.
    /// </summary>
    public static TargetResult Resolve(TargetSpec target)
    {
        if (target.Pick == TargetPick.Cursor) return TargetResult.None("aims at the cursor");
        var key = target.Key;
        try
        {
            if (LimitUnreadable(key) is { } unreadable) return TargetResult.None(unreadable, true);
            switch (target.Pick)
            {
                case TargetPick.Nearest:
                    return Nearest(key);
                case TargetPick.Hovered:
                    var hovered = Hovered();
                    if (hovered == null) return TargetResult.None("no hovered enemy");
                    return LeftOutBy(key, hovered, hovered.Distance, key.Range) < 0 ? Enemy(hovered) : TargetResult.None("the hovered enemy doesn't qualify");
            }
            var pick = Find(key);
            if (pick == null || pick.RefreshedAt < 0f)
            {
                // Not collected yet (a target changed while its macro runs): picked now on its own.
                pick ??= Add(key);
                pick.LastWanted = s_scan;
                FillCandidates();
                Repick(pick, Clock.Now);
            }
            else if (!StillGood(pick))
            {
                FillCandidates();
                Repick(pick, Clock.Now);
            }
            if (pick.Entry is { } entry) return Enemy(entry);
            if (pick.Spot is Vector3 spot) return Area(pick, spot);
            return TargetResult.None(pick.Reason ?? "no target", pick.Unreadable);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("targets:resolve", $"Target read failed: {e.Message}", 30);
            return TargetResult.None("target read failed");
        }
    }

    /// <summary>
    /// A condition's read of a target by its key, without picking anything new: a kept pick as the last refresh left it
    /// (false while the key isn't collected and refreshed yet: not counted); nearest and hovered read now.
    /// </summary>
    public static bool Read(in TargetKey key, out TargetResult result)
    {
        result = default;
        try
        {
            if (LimitUnreadable(key) is { } unreadable)
            {
                result = TargetResult.None(unreadable, true);
                return true;
            }
            switch (key.Pick)
            {
                case TargetPick.Cursor:
                    result = TargetResult.None("aims at the cursor");
                    return true;
                case TargetPick.Nearest:
                    result = Nearest(key);
                    return true;
                case TargetPick.Hovered:
                    var hovered = Hovered();
                    result = hovered == null ? TargetResult.None("no hovered enemy")
                        : LeftOutBy(key, hovered, hovered.Distance, key.Range) < 0 ? Enemy(hovered) : TargetResult.None("the hovered enemy doesn't qualify");
                    return true;
            }
            if (Find(key) is not { RefreshedAt: >= 0f } pick) return false;
            if (pick.Entry is { Gone: false, Dead: false } entry) result = Enemy(entry);
            else if (pick.Spot is Vector3 spot) result = new TargetResult(true, spot, null, pick.Members, pick.Score, null, false);
            else result = TargetResult.None(pick.Reason ?? "no target", pick.Unreadable);
            return true;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("targets:read", $"Target read failed: {e.Message}", 30);
            return false;
        }
    }

    /// <summary>The pick a sticky target holds now (for words and the dev verbs), or null when it isn't collected.</summary>
    internal static Pick? PickOf(TargetSpec target) => target.Sticky ? Find(target.Key) : null;

    private static TargetResult Enemy(WorldEntry e) => new(true, e.Position, e, 1, 0f, null, false);

    // A kept pick still fits: its enemy alive, active, in range with the margin and passing the limits.
    private static bool StillGood(Pick pick)
    {
        if (pick.Entry is { } e) return IsLiveEnemy(e) && LeftOutBy(pick.Key, e, e.Distance, pick.Key.Range + KeepMargin) < 0;
        return pick.Spot != null || pick.Reason != null;
    }

    // An area spot at the send: one mean-shift step over its members' current places.
    private static TargetResult Area(Pick pick, Vector3 spot)
    {
        var key = pick.Key;
        FillCandidates();
        int n = Qualifying(pick, key.Range);
        if (n == 0) return TargetResult.None(NoneReason(key));
        var moved = TargetArea.Shift(s_ax, s_ay, s_az, s_score, n, key.Radius, key.Range, s_heroX, s_heroZ, spot.x, spot.z);
        if (moved.Members == 0) return new TargetResult(true, spot, null, pick.Members, pick.Score, null, false);
        return new TargetResult(true, moved.Centre, null, moved.Members, moved.Score, null, false);
    }

    /// <summary>The nearest enemy within the key's range that passes its limits, now.</summary>
    public static TargetResult Nearest(in TargetKey key)
    {
        if (LimitUnreadable(key) is { } unreadable) return TargetResult.None(unreadable, true);
        WorldEntry? nearest = null;
        var entries = World.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!IsLiveEnemy(e) || (nearest != null && e.Distance >= nearest.Distance)) continue;
            if (LeftOutBy(key, e, e.Distance, key.Range) < 0) nearest = e;
        }
        return nearest != null ? Enemy(nearest) : TargetResult.None(NoneReason(key));
    }

    // ---- words ----

    /// <summary>
    /// A target's pick as words beside its row and in `why`: "Draal Lava Keeper (rare, 12 m, 64%)", "7 enemies, 31,200
    /// health, 9 m away", "nothing qualifies: none rare or better within 20 m"; null for at cursor.
    /// </summary>
    public static string? PickWords(TargetSpec target)
    {
        if (target.Pick == TargetPick.Cursor) return null;
        if (!Player.Exists) return "no live data: enter the game to see values";
        TargetResult result;
        if (target.Sticky)
        {
            if (PickOf(target) is not { RefreshedAt: >= 0f } pick) return "not picked yet";
            if (pick.Entry is { } e) return EnemyWords(e);
            if (pick.Spot is Vector3 spot) return SpotWords(target.Pick, pick.Members, pick.Score, spot);
            return $"nothing qualifies: {pick.Reason ?? "no target"}";
        }
        result = Resolve(target);
        return result.Entry is { } entry ? EnemyWords(entry) : $"nothing qualifies: {result.Reason}";
    }

    public static string EnemyWords(WorldEntry e)
    {
        string health = e.HealthFraction is float f ? $", {MathF.Round(f * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture)}%" : string.Empty;
        return $"{e.Name ?? "enemy"} ({RarityName(e.Rarity ?? Rarity.Normal)}, {MathF.Round(e.Distance).ToString(System.Globalization.CultureInfo.InvariantCulture)} m{health})";
    }

    private static string SpotWords(TargetPick pick, int members, float score, Vector3 spot)
    {
        float away = Player.Transform is Transform hero ? Vector3.Distance(hero.position, spot) : 0f;
        string measure = pick switch
        {
            TargetPick.AreaHealth => $", {score.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} health",
            TargetPick.AreaWeight => $", weight {ArgSpec.Format(score)}",
            _ => string.Empty,
        };
        return $"{members} enem{(members == 1 ? "y" : "ies")}{measure}, {MathF.Round(away).ToString(System.Globalization.CultureInfo.InvariantCulture)} m away";
    }

    public static string RarityName(Rarity rarity) => rarity switch { Rarity.Boss => "boss", Rarity.Rare => "rare", Rarity.Magic => "magic", _ => "normal" };

    /// <summary>
    /// A pick for `why` and the dev verbs: "Draal Lava Keeper (rare, 12 m, 64%) (score 2, kept 1.2 s, last change:
    /// higher rarity; left out: 2 mod, 1 range)", or "nothing qualifies: … (left out: …)".
    /// </summary>
    public static string Describe(Pick pick)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string what = pick.Entry is { } e ? EnemyWords(e)
            : pick.Spot is Vector3 spot ? SpotWords(pick.Key.Pick, pick.Members, pick.Score, spot)
            : pick.RefreshedAt < 0f ? "not picked yet" : $"nothing qualifies: {pick.Reason ?? "no target"}";
        var left = new List<string>();
        for (int i = 0; i < pick.LeftOut.Length; i++)
        {
            if (pick.LeftOut[i] > 0) left.Add($"{pick.LeftOut[i]} {LeftOutNames[i]}");
        }
        string score = pick.Has ? $"score {pick.Score.ToString("0.##", inv)}, " : string.Empty;
        Vector3? at = pick.Entry?.Position ?? pick.Spot;
        if (at is Vector3 p) score = $"at {p.x.ToString("F1", inv)},{p.z.ToString("F1", inv)}, {score}";
        return $"{what} ({score}kept {(Clock.Now - pick.Since).ToString("F1", inv)} s, last change: {pick.Changed}{(left.Count > 0 ? "; left out: " + string.Join(", ", left) : string.Empty)})";
    }

    /// <summary>How many targets are in use, and each one's pick by index (debug drawing; no allocation).</summary>
    public static int Count => s_count;
    internal static Pick PickAt(int index) => s_picks[index];

    // ---- dev ----

#if DEV
    /// <summary>
    /// Dev bench: n synthetic enemies in 1–4 packs around the hero for each of 10 seeds, every kept choice picked 20
    /// times (µs per pick), and each area pick's score as a share of the exact best (TargetArea.ExactBest); then the
    /// same on the current world's enemies. The picks in use are untouched (a pick of its own is used).
    /// </summary>
    public static List<string> Bench(int n, float radius)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var choices = new[] { TargetPick.Rarest, TargetPick.MostHealth, TargetPick.MaxHealth, TargetPick.LowHealth, TargetPick.AreaHealth, TargetPick.AreaWeight, TargetPick.AreaCount };
        var times = new Dictionary<TargetPick, List<double>>();
        var shares = new Dictionary<TargetPick, List<float>>();
        var search = new Dictionary<TargetPick, List<double>>();
        foreach (var c in choices)
        {
            times[c] = new List<double>();
            shares[c] = new List<float>();
            search[c] = new List<double>();
        }
        var lines = new List<string>();
        var pick = new Pick();
        for (int seed = 1; seed <= 10; seed++)
        {
            Synthetic(n, seed);
            foreach (var choice in choices)
            {
                var key = new TargetKey(choice, 30f, choice >= TargetPick.AreaHealth ? radius : 0f, Rarity.Normal, null, null);
                pick.Reset(key, s_scan);
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int run = 0; run < 20; run++)
                {
                    pick.Entry = null;
                    pick.Spot = null;
                    Repick(pick, Clock.Now);
                }
                times[choice].Add(FrameTimes.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / 20.0);
                if (!key.IsArea) continue;
                long q0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int count = 0;
                for (int run = 0; run < 20; run++) count = Qualifying(pick, key.Range);
                long q1 = System.Diagnostics.Stopwatch.GetTimestamp();
                TargetArea.Spot best = default;
                for (int run = 0; run < 20; run++) best = TargetArea.Best(s_ax, s_ay, s_az, s_score, count, radius, key.Range, s_heroX, s_heroZ);
                long q2 = System.Diagnostics.Stopwatch.GetTimestamp();
                float exact = TargetArea.ExactBest(s_ax, s_az, s_score, count, radius);
                shares[choice].Add(exact > 0f ? best.Score / exact : 1f);
                search[choice].Add(FrameTimes.Ms(q2 - q1) * 50.0);
            }
        }
        lines.Add($"ok macro targetbench {n} radius {ArgSpec.Format(radius)}: 10 seeds, 1–4 packs, range 30 m");
        foreach (var choice in choices)
        {
            var t = times[choice];
            string share = shares[choice].Count > 0
                ? $" (search alone {search[choice].Average().ToString("F1", inv)} µs mean, {search[choice].Max().ToString("F1", inv)} max), "
                    + $"area score {(shares[choice].Min() * 100f).ToString("F1", inv)}% min, {(shares[choice].Average() * 100f).ToString("F1", inv)}% mean of the exact best"
                : string.Empty;
            lines.Add($"{TargetSpec.PickNames[(int)choice]}: {t.Average().ToString("F1", inv)} µs mean, {t.Max().ToString("F1", inv)} µs max per pick{share}");
        }
        FillCandidates();
        lines.Add($"current world: {s_candidates} live enemies within {ArgSpec.Format(World.NearRadius)} m");
        foreach (var choice in choices)
        {
            var key = new TargetKey(choice, 30f, choice >= TargetPick.AreaHealth ? radius : 0f, Rarity.Normal, null, null);
            pick.Reset(key, s_scan);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int run = 0; run < 20; run++)
            {
                pick.Entry = null;
                pick.Spot = null;
                Repick(pick, Clock.Now);
            }
            double us = FrameTimes.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / 20.0;
            string share = string.Empty;
            if (key.IsArea)
            {
                int count = Qualifying(pick, key.Range);
                float exact = TargetArea.ExactBest(s_ax, s_az, s_score, count, radius);
                share = $", score {(exact > 0f ? pick.Score / exact * 100f : 100f).ToString("F1", inv)}% of the exact best";
            }
            lines.Add($"  {TargetSpec.PickNames[(int)choice]}: {us.ToString("F1", inv)} µs per pick → {Describe(pick)}{share}");
        }
        return lines;
    }

    // n enemies in 1–4 packs within 25 m of the hero (at the origin): rarities mostly normal, health partly spent.
    private static void Synthetic(int n, int seed)
    {
        var random = new System.Random(seed);
        int packs = 1 + random.Next(4);
        var centres = new Vector2[packs];
        for (int p = 0; p < packs; p++) centres[p] = new Vector2((float)(random.NextDouble() * 50 - 25), (float)(random.NextDouble() * 50 - 25));
        s_candidates = 0;
        s_heroX = s_heroZ = 0f;
        for (int i = 0; i < n; i++)
        {
            if (s_candidates == s_entries.Length) GrowCandidates();
            var centre = centres[random.Next(packs)];
            float spread = 2f + (float)random.NextDouble() * 4f;
            var at = centre + new Vector2((float)(random.NextDouble() * 2 - 1) * spread, (float)(random.NextDouble() * 2 - 1) * spread);
            double roll = random.NextDouble();
            var rarity = roll < 0.02 ? Rarity.Boss : roll < 0.1 ? Rarity.Rare : roll < 0.3 ? Rarity.Magic : Rarity.Normal;
            float max = rarity switch { Rarity.Boss => 50000f, Rarity.Rare => 12000f, Rarity.Magic => 4000f, _ => 1500f };
            float fraction = (float)(0.1 + random.NextDouble() * 0.9);
            int c = s_candidates++;
            s_entries[c] = new WorldEntry { Kind = WorldKind.Enemy, Active = true, Rarity = rarity, Position = new Vector3(at.x, 0f, at.y), Distance = at.magnitude };
            s_x[c] = at.x;
            s_y[c] = 0f;
            s_z[c] = at.y;
            s_distance[c] = at.magnitude;
            s_health[c] = max * fraction;
            s_max[c] = max;
            s_fraction[c] = fraction;
        }
    }
#endif

    /// <summary>The picks in use, for the dev verbs (asking also makes owners name themselves for a while).</summary>
    internal static IReadOnlyList<Pick> Snapshot()
    {
        s_ownersWantedUntil = Time.unscaledTime + 5f;
        var picks = new List<Pick>(s_count);
        for (int i = 0; i < s_count; i++) picks.Add(s_picks[i]);
        return picks;
    }
}
