namespace EpochAssist.Core;

/// <summary>
/// A feature's time per frame (every guarded call: update, late update, draws, scene changes). Each frame's total goes
/// into a bucket per second of unscaled time; the last five seconds are kept.
/// </summary>
internal sealed class FrameTimes
{
    private const int Buckets = 5;

    private readonly long[] _sum = new long[Buckets];
    private readonly int[] _frames = new int[Buckets];
    private readonly long[] _worst = new long[Buckets];
    private readonly int[] _second = new int[Buckets];
    private int _latest = -1;

    /// <summary>Stopwatch ticks spent this frame so far.</summary>
    public long Current;

    /// <summary>The average of the last completed second, in milliseconds (for the budget check).</summary>
    public double LastSecondMs { get; private set; }

    /// <summary>Adds the frame to its second; true when that started a new second, so the one before is complete and
    /// its average is LastSecondMs.</summary>
    public bool Close(int second)
    {
        bool completed = false;
        if (_latest >= 0 && second != _latest)
        {
            int last = _latest % Buckets;
            LastSecondMs = _frames[last] > 0 ? Ms(_sum[last]) / _frames[last] : 0d;
            completed = true;
        }
        int i = second % Buckets;
        if (_second[i] != second || _latest < 0)
        {
            _second[i] = second;
            _sum[i] = 0;
            _frames[i] = 0;
            _worst[i] = 0;
        }
        _sum[i] += Current;
        _frames[i]++;
        _worst[i] = Math.Max(_worst[i], Current);
        _latest = second;
        Current = 0;
        return completed;
    }

    public void Reset()
    {
        Array.Clear(_frames);
        _latest = -1;
        Current = 0;
        LastSecondMs = 0d;
    }

    /// <summary>Average over the kept seconds, in milliseconds.</summary>
    public double AverageMs
    {
        get
        {
            long sum = 0;
            int frames = 0;
            for (int i = 0; i < Buckets; i++)
            {
                if (!Kept(i)) continue;
                sum += _sum[i];
                frames += _frames[i];
            }
            return frames > 0 ? Ms(sum) / frames : 0d;
        }
    }

    /// <summary>The slowest single frame of the kept seconds, in milliseconds.</summary>
    public double WorstMs
    {
        get
        {
            long worst = 0;
            for (int i = 0; i < Buckets; i++)
            {
                if (Kept(i)) worst = Math.Max(worst, _worst[i]);
            }
            return Ms(worst);
        }
    }

    private bool Kept(int i) => _latest >= 0 && _frames[i] > 0 && _second[i] > _latest - Buckets;

    public static double Ms(long ticks) => ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;
}
