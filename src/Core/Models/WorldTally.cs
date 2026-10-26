using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// One owner's enemy counts for several ranges at once: per range, how many alive, active enemies are within it, their
/// weight and the highest rarity, from one pass over the world model. The owner refreshes it a few times a second for
/// the ranges its conditions use; enemy conditions read it. Nothing is allocated after the first refresh.
/// </summary>
internal sealed class WorldTally
{
    /// <summary>Dev builds: every refresh throws, to check what a broken count does (dev cond tallyfail).</summary>
    internal static bool SimulateFailure { get; set; }

    /// <summary>Most distinct ranges one refresh counts.</summary>
    public const int MaxRanges = 10;

    private readonly float[] _ranges = new float[MaxRanges];
    private readonly int[] _counts = new int[MaxRanges];
    private readonly float[] _weights = new float[MaxRanges];
    private readonly int[] _maxRarity = new int[MaxRanges];
    private int _rangeCount;

    /// <summary>Unscaled time of the last refresh that ran; negative before the first.</summary>
    public float RefreshedAt { get; private set; } = -1f;
    /// <summary>Why the last refresh threw, or null.</summary>
    public string? Failed { get; private set; }
    public int RangeCount => _rangeCount;

    public float RangeAt(int index) => _ranges[index];

    /// <summary>Counts the given ranges (distinct; past MaxRanges ignored) in one pass, weighing enemies by rarity.</summary>
    public void Refresh(IReadOnlyList<float> ranges, Func<Rarity, float> weight)
    {
        _rangeCount = Math.Min(ranges.Count, MaxRanges);
        for (int r = 0; r < _rangeCount; r++)
        {
            _ranges[r] = ranges[r];
            _counts[r] = 0;
            _weights[r] = 0f;
            _maxRarity[r] = -1;
        }
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
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Kind != WorldKind.Enemy || entry.Gone || entry.Dead || !entry.Active) continue;
                Rarity rarity = entry.Rarity ?? Rarity.Normal;
                float distance = entry.Distance;
                for (int r = 0; r < _rangeCount; r++)
                {
                    if (distance > _ranges[r]) continue;
                    _counts[r]++;
                    _weights[r] += weight(rarity);
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

    /// <summary>The last refresh's numbers for a range; false when that range wasn't counted.</summary>
    public bool TryGet(float range, out int count, out float weight, out Rarity? maxRarity)
    {
        for (int r = 0; r < _rangeCount; r++)
        {
            if (Mathf.Abs(_ranges[r] - range) > 0.01f) continue;
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

    public bool Counts(float range) => TryGet(range, out _, out _, out _);

    public void Clear()
    {
        _rangeCount = 0;
        RefreshedAt = -1f;
        Failed = null;
    }
}
