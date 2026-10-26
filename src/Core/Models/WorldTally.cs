using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// What one enemy count is of: within a range around the hero or the cursor point, each enemy weighed by the owner's
/// weights or the entry's own (NaN = the owner's). Equal keys share a count.
/// </summary>
internal readonly record struct TallyKey(float Range, bool Cursor, float Normal = float.NaN, float Magic = float.NaN, float Rare = float.NaN, float Boss = float.NaN)
{
    public bool OwnWeights => !float.IsNaN(Normal) || !float.IsNaN(Magic) || !float.IsNaN(Rare) || !float.IsNaN(Boss);

    /// <summary>An enemy's weight: the key's own for its rarity, or the owner's where the key has none.</summary>
    public float Weight(Rarity rarity, Func<Rarity, float> owner)
    {
        float own = rarity switch
        {
            Rarity.Boss => Boss,
            Rarity.Rare => Rare,
            Rarity.Magic => Magic,
            _ => Normal,
        };
        return float.IsNaN(own) ? owner(rarity) : own;
    }
}

/// <summary>
/// Which enemy an enemy line is about: the one under the cursor, or the nearest of at least a rarity within a range of
/// the hero. Equal keys share a pick.
/// </summary>
internal readonly record struct SubjectKey(bool Hovered, Rarity MinRarity, float Range);

/// <summary>
/// One owner's enemy counts for several keys at once: per key, how many alive, active enemies are within its range of
/// its centre, their weight and the highest rarity, from one pass over the world model. The owner refreshes it a few
/// times a second for the keys its conditions use; enemy conditions read it. Nothing is allocated after the first
/// refresh.
/// </summary>
internal sealed class WorldTally
{
    /// <summary>Dev builds: every refresh throws, to check what a broken count does (dev cond tallyfail).</summary>
    internal static bool SimulateFailure { get; set; }

    /// <summary>Most distinct keys one refresh counts.</summary>
    public const int MaxRanges = 32;

    private readonly TallyKey[] _keys = new TallyKey[MaxRanges];
    private readonly int[] _counts = new int[MaxRanges];
    private readonly float[] _weights = new float[MaxRanges];
    private readonly int[] _maxRarity = new int[MaxRanges];
    private int _rangeCount;

    /// <summary>Unscaled time of the last refresh that ran; negative before the first.</summary>
    public float RefreshedAt { get; private set; } = -1f;
    /// <summary>Why the last refresh threw, or null.</summary>
    public string? Failed { get; private set; }
    public int RangeCount => _rangeCount;
    /// <summary>The cursor point the last refresh counted around, or null when it had none.</summary>
    public Vector3? CursorPoint { get; private set; }

    public float RangeAt(int index) => _keys[index].Range;
    public TallyKey KeyAt(int index) => _keys[index];

    /// <summary>Counts the given ranges around the hero (distinct; past MaxRanges ignored), weighing enemies by rarity.</summary>
    public void Refresh(IReadOnlyList<float> ranges, Func<Rarity, float> weight)
    {
        s_rangeKeys.Clear();
        for (int i = 0; i < ranges.Count; i++) s_rangeKeys.Add(new TallyKey(ranges[i], false));
        Refresh(s_rangeKeys, weight, null);
    }

    private static readonly List<TallyKey> s_rangeKeys = new();

