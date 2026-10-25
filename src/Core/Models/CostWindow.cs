using UnityEngine;

namespace EpochAssist.Core;

/// <summary>Model fill time per frame over the last second, counting only frames with work.</summary>
internal sealed class CostWindow
{
    private const int Capacity = 2048;
    private readonly long[] _ticks = new long[Capacity];
    private readonly float[] _at = new float[Capacity];
    private int _next;
    private int _frame = -1;
    private long _current;

    /// <summary>Adds time spent in this frame.</summary>
    public void Add(long ticks)
    {
        int frame = Time.frameCount;
        if (frame != _frame) Close(frame);
        _current += ticks;
    }

    public void Reset()
    {
        Array.Clear(_at);
        Array.Clear(_ticks);
        _next = 0;
        _frame = -1;
        _current = 0;
    }

    public double AverageMs => Window(out _);

    public double WorstMs
    {
        get
        {
            Window(out double worst);
            return worst;
        }
    }

    private void Close(int frame)
    {
        if (_frame >= 0)
        {
            _ticks[_next] = _current;
            _at[_next] = Time.unscaledTime;
            _next = (_next + 1) % Capacity;
        }
        _frame = frame;
        _current = 0;
    }

    // The frames closed within the last second, plus this frame's work so far.
    private double Window(out double worstMs)
    {
        float since = Time.unscaledTime - 1f;
        long sum = 0, worst = 0;
        int frames = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (_at[i] <= since || _at[i] == 0f) continue;
            sum += _ticks[i];
            worst = Math.Max(worst, _ticks[i]);
            frames++;
        }
        if (_current > 0)
        {
            sum += _current;
            worst = Math.Max(worst, _current);
            frames++;
        }
        worstMs = FrameTimes.Ms(worst);
        return frames > 0 ? FrameTimes.Ms(sum) / frames : 0d;
    }
}