    /// <summary>
    /// Counts the given keys (distinct; past MaxRanges ignored) in one pass. Keys around the cursor count from the
    /// cursor point given; without one they read as not counted.
    /// </summary>
    public void Refresh(IReadOnlyList<TallyKey> keys, Func<Rarity, float> weight, Vector3? cursor)
    {
        _rangeCount = 0;
        for (int k = 0; k < keys.Count && _rangeCount < MaxRanges; k++)
        {
            if (keys[k].Cursor && cursor == null) continue;
            int r = _rangeCount++;
            _keys[r] = keys[k];
            _counts[r] = 0;
            _weights[r] = 0f;
            _maxRarity[r] = -1;
        }
        CursorPoint = cursor;
        // Without the world's enemy part there is nothing to count: unknown, not zero.
        string? missing = World.Running ? World.MissingGroup("enemies") : "world model";
        if (missing != null)
        {
            Failed = missing + " missing";
            RefreshedAt = Time.unscaledTime;
            return;
        }
        try
        {
            if (SimulateFailure) throw new InvalidOperationException("simulated count failure (dev)");
            World.Want(WorldKind.Enemy);
            var entries = World.Entries;
            Vector3 point = cursor ?? Vector3.zero;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Kind != WorldKind.Enemy || entry.Gone || entry.Dead || !entry.Active) continue;
                Rarity rarity = entry.Rarity ?? Rarity.Normal;
                float distance = entry.Distance;
                float fromCursor = cursor != null ? Vector3.Distance(entry.Position, point) : float.PositiveInfinity;
                for (int r = 0; r < _rangeCount; r++)
                {
                    var key = _keys[r];
                    if ((key.Cursor ? fromCursor : distance) > key.Range) continue;
                    _counts[r]++;
                    _weights[r] += key.Weight(rarity, weight);
                    if ((int)rarity > _maxRarity[r]) _maxRarity[r] = (int)rarity;
                }
            }
            Failed = null;
        }
        catch (Exception e)
        {
            Failed = e.Message;
            Log.ErrorThrottled("tally", $"Enemy count failed: {e.Message}", 30);
        }
        RefreshedAt = Time.unscaledTime;
    }

    // ---- Subjects: the enemy each subject key picks, refreshed with the counts ----

    public const int MaxSubjects = 16;
    private readonly SubjectKey[] _subjectKeys = new SubjectKey[MaxSubjects];
    private readonly WorldEntry?[] _subjects = new WorldEntry?[MaxSubjects];
    private int _subjectCount;
    private const float HoverFallbackMetres = 2f;

    /// <summary>
    /// Picks each subject's enemy: the hovered one is the world entry whose object the cursor hits (or a parent of what
    /// it hits), else the nearest enemy within 2 m of the cursor point (GF-79); the nearest one is World.Nearest.
    /// </summary>
    public void RefreshSubjects(IReadOnlyList<SubjectKey> keys, Vector3? cursor, Transform? hit)
    {
        _subjectCount = 0;
        if (!World.Running) return;
        for (int k = 0; k < keys.Count && _subjectCount < MaxSubjects; k++)
        {
            var key = keys[k];
            int s = _subjectCount++;
            _subjectKeys[s] = key;
            try
            {
                _subjects[s] = key.Hovered ? Hovered(cursor, hit)
                    : World.Nearest(new WorldQuery { Kind = WorldKind.Enemy, Alive = true, Active = true, MinRarity = key.MinRarity, Range = key.Range });
            }
            catch (Exception e)
            {
                _subjects[s] = null;
                Log.ErrorThrottled("tally:subjects", $"Enemy pick failed: {e.Message}", 30);
            }
        }
    }

    private static WorldEntry? Hovered(Vector3? cursor, Transform? hit)
    {
        if (cursor is not Vector3 point) return null;
        var entries = World.Entries;
        WorldEntry? near = null;
        float nearest = HoverFallbackMetres;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Kind != WorldKind.Enemy || entry.Gone || entry.Dead || !entry.Active) continue;
            if (hit != null && IsOrParent(entry.Transform, hit)) return entry;
            float d = Vector3.Distance(entry.Position, point);
            if (d <= nearest)
            {
                nearest = d;
                near = entry;
            }
        }
        return near;
    }

    // Whether the entry's transform is the hit one or a parent of it (colliders sit on child objects).
    private static bool IsOrParent(Transform? entry, Transform hit)
    {
        if (!Game.Alive(entry)) return false;
        var t = hit;
        for (int depth = 0; depth < 6 && Game.Alive(t); depth++, t = t!.parent)
        {
            if (t!.Pointer == entry!.Pointer) return true;
        }
        return false;
    }

    /// <summary>The enemy a subject key picked at the last refresh (null for none, or gone and dead since); false when
    /// the key wasn't picked.</summary>
    public bool TryGetSubject(SubjectKey key, out WorldEntry? entry)
    {
        for (int s = 0; s < _subjectCount; s++)
        {
            var k = _subjectKeys[s];
            if (k.Hovered != key.Hovered || k.MinRarity != key.MinRarity || Mathf.Abs(k.Range - key.Range) > 0.01f) continue;
            entry = _subjects[s] is { Gone: false, Dead: false } picked ? picked : null;
            return true;
        }
        entry = null;
        return false;
    }

    /// <summary>The last refresh's numbers for a range around the hero with the owner's weights; false when not counted.</summary>
    public bool TryGet(float range, out int count, out float weight, out Rarity? maxRarity) =>
        TryGet(new TallyKey(range, false), out count, out weight, out maxRarity);

    /// <summary>The last refresh's numbers for a key; false when that key wasn't counted.</summary>
    public bool TryGet(TallyKey key, out int count, out float weight, out Rarity? maxRarity)
    {
        for (int r = 0; r < _rangeCount; r++)
        {
            var k = _keys[r];
            if (Mathf.Abs(k.Range - key.Range) > 0.01f || k.Cursor != key.Cursor || !SameWeights(k, key)) continue;
            count = _counts[r];
            weight = _weights[r];
            maxRarity = _maxRarity[r] < 0 ? null : (Rarity)_maxRarity[r];
            return true;
        }
        count = 0;
        weight = 0f;
        maxRarity = null;
        return false;
    }

    // NaN (the owner's weight) equals NaN here.
    private static bool SameWeights(TallyKey a, TallyKey b) =>
        a.Normal.Equals(b.Normal) && a.Magic.Equals(b.Magic) && a.Rare.Equals(b.Rare) && a.Boss.Equals(b.Boss);

    public bool Counts(float range) => TryGet(range, out _, out _, out _);

    public void Clear()
    {
        _rangeCount = 0;
        _subjectCount = 0;
        RefreshedAt = -1f;
        Failed = null;
        CursorPoint = null;
    }
}
